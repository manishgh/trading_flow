using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class ProductionParameterRegistryTests
{
    [Fact]
    public void Registry_IsBidirectionallySynchronizedWithBindingSpecAppendixA()
    {
        var expected = ReadAppendixParameterReferences();
        var actual = ProductionParameterRegistry.Definitions
            .ToDictionary(definition => definition.Name, definition => definition.SpecificationReference, StringComparer.Ordinal);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Empty(expected.Keys.Except(actual.Keys, StringComparer.Ordinal));
        Assert.Empty(actual.Keys.Except(expected.Keys, StringComparer.Ordinal));
        foreach (var parameter in expected)
        {
            Assert.Equal(parameter.Value, actual[parameter.Key]);
        }
    }

    [Fact]
    public void Registry_DefinitionsAreUniqueTypedAndInternallyValid()
    {
        var definitions = ProductionParameterRegistry.Definitions;

        Assert.Equal(definitions.Count, definitions.Select(definition => definition.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(definitions.Count, ProductionParameterRegistry.ByName.Count);
        foreach (var definition in definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.Matches("^[a-z][a-z0-9_]*$", definition.Name);
            Assert.Matches("^[A-Z]{3,4}-[0-9]{2}$", definition.SpecificationReference);
            AssertDefaultType(definition);

            if (definition.DefaultValue is int integer)
            {
                AssertInRange(integer, definition);
            }
            else if (definition.DefaultValue is decimal number)
            {
                AssertInRange(number, definition);
            }

            if (definition.Kind == ProductionParameterKind.Enumeration)
            {
                Assert.NotNull(definition.AllowedValues);
                Assert.Contains(Assert.IsType<string>(definition.DefaultValue), definition.AllowedValues!);
            }

            if (definition.IsRequired)
            {
                Assert.Null(definition.DefaultValue);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAppendixParameterReferences()
    {
        var root = TestRepository.FindRoot();
        var specPath = Path.Combine(root, "docs", "spec", "automated_trading_production_spec_v1.md");
        var lines = File.ReadAllLines(specPath);
        var appendixStart = Array.FindIndex(lines, line => line.StartsWith("## Appendix A", StringComparison.Ordinal));
        var appendixEnd = Array.FindIndex(lines, appendixStart + 1, line => line.StartsWith("## Appendix B", StringComparison.Ordinal));
        Assert.True(appendixStart >= 0 && appendixEnd > appendixStart, "Binding-spec Appendix A could not be located.");

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[(appendixStart + 1)..appendixEnd])
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("| Parameter ", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 6 || cells[1].StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in ExpandCompoundName(cells[1]))
            {
                Assert.True(parameters.TryAdd(name, cells[5]), $"Appendix A contains duplicate parameter '{name}'.");
            }
        }

        return parameters;
    }

    private static IReadOnlyList<string> ExpandCompoundName(string appendixName) => appendixName switch
    {
        "rest_budget_per_min (md/trading)" => ["rest_budget_market_data_per_min", "rest_budget_trading_per_min"],
        "finviz_min_rows / finviz_max_rows" => ["finviz_min_rows", "finviz_max_rows"],
        "shutdown_flatten (day/swing)" => ["shutdown_flatten_day", "shutdown_flatten_swing"],
        "per_trade_risk_pct / _swing" => ["per_trade_risk_pct_day", "per_trade_risk_pct_swing"],
        "max_positions_day / _swing" => ["max_positions_day", "max_positions_swing"],
        _ => [appendixName]
    };

    private static void AssertDefaultType(ProductionParameterDefinition definition)
    {
        if (definition.DefaultValue is null)
        {
            Assert.True(definition.IsRequired);
            return;
        }

        var expectedType = definition.Kind switch
        {
            ProductionParameterKind.Integer => typeof(int),
            ProductionParameterKind.Decimal => typeof(decimal),
            ProductionParameterKind.Boolean => typeof(bool),
            ProductionParameterKind.String => typeof(string),
            ProductionParameterKind.Enumeration => typeof(string),
            ProductionParameterKind.TimeOfDay => typeof(TimeOnly),
            ProductionParameterKind.TimeWindow => typeof(ProductionTimeWindow),
            ProductionParameterKind.StringList => typeof(IReadOnlyList<string>),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };

        Assert.IsAssignableFrom(expectedType, definition.DefaultValue);
    }

    private static void AssertInRange(decimal value, ProductionParameterDefinition definition)
    {
        if (definition.Minimum is not null)
        {
            Assert.True(value >= definition.Minimum, $"{definition.Name} default is below its minimum.");
        }

        if (definition.Maximum is not null)
        {
            Assert.True(value <= definition.Maximum, $"{definition.Name} default is above its maximum.");
        }
    }
}
