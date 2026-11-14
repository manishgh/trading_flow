using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class BacktestBiasRiskTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);

    // Retain the frozen wishlist identity and describe a daily eligibility rule separately from the list's source.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WishlistSnapshot_UsesResolvedNameAndObservedDate(bool dailyEligibility)
    {
        var run = ReadProfile() with
        {
            Universe = UniverseConfig.StaticUniverse with
            {
                Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "wishlist", WishlistName = "Saved prior close list",
                ResolvedAtUtc = Observed, DailyEligibility = dailyEligibility ? SyntheticScreen.Rules : null
            }
        };
        var report = BacktestValidator.BuildBiasRisk(run, MarketPriceBasis.FromBars([Bar("1d", "raw")], null));
        Assert.Equal("wishlist", report.UniverseSource);
        Assert.Equal("Saved prior close list", report.UniverseName);
        Assert.Equal(new DateOnly(2026, 10, 1), report.UniverseAsOfDate);
        Assert.Equal(dailyEligibility ? "daily_eligibility" : null, report.UniverseSelectionMethod);
        Assert.Equal("raw", report.PriceAdjustmentPolicy);
        Assert.Contains(report.Warnings, warning => warning.Contains("raw daily bars", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("sub-daily", StringComparison.Ordinal));
    }

    // Historical filtering keeps the configured source of its candidates even on an already prepared run.
    [Theory]
    [InlineData("static", "per_run", "historical_screener")]
    [InlineData("finviz", "per_run", "historical_screener/finviz")]
    [InlineData("both", "per_day", "historical_screener/curated+finviz/per_day")]
    public void HistoricalScreen_UsesTheCandidateSourceAndCadence(string candidateSource, string cadence, string expected)
    {
        var run = ReadProfile() with
        {
            Universe = UniverseConfig.StaticUniverse with
            {
                Mode = UniverseConfig.HistoricalScreenerMode, CandidateSource = candidateSource, RescreenFrequency = cadence
            }
        };
        var report = BacktestValidator.BuildBiasRisk(run, MarketPriceBasis.FromBars([Bar("1d", "raw")], null));
        Assert.Equal(expected, report.UniverseSource);
        Assert.True(report.SurvivorshipBiasWarning);
    }

    // Unknown observations use the provider contract when it supplies a basis, and otherwise remain explicitly unknown.
    [Fact]
    public void PriceBasis_UsesProviderFallbackForUnspecifiedBarsAndExplicitLabelsWhenPresent()
    {
        var source = new MarketDataProvenance("pinned-fixture", "sip", "raw");
        var run = ReadProfile();
        var fallback = BacktestValidator.BuildBiasRisk(run, MarketPriceBasis.FromBars([Bar("1h", "unspecified")], source));
        Assert.Equal("raw", fallback.PriceAdjustmentPolicy);
        Assert.Contains(fallback.Warnings, warning => warning.Contains("raw sub-daily", StringComparison.Ordinal));

        var known = BacktestValidator.BuildBiasRisk(run, MarketPriceBasis.FromBars([Bar("1d", "split")], source));
        Assert.Equal("split", known.PriceAdjustmentPolicy);
        var unknown = BacktestValidator.BuildBiasRisk(run, MarketPriceBasis.FromBars([Bar("1d", "unspecified")], null));
        Assert.Equal("unknown", unknown.PriceAdjustmentPolicy);
        Assert.Contains(unknown.Warnings, warning => warning.Contains("basis is unavailable", StringComparison.Ordinal));
    }

    // Use the existing run shape with its advisory config labels, which should not override observed evidence.
    private static BacktestRunConfig ReadProfile() => new SimpleYamlReader().ReadBacktestRun(
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));

    // Only the timeframe and adjustment fields differ between these otherwise equivalent fixture candles.
    private static OhlcvBar Bar(string timeframe, string adjustment) =>
        new("ABC", Observed, timeframe, 10m, 10m, 10m, 10m, 1_000m, "sip", adjustment);
}
