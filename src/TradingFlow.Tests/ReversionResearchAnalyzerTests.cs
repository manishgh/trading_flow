using TradingFlow.Backtesting.Research;
using TradingFlow.Domain.Market;

namespace TradingFlow.Tests;

public sealed class ReversionResearchAnalyzerTests
{
    [Fact]
    public void Analyze_StretchDays_OutperformUnconditionalBaseline()
    {
        // Periodic series 100,101,99,96,102 repeating: every "96" is a two-down-close stretch that
        // bounces to 102 (+6.25%) the next day, while the average next-day move across all bars is ~flat.
        // The stretch cohort must beat the unconditional baseline — that's the whole point of the control.
        var closes = new List<decimal>();
        for (var cycle = 0; cycle < 12; cycle++)
        {
            closes.AddRange(new[] { 100m, 101m, 99m, 96m, 102m });
        }

        var report = new ReversionResearchAnalyzer().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["TEST"] = BuildDailyBars("TEST", closes) },
            new ReversionResearchOptions(SmaTrendPeriod: 3, ConsecutiveDownDays: 2, RsiOversoldThreshold: 0m));

        Assert.True(report.TotalStretchEvents > 0);

        var baseline1d = Horizon(report, "baseline_all_bars", "all", 1);
        var stretch1d = Horizon(report, "any_stretch", "all", 1);

        Assert.True(
            stretch1d.MeanReturnPct > baseline1d.MeanReturnPct + 1m,
            $"stretch +1d {stretch1d.MeanReturnPct}% should clearly beat baseline {baseline1d.MeanReturnPct}%");
        Assert.True(stretch1d.WinRatePct >= 90m);
    }

    [Fact]
    public void Analyze_SkipsSeriesWithoutEnoughHistoryForTheTrendSma()
    {
        var shortSeries = BuildDailyBars("TEST", Enumerable.Range(0, 10).Select(i => 100m + i).ToArray());

        var report = new ReversionResearchAnalyzer().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["TEST"] = shortSeries },
            new ReversionResearchOptions(SmaTrendPeriod: 200));

        Assert.Empty(report.Tickers);
        Assert.Equal(0, report.TotalStretchEvents);
    }

    [Fact]
    public void Analyze_SplitsFreshNewsFromNoIdentifiableNewsUsingOnlyNewsAvailableBySessionClose()
    {
        var closes = new List<decimal>();
        for (var cycle = 0; cycle < 12; cycle++)
        {
            closes.AddRange(new[] { 100m, 101m, 99m, 96m, 102m });
        }

        var bars = BuildDailyBars("TEST", closes);
        var freshNewsBar = bars[8];
        var afterCloseBar = bars[13];
        var catalysts = new[]
        {
            new CatalystEvent(
                "TEST",
                freshNewsBar.Timestamp.AddHours(-1),
                CatalystType.NewsReport,
                "Material contract announced before the close",
                0.4m,
                Provider: "test",
                ReceivedAt: freshNewsBar.Timestamp.AddMinutes(-55)),
            new CatalystEvent(
                "TEST",
                afterCloseBar.Timestamp.AddHours(2),
                CatalystType.NewsReport,
                "News published after the decision cutoff",
                -0.4m,
                Provider: "test",
                ReceivedAt: afterCloseBar.Timestamp.AddMonths(1))
        };

        var report = new ReversionResearchAnalyzer().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["TEST"] = bars },
            new ReversionResearchOptions(
                SmaTrendPeriod: 3,
                ConsecutiveDownDays: 2,
                RsiOversoldThreshold: 0m,
                FreshNewsLookbackHours: 12m),
            new Dictionary<string, IReadOnlyList<CatalystEvent>> { ["TEST"] = catalysts });

        Assert.True(report.NewsConditioningEnabled);
        Assert.Equal(1, report.NewsCoverageTickerCount);
        Assert.Equal(2, report.NewsEventsSupplied);
        Assert.Equal(1, report.NewsEventsWithTrustedReceivedTimestamp);
        Assert.Equal(1, report.NewsEventsUsingPublishedProxy);
        Assert.True(Horizon(report, "any_stretch", "all", 1, "fresh_news").Count > 0);
        Assert.True(Horizon(report, "any_stretch", "all", 1, "no_identifiable_fresh_news").Count > 0);
    }

    [Fact]
    public void Analyze_MissingTickerNewsCoverageIsUnknownRatherThanNoNews()
    {
        var closes = Enumerable.Range(0, 40)
            .Select(index => index % 5 switch
            {
                0 => 100m,
                1 => 101m,
                2 => 99m,
                3 => 96m,
                _ => 102m
            })
            .ToArray();

        var report = new ReversionResearchAnalyzer().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>>
            {
                ["TEST"] = BuildDailyBars("TEST", closes)
            },
            new ReversionResearchOptions(
                SmaTrendPeriod: 3,
                ConsecutiveDownDays: 2,
                RsiOversoldThreshold: 0m));

        Assert.False(report.NewsConditioningEnabled);
        Assert.DoesNotContain(report.Cohorts, cohort =>
            cohort.NewsContext == ReversionNewsContext.NoIdentifiableFreshNews);
        Assert.Contains(report.Cohorts, cohort =>
            cohort.NewsContext == ReversionNewsContext.NewsUnavailable);
    }

    private static ReversionHorizonStat Horizon(
        ReversionResearchReport report,
        string trigger,
        string regime,
        int days,
        string newsContext = "all_news_contexts")
    {
        var cohort = report.Cohorts.Single(c =>
            c.Trigger == trigger &&
            c.Regime == regime &&
            c.NewsContext == newsContext);
        return cohort.Horizons.Single(h => h.Days == days);
    }

    private static IReadOnlyList<OhlcvBar> BuildDailyBars(string ticker, IReadOnlyList<decimal> closes)
    {
        var start = new DateTimeOffset(2025, 1, 2, 21, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < closes.Count; i++)
        {
            var close = closes[i];
            bars.Add(new OhlcvBar(ticker, start.AddDays(i), "1d", close, close, close, close, 1_000_000m));
        }

        return bars;
    }
}
