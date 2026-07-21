using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TradingFlow.Engine.Configuration;

public enum ProductionProfile
{
    Development,
    Paper,
    Live
}

/// <summary>
/// Signals that production configuration is incomplete, unknown, malformed, or outside its binding range.
/// </summary>
public sealed class ProductionConfigurationException : Exception
{
    public ProductionConfigurationException(string parameterName, string reason)
        : base($"Invalid production parameter '{parameterName}': {reason}")
    {
        ParameterName = parameterName;
    }

    public string ParameterName { get; }
}

/// <summary>
/// Immutable, validated production configuration loaded once for a process lifetime.
/// </summary>
public sealed class ProductionConfigurationSnapshot
{
    internal ProductionConfigurationSnapshot(
        ProductionProfile profile,
        ImmutableDictionary<string, object> values,
        string configHash)
    {
        Profile = profile;
        Values = values;
        ConfigHash = configHash;
    }

    public ProductionProfile Profile { get; }

    public IReadOnlyDictionary<string, object> Values { get; }

    public string ConfigHash { get; }

    public T Get<T>(string name)
    {
        if (!Values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"Production parameter '{name}' is not registered.");
        }

        if (value is not T typedValue)
        {
            throw new InvalidOperationException(
                $"Production parameter '{name}' is {value.GetType().Name}, not {typeof(T).Name}.");
        }

        return typedValue;
    }
}

/// <summary>
/// Converts provider-neutral text values into one validated and canonically hashed startup snapshot.
/// </summary>
public sealed class ProductionConfigurationLoader
{
    private readonly ILogger<ProductionConfigurationLoader>? logger;

    public ProductionConfigurationLoader(ILogger<ProductionConfigurationLoader>? logger = null)
    {
        this.logger = logger;
    }

    public ProductionConfigurationSnapshot Load(
        ProductionProfile profile,
        IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        foreach (var name in overrides.Keys)
        {
            if (!ProductionParameterRegistry.ByName.ContainsKey(name))
            {
                throw new ProductionConfigurationException(name, "parameter is not declared in Appendix A");
            }
        }

        var values = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var definition in ProductionParameterRegistry.Definitions)
        {
            object value;
            if (overrides.TryGetValue(definition.Name, out var text))
            {
                value = Parse(definition, text);
            }
            else if (definition.DefaultValue is not null)
            {
                value = definition.DefaultValue;
            }
            else
            {
                throw new ProductionConfigurationException(definition.Name, "a value is required");
            }

            Validate(definition, value, profile);
            values.Add(definition.Name, value);
        }

