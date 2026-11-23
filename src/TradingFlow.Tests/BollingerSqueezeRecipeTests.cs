using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Bollinger squeeze breakout v1 and the swing switch-over (design docs/design-review/2026-11-18/
/// regime-gate-and-bollinger-squeeze.md, approved with option A): the band-width rank primitive, the frozen recipe values,
/// recipe validation and the catalog state.
/// </summary>
public sealed class BollingerSqueezeRecipeTests
{
    private static readonly string Root = TestRepository.FindRoot();
    private const string RecipePath = "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml";

    // The rank is the share of the look-back widths at or below the previous bar's width; the signal bar is excluded.
    // Widths (upper - lower) / middle x 100 here are 10, 9, ..., 1 for bars 0-9, then a very wide signal bar 10.
    [Fact]
    public void PriorBollingerWidthPercentile_RanksThePreviousBarAmongItsLookback()
    {
        var snapshots = Enumerable.Range(0, 11)
            .Select(index => Snapshot(index, width: index == 10 ? 50m : 10m - index)).ToArray();

        // Previous bar (9) has the narrowest width 1 of the 10 widths of bars 0-9 -> 1 of 10 at or below it.
        Assert.Equal(0.1m, SignalGenerator.PriorBollingerWidthPercentile(snapshots, 10, 10));
        // Look-back 4 (bars 6-9: widths 4, 3, 2, 1) -> still the narrowest -> 1 of 4.
        Assert.Equal(0.25m, SignalGenerator.PriorBollingerWidthPercentile(snapshots, 10, 4));
        // Too little history, an off rule and a missing width all give no rank (the setup then fails closed).
        Assert.Null(SignalGenerator.PriorBollingerWidthPercentile(snapshots, 5, 10));
        Assert.Null(SignalGenerator.PriorBollingerWidthPercentile(snapshots, 10, 0));
        snapshots[7] = snapshots[7] with { BollingerUpper = null };
        Assert.Null(SignalGenerator.PriorBollingerWidthPercentile(snapshots, 10, 10));
    }

    // Every frozen value of the approved recipe, including the enforced volume rule and the rising SPY average.
    [Fact]
    public void Recipe_ParsesTheApprovedFrozenValues()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(Root, RecipePath));

        Assert.Equal(("1d", "long", "bollinger_squeeze_breakout"), (strategy.Timeframe, strategy.Direction, strategy.EntryRules.SetupType));
        Assert.Equal((60, 0.15m, 10), (strategy.EntryRules.BollingerSqueezeLookbackBars, strategy.EntryRules.MaxBollingerWidthPercentile,
            strategy.EntryRules.RecentHighLookbackBars));
        Assert.Equal((1.5m, "hard_gate", 0.75m), (strategy.EntryRules.MinVolumeSpike, strategy.EntryRules.VolumeConfirmationMode,
            strategy.EntryRules.MinCloseLocationValue));
        Assert.Equal("QQQ", strategy.Regime!.NasdaqListingBenchmark);
        Assert.Equal(("SPY", 50, 20, 70), (strategy.Regime!.BenchmarkSymbol, strategy.Regime.SmaPeriod, strategy.Regime.SmaRisingLookbackBars,
            strategy.Regime.RequiredCloses));
        Assert.Equal((1.0m, 2.5m, 10), (strategy.ExitRules.StopAtrMultiple, strategy.ExitRules.TargetRMultiple, strategy.ExitRules.MaxHoldBars));
    }

    // Squeeze fields without the squeeze setup, or the setup without its fields, are refused instead of being ignored.
    [Theory]
    [InlineData("  setup_type: bollinger_squeeze_breakout", "  setup_type: momentum")]
    [InlineData("  max_bollinger_width_percentile: 0.15", "  max_bollinger_width_percentile: 1.5")]
    [InlineData("  bollinger_squeeze_lookback_bars: 60", "  bollinger_squeeze_lookback_bars: 1")]
    [InlineData("  sma_rising_lookback_bars: 20", "  sma_rising_lookback_bars: -1")]
    [InlineData("  nasdaq_listing_benchmark: QQQ", "  nasdaq_listing_benchmark: SPY")]
    public void Recipe_RefusesInconsistentSqueezeOrRegimeFields(string original, string replacement)
    {
        var text = File.ReadAllText(Path.Combine(Root, RecipePath));
        Assert.Contains(original, text);
        var path = Path.Combine(Path.GetTempPath(), "tf-squeeze-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally { File.Delete(path); }
    }

    // The catalog enables the squeeze for research backtests only (never paper/live without authorization), and keeps the
    // three same-day opening-range versions switched off (set aside).
    [Fact]
    public void Catalog_EnablesTheSqueezeForResearchBacktestsAndSetsAsideSameDayOpeningRange()
    {
        var catalog = new StrategyArtifactCatalog(Root, Path.Combine(Root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var artifacts = catalog.GetSnapshot().ExecutableArtifacts;

        var squeeze = Assert.Single(artifacts, artifact => artifact.Identity.StrategyId == "technical.bollinger-squeeze-breakout" && artifact.Identity.SemanticVersion == "1.0.0");
        Assert.Null(catalog.GetDisabledReason(squeeze));
        var selectable = catalog.GetSelectable(TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
        Assert.Contains(selectable, item => item.Identity == squeeze.Identity);
        Assert.Empty(catalog.GetSelectable(TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(catalog.GetSelectable(TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperShadow));
        Assert.Empty(catalog.GetSelectable(TradingFlow.Domain.Strategies.StrategySelectionMode.RunLive));

        Assert.DoesNotContain(artifacts, artifact => artifact.Identity.StrategyId == "technical.opening-range-breakout");
        var retiredCatalog = RetiredStrategyFixtures.Catalog();
        var openingRange = retiredCatalog.GetSnapshot().ExecutableArtifacts
            .Where(artifact => artifact.Identity.StrategyId == "technical.opening-range-breakout").ToArray();
        Assert.Equal(3, openingRange.Length);
        Assert.All(openingRange, artifact => Assert.Contains("Set aside 18 November 2026", retiredCatalog.GetDisabledReason(artifact)));
    }

    // A completed daily snapshot whose Bollinger bands have the given width in percent of the middle band (middle 100).
    private static IndicatorSnapshot Snapshot(int index, decimal width) => new(
        "ABC", new DateTimeOffset(2026, 1, 1, 21, 0, 0, TimeSpan.Zero).AddDays(index), "1d", 100m, 1_000_000m,
        Vwap: null, Rsi: null, Atr: null, Ema20: null, Ema50: null, Ema200: null,
        BollingerMiddle: 100m, BollingerUpper: 100m + width / 2m, BollingerLower: 100m - width / 2m,
        RelativeVolume: null, MacdLine: null, MacdSignal: null, MacdHistogram: null);
}
