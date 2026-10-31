using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class SwingFamilyConfigurationTests
{
    private static StrategyDefinition Strategy() => new("research.test", "test", "test", 1, "1d", "long",
        new EntryRules("swing_reclaim", 0, 0, 100, "none", "none", false, false,
            false, false, false, false, null, 20, 10,
            ReclaimLookbackBars: 5, MinReclaimPullbackDepthPct: 8, MaxReclaimPullbackDepthPct: 35,
            RequireReclaimLowAboveSma50: false, RequireReclaimCloseAboveSma10: false,
            RequireReclaimCloseAboveSma20: false, RequireOrderedReclaimPullback: true),
        new ConfluenceRules(false, "1d", 50, "none"),
        new ExitRules(2, 3, 240, false, 2, 1, false, false, false, 1),
        new ExecutionRules("1d", 10, new ExecutionConfirmationRules(false, 0, "none", "none", "none")), new SessionRules("America/New_York", 0, 0, 0));

    private static OhlcvBar[] Bars(params (decimal High, decimal Low)[] tail)
    {
        var start = new DateTimeOffset(2025, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 260).Select(i => new OhlcvBar("TEST", start.AddDays(i), "1d",
            100, 101, 99, 100, 1000000)).ToList();
        foreach (var (high, low) in tail)
        {
            var mid = (high + low) / 2;
            rows.Add(new("TEST", start.AddDays(rows.Count), "1d", mid, high, low, mid, 1000000));
        }
        return rows.ToArray();
    }

    [Fact]
    public void OrderedReclaim_RejectsLowBeforePeak_WhileLegacyRangeRuleRemainsExplicit()
    {
        var bars = Bars((105, 80), (110, 103), (115, 108), (118, 112), (120, 114), (125, 116));
        var snapshots = new IndicatorEngine().Compute(bars);
        var generator = new SignalGenerator();
        Assert.False(generator.CreateTradeSignal(Strategy(), bars, snapshots, 265)!.IsSwingReclaim);
        var legacy = Strategy() with { EntryRules = Strategy().EntryRules with { RequireOrderedReclaimPullback = false } };
        Assert.True(generator.CreateTradeSignal(legacy, bars, snapshots, 265)!.IsSwingReclaim);
        Assert.DoesNotContain("RequireOrderedReclaimPullback", JsonSerializer.Serialize(legacy.EntryRules));
    }

    [Fact]
    public void OrderedReclaim_UsesOnlyPostPeakLow_AndIgnoresFutureBars()
    {
        var bars = Bars((105, 70), (120, 100), (112, 103), (110, 96), (112, 99), (125, 90));
        var generator = new SignalGenerator();
        var signal = generator.CreateTradeSignal(Strategy(), bars, new IndicatorEngine().Compute(bars), 265)!;
        Assert.True(signal.IsSwingReclaim);
        Assert.Equal(96m, signal.ReclaimPullbackLow);
        Assert.Equal(20m, signal.ReclaimPullbackDepthPct);
        var extended = bars.Append(new OhlcvBar("TEST", bars[^1].Timestamp.AddDays(1), "1d", 500, 1000, 1, 500, 99999999)).ToArray();
        var replay = generator.CreateTradeSignal(Strategy(), extended, new IndicatorEngine().Compute(extended), 265)!;
        Assert.Equal(signal.IsSwingReclaim, replay.IsSwingReclaim);
        Assert.Equal(signal.ReclaimPullbackLow, replay.ReclaimPullbackLow);
        Assert.Equal(signal.ReclaimPullbackDepthPct, replay.ReclaimPullbackDepthPct);
    }

    [Fact]
    public void OrderedReclaim_UsesLatestTiedPeak_AndDoesNotAssumeSameBarOrdering()
    {
        var bars = Bars((120, 100), (110, 90), (112, 99), (115, 100), (120, 80), (125, 110));
        Assert.False(new SignalGenerator().CreateTradeSignal(Strategy(), bars, new IndicatorEngine().Compute(bars), 265)!.IsSwingReclaim);
    }

    [Theory]
    [InlineData("step_breakout")]
    [InlineData("swing_reclaim")]
    public void SerializedStrategies_PreserveNonDefaultBaseRulesAndOrderedPullback(string setup)
    {
        var s = Strategy() with { EntryRules = Strategy().EntryRules with {
            SetupType = setup, StepPriorMoveLookbackBars = 37, StepConsolidationMinBars = 4,
            StepConsolidationMaxBars = 13, MinStepPriorMovePct = 19, MinStepPriorDeclinePct = 17,
            MaxStepBaseDepthPct = 14, MaxStepBaseVolumeRatio = .7m, MinStepBreakoutVolumeRatio = 1.7m,
            RequireStepHigherLows = true, RequireStepLowerHighsForShort = true,
            RequireStepBollingerContraction = true, StepBollingerWidthRatioMax = .6m }};
        var path = Path.Combine(Path.GetTempPath(), "tf-family-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(s));
            var replay = new SimpleYamlReader().ReadStrategy(path);
            Assert.Equivalent(s, replay, strict: true);
        }
        finally { File.Delete(path); }
    }
}