        var immutableValues = values.ToImmutable();
        var snapshot = new ProductionConfigurationSnapshot(
            profile,
            immutableValues,
            ComputeHash(profile, immutableValues));
        logger?.LogInformation(
            "Loaded immutable production configuration for profile {Profile} with hash {ConfigHash} and {ParameterCount} parameters.",
            profile,
            snapshot.ConfigHash,
            snapshot.Values.Count);
        return snapshot;
    }

    public T ResolveParameter<T>(
        ProductionProfile profile,
        string name,
        string? overrideValue = null)
    {
        if (!ProductionParameterRegistry.ByName.TryGetValue(name, out var definition))
        {
            throw new ProductionConfigurationException(name, "parameter is not declared in Appendix A");
        }

        var value = !String.IsNullOrWhiteSpace(overrideValue)
            ? Parse(definition, overrideValue)
            : definition.DefaultValue
              ?? throw new ProductionConfigurationException(name, "a value is required");
        Validate(definition, value, profile);
        return value is T typed
            ? typed
            : throw new ProductionConfigurationException(
                name,
                $"parameter resolves to {value.GetType().Name}, not {typeof(T).Name}");
    }

    private static object Parse(ProductionParameterDefinition definition, string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new ProductionConfigurationException(definition.Name, "value cannot be empty");
        }

        return definition.Kind switch
        {
            ProductionParameterKind.Integer => ParseInteger(definition.Name, value),
            ProductionParameterKind.Decimal => ParseDecimal(definition.Name, value),
            ProductionParameterKind.Boolean => ParseBoolean(definition.Name, value),
            ProductionParameterKind.String => value,
            ProductionParameterKind.Enumeration => value,
            ProductionParameterKind.TimeOfDay => ParseTime(definition.Name, value),
            ProductionParameterKind.TimeWindow => ParseTimeWindow(definition.Name, value),
            ProductionParameterKind.StringList => ParseStringList(definition.Name, value),
            _ => throw new ProductionConfigurationException(definition.Name, "unsupported parameter type")
        };
    }

    private static int ParseInteger(string name, string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ProductionConfigurationException(name, $"'{value}' is not an integer");
    }

    private static decimal ParseDecimal(string name, string value)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ProductionConfigurationException(name, $"'{value}' is not a decimal number");
    }

    private static bool ParseBoolean(string name, string value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new ProductionConfigurationException(name, $"'{value}' is not true or false");
    }

    private static TimeOnly ParseTime(string name, string value)
    {
        if (TimeOnly.TryParseExact(
                value,
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        throw new ProductionConfigurationException(name, $"'{value}' is not an HH:mm time");
    }

    private static ProductionTimeWindow ParseTimeWindow(string name, string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ProductionConfigurationException(name, $"'{value}' is not an HH:mm-HH:mm window");
        }

        return new ProductionTimeWindow(ParseTime(name, parts[0]), ParseTime(name, parts[1]));
    }

    private static IReadOnlyList<string> ParseStringList(string name, string value)
    {
        var content = value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal)
            ? value[1..^1]
            : value;
        var items = content
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToImmutableArray();
        if (items.IsEmpty)
        {
            throw new ProductionConfigurationException(name, "list cannot be empty");
        }

        return items;
    }

    private static void Validate(
        ProductionParameterDefinition definition,
        object value,
        ProductionProfile profile)
    {
        if (definition.Kind == ProductionParameterKind.Enumeration &&
            (value is not string choice || definition.AllowedValues is null || !definition.AllowedValues.Contains(choice, StringComparer.Ordinal)))
        {
            throw new ProductionConfigurationException(
                definition.Name,
                $"value must be one of: {string.Join(", ", definition.AllowedValues ?? [])}");
        }

        var numericValue = value switch
        {
            int integer => integer,
            decimal number => number,
            TimeOnly time => time.Hour * 60m + time.Minute,
            _ => (decimal?)null
        };
        if (numericValue is not null)
        {
            ValidateRange(definition, numericValue.Value);
        }

        if (value is ProductionTimeWindow window)
        {
            var start = window.Start.Hour * 60m + window.Start.Minute;
            var end = window.End.Hour * 60m + window.End.Minute;
            ValidateRange(definition, start);
            ValidateRange(definition, end);
            if (window.End <= window.Start)
            {
                throw new ProductionConfigurationException(definition.Name, "window end must be after its start");
            }
        }

        if (profile == ProductionProfile.Live && definition.IsLockedInLiveV1 && !Equals(value, definition.DefaultValue))
        {
                throw new ProductionConfigurationException(definition.Name, "value is locked to its Appendix-A default in live v1");
        }

        if (profile != ProductionProfile.Development &&
            definition.Name == "allow_iex_fallback" &&
            value is true)
        {
            throw new ProductionConfigurationException(definition.Name, "IEX fallback is permitted only in the development profile");
        }
    }

    private static void ValidateRange(ProductionParameterDefinition definition, decimal value)
    {
        if (definition.Minimum is not null && value < definition.Minimum)
        {
            throw new ProductionConfigurationException(definition.Name, $"value must be at least {definition.Minimum.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (definition.Maximum is not null && value > definition.Maximum)
        {
            throw new ProductionConfigurationException(definition.Name, $"value must be at most {definition.Maximum.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static string ComputeHash(
        ProductionProfile profile,
        ImmutableDictionary<string, object> values)
    {
        var canonical = new StringBuilder();
        AppendHashPart(canonical, "profile", profile.ToString().ToLowerInvariant());
        foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            AppendHashPart(canonical, item.Key, FormatCanonicalValue(item.Value));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static void AppendHashPart(StringBuilder output, string name, string value)
    {
        output.Append(name.Length).Append(':').Append(name);
        output.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    private static string FormatCanonicalValue(object value) => value switch
    {
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        ProductionTimeWindow window =>
            $"{window.Start.ToString("HH:mm", CultureInfo.InvariantCulture)}-{window.End.ToString("HH:mm", CultureInfo.InvariantCulture)}",
        IEnumerable<string> items => $"[{string.Join(',', items)}]",
        string text => text,
        _ => throw new InvalidOperationException($"Cannot hash production value type {value.GetType().Name}.")
    };
}
