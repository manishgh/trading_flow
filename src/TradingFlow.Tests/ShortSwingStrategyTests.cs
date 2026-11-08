using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// The two swing short strategies approved on 8 November 2026 (swing.connors-rsi2-short@2.0.0 and
/// swing.snapback-short@2.0.0): their settings files, entry gates and run-snapshot round trip. They are
/// separate strategies so the long versions keep their identities unchanged.
/// </summary>
public sealed class ShortSwingStrategyTests
{
    private const string ConnorsShortFile = "swing-connors-rsi2-short.bt-v2.yaml";
    private const string SnapbackShortFile = "swing-snapback-short.bt-v2.yaml";

    private static StrategyDefinition Load(string name) => new SimpleYamlReader().ReadStrategy(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", name));

    // Build daily bars from closes (high/low one dollar around the close) and evaluate the last completed bar.
    private static (TradeSignal Signal, string? Rejection) EvaluateLastBar(StrategyDefinition strategy, IReadOnlyList<decimal> closes)
    {
        var start = new DateTimeOffset(2025, 1, 2, 21, 0, 0, TimeSpan.Zero);
        var bars = closes.Select((close, index) =>
            new OhlcvBar("TEST", start.AddDays(index), "1d", close, close + 1m, close - 1m, close, 1_000_000m)).ToArray();
        var snapshots = new IndicatorEngine().Compute(bars);
        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, bars.Length - 1)!;
        return (signal, new BasicStrategyEvaluator().GetShortEntryRejection(strategy, signal, 1m));
    }

    // Both files load as single-direction shorts with no stop, no target, and the close-below-SMA5 cover exit.
    [Fact]
    public void ShortFiles_DeclareTheirPublishedRules()
    {
        var connors = Load(ConnorsShortFile);
        var snapback = Load(SnapbackShortFile);

        Assert.All(new[] { connors, snapback }, strategy =>
        {
            Assert.Equal("short", strategy.Direction);
            Assert.True(strategy.EntryRules.EnableShort);
            Assert.False(strategy.ExitRules.HasProtectiveStop);
            Assert.False(strategy.ExitRules.HasProfitTarget);
            Assert.True(strategy.ExitRules.ExitOnCloseBelowSma5);
            Assert.False(strategy.ExitRules.ExitOnCloseAboveSma5);
            Assert.True(strategy.ExitRules.CheckExitsFromFillDay);
            Assert.Equal("rsi2_descending", strategy.EntryRules.PortfolioRankMode);
        });
        Assert.Equal("rsi2_overbought", connors.EntryRules.ShortSetupType);
        Assert.Equal(95m, connors.EntryRules.MinShortReversionRsi2);
        Assert.True(connors.EntryRules.RequirePriceBelowSma200ForShort);
        Assert.Null(connors.ExitRules.MaxHoldBars);
        Assert.Null(connors.EntryRules.HistoricalNewsDiagnostic);
        Assert.Equal("momentum", snapback.EntryRules.ShortSetupType);
        Assert.Equal(70m, snapback.EntryRules.MinShortEntryRsi);
        Assert.Equal((10m, 20), (snapback.EntryRules.MinCloseAboveSmaPct!.Value, snapback.EntryRules.CloseAboveSmaPeriod!.Value));
        Assert.False(snapback.EntryRules.RequirePriceBelowSma200ForShort);
        Assert.NotNull(snapback.EntryRules.HistoricalNewsDiagnostic);
        Assert.Equal(5, snapback.ExitRules.MaxHoldBars);
    }

    // Connors short fires on RSI(2) above 95 only while the close is below SMA200: a sharp two-day bounce inside a long
    // decline qualifies; the same two-day rise at the top of a long advance does not.
    [Fact]
    public void ConnorsShort_TriggersOnlyBelowSma200WithRsi2Above95()
    {
        var strategy = Load(ConnorsShortFile);
        var decline = Enumerable.Range(0, 258).Select(index => 200m - index * 0.4m).Concat(new[] { 107m, 117m }).ToArray();
        var advance = Enumerable.Range(0, 258).Select(index => 100m + index * 0.4m).Concat(new[] { 205m, 215m }).ToArray();

        var (bounceSignal, bounceRejection) = EvaluateLastBar(strategy, decline);
        var (topSignal, topRejection) = EvaluateLastBar(strategy, advance);

        Assert.True(bounceSignal.CurrentRsi2 > 95m);
        Assert.True(bounceSignal.IsConnorsRsi2Overbought);
        Assert.Null(bounceRejection);
        Assert.True(topSignal.CurrentRsi2 > 95m);
        Assert.False(topSignal.IsConnorsRsi2Overbought);
        Assert.Equal("setup_rsi2_overbought_not_triggered", topRejection);
    }

    // Snapback short needs RSI(14) of at least 70 and the completed close at least 10% above SMA20.
    [Fact]
    public void SnapbackShort_RequiresTheStretchAboveSma20()
    {
        var strategy = Load(SnapbackShortFile);
        var flat = Enumerable.Repeat(100m, 259);

        var (spikeSignal, spikeRejection) = EvaluateLastBar(strategy, flat.Append(125m).ToArray());
        var (smallSignal, smallRejection) = EvaluateLastBar(strategy, flat.Append(105m).ToArray());

        var sma20 = (19 * 100m + 125m) / 20m;
        Assert.Equal((125m - sma20) / sma20 * 100m, spikeSignal.CloseAboveSmaPct);
        Assert.True(spikeSignal.CurrentRsi >= 70m);
        Assert.Null(spikeRejection);
        Assert.True(smallSignal.CurrentRsi >= 70m);
        Assert.StartsWith("close_not_far_enough_above_sma20", smallRejection, StringComparison.Ordinal);
    }

    // Short settings that would silently never fire, or a cover exit on a long, fail when the file is loaded.
    [Theory]
    [InlineData(ConnorsShortFile, "  min_short_reversion_rsi2: 95.0\n", "")]
    [InlineData(ConnorsShortFile, "  min_short_reversion_rsi2: 95.0", "  min_short_reversion_rsi2: 100")]
    [InlineData(ConnorsShortFile, "direction: short", "direction: long")]
    [InlineData(SnapbackShortFile, "  close_above_sma_period: 20\n", "")]
    public void InvalidShortSettings_FailWhenLoaded(string file, string original, string replacement)
    {
        var source = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", file))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(original, source, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "tf-short-rule-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, source.Replace(original, replacement, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally { File.Delete(path); }
    }

    // The fill-day exit check is refused with a protective stop: that stop is placed at the fill bar's close and cannot also
    // be judged on that bar.
    [Fact]
    public void FillDayCheck_WithAStop_FailsWhenLoaded()
    {
        var source = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", SnapbackShortFile))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("  initial_stop_mode: none", "  initial_stop_mode: atr", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "tf-fill-day-stop-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, source);
            var error = Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
            Assert.Contains("check_exits_from_fill_day requires initial_stop_mode: none", error.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    // Generated run snapshots keep every short rule exactly, so a saved run replays the same strategy.
    [Theory]
    [InlineData(ConnorsShortFile)]
    [InlineData(SnapbackShortFile)]
    public void ShortSnapshots_RoundTripThroughTheRunWriter(string file)
    {
        var original = Load(file);
        var path = Path.Combine(Path.GetTempPath(), "tf-short-roundtrip-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, TradingFlow.Web.Services.RunConfigWriter.SerializeStrategyYaml(original));
            Assert.Equivalent(original, new SimpleYamlReader().ReadStrategy(path), strict: true);
        }
        finally { File.Delete(path); }
    }
}
