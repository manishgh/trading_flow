using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Covers the swing rules kept after the November 2026 catalog narrowing
/// (docs/research/retired-strategy-artifacts-2026-11-07.md): parameter fields that stay for
/// identity hashes must round-trip, and thresholds of removed setups must fail at load time.
/// </summary>
public sealed class KeptSwingRuleTests
{
    // A minimal daily long mean-reversion recipe built from the shared rule records.
    private static StrategyDefinition Strategy() => new("research.test", "test", "test", 1, "1d", "long",
        new EntryRules("mean_reversion_reclaim", 0, 0, 100, "none", "none", false, false,
            false, false, false, false, null, 20, 10),
        new ConfluenceRules(false, "1d", 50, "none"),
        new ExitRules(2, 3, 240, false, 2, 1, false, false, false, 1),
        new ExecutionRules("1d", 10, new ExecutionConfirmationRules(false, 0, "none", "none", "none")),
        new SessionRules("America/New_York", 0, 0, 0));

    // Writes a strategy through the shared YAML writer and reads it back with the strict reader.
    private static StrategyDefinition RoundTrip(StrategyDefinition strategy)
    {
        var path = Path.Combine(Path.GetTempPath(), "tf-kept-rules-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            return new SimpleYamlReader().ReadStrategy(path);
        }
        finally { File.Delete(path); }
    }

    // Kept configs still list step parameters; they must survive a save and reload unchanged.
    [Fact]
    public void SerializedStrategy_PreservesRetainedStepParameters()
    {
        var s = Strategy() with { EntryRules = Strategy().EntryRules with {
            StepPriorMoveLookbackBars = 37, StepConsolidationMinBars = 4,
            StepConsolidationMaxBars = 13, MinStepPriorMovePct = 19, MinStepPriorDeclinePct = 17,
            MaxStepBaseDepthPct = 14, MaxStepBaseVolumeRatio = .7m, MinStepBreakoutVolumeRatio = 1.7m,
            RequireStepHigherLows = true, RequireStepLowerHighsForShort = true,
            RequireStepBollingerContraction = true, StepBollingerWidthRatioMax = .6m }};

        Assert.Equivalent(s, RoundTrip(s), strict: true);
    }

    // A threshold whose evaluator rule was removed would otherwise be ignored and let entries through unfiltered.
    [Theory]
    [InlineData("min_contractions: 3")]
    [InlineData("max_contractions: 4")]
    [InlineData("require_volatility_halving_left_to_right: true")]
    [InlineData("require_volume_dry_up_pre_breakout: true")]
    [InlineData("min_breakout_volume_ratio: 1.5")]
    [InlineData("avwap_proximity_pct: 2")]
    [InlineData("require_pullback_volume_dryup: true")]
    [InlineData("min_bounce_volume_ratio: 1.2")]
    [InlineData("require_ordered_reclaim_pullback: true")]
    [InlineData("enable_short: true")]
    public void RemovedSetupThreshold_FailsWhenLoaded(string rule)
    {
        var source = File.ReadAllText(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("\n  " + rule.Split(':')[0] + ":", source, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "tf-removed-rule-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, source.Replace("entry_rules:\n", "entry_rules:\n  " + rule + "\n", StringComparison.Ordinal));
            var error = Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
            Assert.Contains(rule.StartsWith("enable_short", StringComparison.Ordinal) ? "enable_short" : "removed setup",
                error.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    // The 52-week position uses a full year of completed sessions regardless of the selected setup.
    [Fact]
    public void CreateTradeSignal_Uses252SessionsFor52WeekPosition()
    {
        var start = new DateTimeOffset(2025, 1, 2, 5, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 260)
            .Select(index =>
            {
                var close = index == 8 ? 50m : 100m + (index * 0.04m);
                return new OhlcvBar("TEST", start.AddDays(index), "1d", close, close + 1m, close - 1m, close, 1_000_000m);
            })
            .ToArray();
        var snapshots = new IndicatorEngine().Compute(bars);
        var strategy = Strategy() with { EntryRules = Strategy().EntryRules with { SetupType = "momentum" } };

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Length - 1);

        Assert.NotNull(signal);
        Assert.NotNull(signal!.PriceVs52WeekLowPct);
        Assert.True(signal.PriceVs52WeekLowPct > 100m);
        Assert.InRange(signal.PriceVs52WeekHighPct!.Value, -2m, 0m);
    }
}
