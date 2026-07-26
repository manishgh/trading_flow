using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public enum EvidenceNormalizationFailureCode
{
    MalformedPayload = 1,
    ArtifactMismatch = 2,
    ObservationMismatch = 3,
    UnexpectedProvider = 4,
    UnexpectedEndpoint = 5,
    UnexpectedTransportStatus = 6,
    UnexpectedFeed = 7,
    UnexpectedCurrency = 8,
    UnexpectedAsOfDate = 9,
    UnexpectedAdjustment = 10,
    UnexpectedSymbol = 11,
    TimestampOutsideRequestedRange = 12,
    MissingRequiredField = 13,
    InvalidField = 14,
    DuplicateLogicalKey = 15,
    InvalidPaginationShape = 16,
    HistoricalAvailabilityViolation = 17
}

/// <summary>
/// A page-level failure. Callers must quarantine the complete raw observation and publish no rows.
/// </summary>
public sealed class EvidenceNormalizationException : Exception
{
    public EvidenceNormalizationException(
        EvidenceNormalizationFailureCode code,
        string observationId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        ObservationId = observationId;
    }

    public EvidenceNormalizationFailureCode Code { get; }

    public string ObservationId { get; }

    public bool QuarantineRecommended => true;
}

public sealed record EvidenceSecurityIdentity
{
    public EvidenceSecurityIdentity(string securityId, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        SecurityId = securityId.Trim();
        Symbol = symbol.Trim().ToUpperInvariant();
    }

    public string SecurityId { get; }

    public string Symbol { get; }
}

/// <summary>
/// Explicit expectations supplied by the collection plan; provider payloads cannot override them.
/// </summary>
public sealed class AlpacaEvidenceNormalizationContext
{
    public AlpacaEvidenceNormalizationContext(
        int schemaVersion,
        string expectedDataFeed,
        string expectedCurrency,
        DateOnly expectedAsOfDate,
        IReadOnlyCollection<EvidenceSecurityIdentity> securities,
        bool allowEmptySecurities = false)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDataFeed);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrency);
        SchemaVersion = schemaVersion;
        ExpectedDataFeed = expectedDataFeed.Trim().ToLowerInvariant();
        ExpectedCurrency = expectedCurrency.Trim().ToUpperInvariant();
        ExpectedAsOfDate = expectedAsOfDate;

        var identities = (securities ?? throw new ArgumentNullException(nameof(securities)))
            .Select(identity => identity ??
                throw new ArgumentException("Security identities cannot contain null entries.", nameof(securities)))
            .GroupBy(identity => identity.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (identities.Length == 0 && !allowEmptySecurities)
        {
            throw new ArgumentException("At least one security identity is required.", nameof(securities));
        }

        var conflicts = identities
            .Where(group => group.Select(value => value.SecurityId).Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new ArgumentException(
                $"Conflicting security identities were supplied for: {String.Join(", ", conflicts)}.",
                nameof(securities));
        }

        SecuritiesBySymbol = new ReadOnlyDictionary<string, EvidenceSecurityIdentity>(
            identities.ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal));
    }

    public int SchemaVersion { get; }

    public string ExpectedDataFeed { get; }

    public string ExpectedCurrency { get; }

    public DateOnly ExpectedAsOfDate { get; }

    public IReadOnlyDictionary<string, EvidenceSecurityIdentity> SecuritiesBySymbol { get; }
}

internal static class EvidenceNormalizationGuard
{
    public static void ValidateObservation(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        AlpacaEvidenceNormalizationContext context,
        string endpointSuffix,
        bool requireRange)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(context);

