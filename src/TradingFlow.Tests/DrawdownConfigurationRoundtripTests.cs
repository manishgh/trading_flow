using System.Text.Json;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class DrawdownConfigurationRoundtripTests
{
    [Theory]
    [InlineData("observe", null, null, null, false, false, 0)]
    [InlineData("gate", "predecline_atr_depth", "1.25", "3.5", false, false, 0)]
    [InlineData("gate", null, null, null, true, true, 4)]
    public void SnapshotRoundtripPreservesExplicitQualificationContract(string mode, string? metric,
        string? minimum, string? maximum, bool sector, bool events, int history)
    {
        decimal? Parse(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var rules = new DrawdownQualificationRules(mode, metric, Parse(minimum), Parse(maximum), sector, events, history);
        var strategy = Strategy() with { EntryRules = Strategy().EntryRules with { DrawdownQualification = rules } };
        var path = Path.Combine(Path.GetTempPath(), "tf-drawdown-roundtrip-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            var actual = new SimpleYamlReader().ReadStrategy(path);
            Assert.Equal(rules, actual.EntryRules.DrawdownQualification);
            Assert.Equivalent(strategy, actual, strict: true);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AbsentQualificationDoesNotChangeLegacySerializedEntryIdentity()
    {
        var strategy = Strategy();
        Assert.DoesNotContain("DrawdownQualification", JsonSerializer.Serialize(strategy));
        Assert.DoesNotContain("drawdown_qualification", RunConfigWriter.SerializeStrategyYaml(strategy));
    }

    private static StrategyDefinition Strategy() => new("research.drawdown.roundtrip", "Roundtrip", "test", 1, "1d", "long",
        new EntryRules("rsi2_oversold", 0, 0, 100, "none", "none", false, false,
            false, false, false, false, null, 20, 10),
        new ConfluenceRules(false, "1d", 50, "none"),
        new ExitRules(2, 3, 240, false, 2, 1, false, false, false, 1),
        new ExecutionRules("1d", 10, new ExecutionConfirmationRules(false, 0, "none", "none", "none")),
        new SessionRules("America/New_York", 0, 0, 0));
}