        if (!observation.Provider.Equals("alpaca", StringComparison.Ordinal))
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedProvider,
                observation,
                $"Expected provider 'alpaca', received '{observation.Provider}'.");
        }

        if (!observation.Endpoint.EndsWith(endpointSuffix, StringComparison.OrdinalIgnoreCase))
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedEndpoint,
                observation,
                $"Observation endpoint '{observation.Endpoint}' is not '{endpointSuffix}'.");
        }

        if (observation.TransportKind != EvidenceTransportKind.Http ||
            observation.TransportStatusCode is < 200 or >= 300)
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedTransportStatus,
                observation,
                "Normalization requires a successful archived HTTP response.");
        }

        if (!observation.DataFeed.Equals(context.ExpectedDataFeed, StringComparison.Ordinal))
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedFeed,
                observation,
                $"Expected feed '{context.ExpectedDataFeed}', received '{observation.DataFeed}'.");
        }

        if (!observation.Currency.Equals(context.ExpectedCurrency, StringComparison.Ordinal))
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedCurrency,
                observation,
                $"Expected currency '{context.ExpectedCurrency}', received '{observation.Currency}'.");
        }

        if (observation.AsOfDate != context.ExpectedAsOfDate)
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedAsOfDate,
                observation,
                $"Expected as-of date '{context.ExpectedAsOfDate:yyyy-MM-dd}', " +
                $"received '{observation.AsOfDate:yyyy-MM-dd}'.");
        }

        if (requireRange &&
            (observation.RequestedStartUtc is null || observation.RequestedEndUtc is null))
        {
            Fail(
                EvidenceNormalizationFailureCode.ObservationMismatch,
                observation,
                "The source observation is missing its requested UTC range.");
        }

        foreach (var symbol in observation.RequestedSymbols)
        {
            if (!context.SecuritiesBySymbol.ContainsKey(symbol))
            {
                Fail(
                    EvidenceNormalizationFailureCode.UnexpectedSymbol,
                    observation,
                    $"Requested symbol '{symbol}' has no supplied security identity.");
            }
        }

        if (observation.Artifact.Content.ByteLength != rawBytes.Length)
        {
            Fail(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation,
                "Archived bytes do not match the observation byte length.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(rawBytes.Span)).ToLowerInvariant();
        if (!actualHash.Equals(observation.Artifact.Content.Sha256, StringComparison.Ordinal))
        {
            Fail(
                EvidenceNormalizationFailureCode.ArtifactMismatch,
                observation,
                "Archived bytes do not match the observation SHA-256.");
        }
    }

    public static JsonElement RequireRootObject(
        JsonDocument document,
        EvidenceSourceObservation observation)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            Fail(
                EvidenceNormalizationFailureCode.MalformedPayload,
                observation,
                "The Alpaca response root must be a JSON object.");
        }

        return document.RootElement;
    }

    public static void EnsureNoDuplicateJsonProperties(
        JsonElement element,
        EvidenceSourceObservation observation,
        string path = "$")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    Fail(
                        EvidenceNormalizationFailureCode.MalformedPayload,
                        observation,
                        $"Duplicate JSON property '{path}.{property.Name}'.");
                }

                EnsureNoDuplicateJsonProperties(
                    property.Value,
                    observation,
                    $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateJsonProperties(item, observation, $"{path}[{index++}]");
            }
        }
    }

    public static JsonElement RequireProperty(
        JsonElement parent,
        string name,
        JsonValueKind kind,
        EvidenceSourceObservation observation)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            Fail(
                EvidenceNormalizationFailureCode.MissingRequiredField,
                observation,
                $"Required property '{name}' is missing.");
        }

        if (value.ValueKind != kind)
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be {kind}, not {value.ValueKind}.");
        }

        return value;
    }

    public static string RequireString(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        var value = RequireProperty(parent, name, JsonValueKind.String, observation).GetString();
        if (String.IsNullOrWhiteSpace(value))
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' cannot be empty.");
        }

        return value.Trim();
    }

    public static DateTimeOffset RequireUtcTimestamp(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        var text = RequireString(parent, name, observation);
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a valid UTC timestamp.");
        }

        return timestamp;
    }

    public static decimal RequireDecimal(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        var element = RequireProperty(parent, name, JsonValueKind.Number, observation);
        if (!element.TryGetDecimal(out var value))
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' is outside the supported decimal range.");
        }

        return value;
    }

    public static long RequireInt64(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        var element = RequireProperty(parent, name, JsonValueKind.Number, observation);
        if (!element.TryGetInt64(out var value))
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Property '{name}' must be a 64-bit integer.");
        }

        return value;
    }

    public static void ValidatePagination(
        JsonElement root,
        EvidenceSourceObservation observation)
    {
        if (!root.TryGetProperty("next_page_token", out var token) ||
            token.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            token.ValueKind == JsonValueKind.String && String.IsNullOrWhiteSpace(token.GetString()))
        {
            Fail(
                EvidenceNormalizationFailureCode.InvalidPaginationShape,
                observation,
                "'next_page_token' must be either null or a non-empty string.");
        }
    }

    public static void EnsureTimestampInRange(
        DateTimeOffset timestamp,
        EvidenceSourceObservation observation,
        string fieldName)
    {
        if (!observation.RequestedStartUtc.HasValue ||
            !observation.RequestedEndUtc.HasValue)
        {
            Fail(
                EvidenceNormalizationFailureCode.ObservationMismatch,
                observation,
                "The source observation is missing its requested UTC range.");
        }

        var start = observation.RequestedStartUtc!.Value;
        var end = observation.RequestedEndUtc!.Value;
        if (timestamp < start || timestamp >= end)
        {
            Fail(
                EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange,
                observation,
                $"Timestamp '{fieldName}' ({timestamp:O}) is outside [{start:O}, {end:O}).");
        }
    }

    public static EvidenceSecurityIdentity RequireExpectedSymbol(
        string rawSymbol,
        EvidenceSourceObservation observation,
        AlpacaEvidenceNormalizationContext context,
        bool requireRequestedSymbol)
    {
        var symbol = rawSymbol.Trim().ToUpperInvariant();
        if (!context.SecuritiesBySymbol.TryGetValue(symbol, out var identity) ||
            requireRequestedSymbol && !observation.RequestedSymbols.Contains(symbol, StringComparer.Ordinal))
        {
            Fail(
                EvidenceNormalizationFailureCode.UnexpectedSymbol,
                observation,
                $"Unexpected symbol '{symbol}'.");
        }

        return identity;
    }

    public static EvidenceRowSourceAddress Source(EvidenceSourceObservation observation) =>
        new(observation.ObservationId, observation.Artifact.Content.Sha256);

    public static EvidenceNormalizationException Wrap(
        EvidenceSourceObservation observation,
        Exception exception)
    {
        if (exception is EvidenceNormalizationException normalizationException)
        {
            return normalizationException;
        }

        var code = exception is JsonException
            ? EvidenceNormalizationFailureCode.MalformedPayload
            : EvidenceNormalizationFailureCode.InvalidField;
        return new EvidenceNormalizationException(
            code,
            observation.ObservationId,
            $"Observation '{observation.ObservationId}' could not be normalized: {exception.Message}",
            exception);
    }

    [DoesNotReturn]
    public static void Fail(
        EvidenceNormalizationFailureCode code,
        EvidenceSourceObservation observation,
        string message) =>
        throw new EvidenceNormalizationException(code, observation.ObservationId, message);
}
