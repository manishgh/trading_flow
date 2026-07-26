using TradingFlow.Domain.Research;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Tests;

public sealed class ProviderUpdatedDailyNewsStudyTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly EvidenceRowSourceAddress Source = new("observation", Hash);

    [Fact]
    public void PremarketNews_EntersSameOpenAndReportsBenchmarkExcessAfterCosts()
    {
        var report = new ProviderUpdatedDailyNewsStudy().Analyze(
            [News("story-1", "AAPL", Utc(2026, 7, 1, 12, 0))],
            Bars(),
            Sessions(),
            new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 14.2m),
            Partitions());

        var observation = Assert.Single(report.EventObservations);
        var outcome = Assert.Single(report.HorizonReturns);
        Assert.Equal("premarket", observation.TimingBucket);
        Assert.Equal(new DateOnly(2026, 7, 1), observation.ResponseTradeDate);
        Assert.Equal(new DateOnly(2026, 7, 1), observation.EligibleEntryTradeDate);
        Assert.Equal(10m, outcome.GrossReturnPct);
        Assert.Equal(9.858m, outcome.NetReturnPct);
        Assert.Equal(1m, outcome.BenchmarkReturnPct);
        Assert.Equal(8.858m, outcome.NetExcessReturnPct);
        Assert.False(outcome.CensoredByLaterNews);
        Assert.False(report.PromotionEligible);
        var summary = Assert.Single(report.Summaries);
        Assert.Equal(1, summary.Outcomes.UsableCount);
        Assert.Equal(1, summary.Outcomes.CleanCount);
        Assert.Equal(9.858m, summary.Outcomes.UsableMeanNetReturnPct);
        Assert.Equal(9.858m, summary.Outcomes.CleanMeanNetReturnPct);
        Assert.Contains(
            "historical_news_first_seen_unproven",
            report.PromotionBlockers);
    }

    [Fact]
    public void RegularAndPostmarketNews_CannotEnterAtAnEarlierOpen()
    {
        var report = new ProviderUpdatedDailyNewsStudy().Analyze(
            [
                News("regular", "AAPL", Utc(2026, 7, 1, 14, 0)),
                News("postmarket", "MSFT", Utc(2026, 7, 2, 21, 0))
            ],
            Bars(),
            Sessions(),
            new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 0m),
            Partitions());

        var regular = Assert.Single(
            report.EventObservations,
            row => row.ProviderArticleId == "regular");
        var postmarket = Assert.Single(
            report.EventObservations,
            row => row.ProviderArticleId == "postmarket");
        Assert.Equal(new DateOnly(2026, 7, 1), regular.ResponseTradeDate);
        Assert.Equal(new DateOnly(2026, 7, 2), regular.EligibleEntryTradeDate);
        Assert.Equal("regular", regular.TimingBucket);
        Assert.Equal(new DateOnly(2026, 7, 3), postmarket.ResponseTradeDate);
        Assert.Equal(new DateOnly(2026, 7, 3), postmarket.EligibleEntryTradeDate);
        Assert.Equal("postmarket", postmarket.TimingBucket);
    }

    [Fact]
    public void RevisionsAndLaterStories_AreNotCountedAsIndependentCleanOutcomes()
    {
        var first = News("story-1", "AAPL", Utc(2026, 7, 1, 12, 0));
        var revision = News(
            "story-1",
            "AAPL",
            Utc(2026, 7, 1, 17, 0),
            revisionId: "story-1-revision-2",
            headline: "Updated headline");
        var laterStory = News("story-2", "AAPL", Utc(2026, 7, 1, 18, 0));

        var report = new ProviderUpdatedDailyNewsStudy().Analyze(
            [first, revision, laterStory],
            Bars(),
            Sessions(),
            new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 0m),
            Partitions());

        Assert.Equal(2, report.StoryClusters.Count);
        var cluster = Assert.Single(
            report.StoryClusters,
            value => value.ProviderArticleId == "story-1");
        Assert.Equal(2, cluster.RevisionIds.Count);
        var firstOutcome = Assert.Single(
            report.HorizonReturns,
            value => value.StoryId == cluster.StoryId);
        Assert.True(firstOutcome.CensoredByLaterNews);
        Assert.Equal(Utc(2026, 7, 1, 17, 0), firstOutcome.CensoringNewsAtUtc);
        Assert.Equal(2, report.EventObservations.Count);
        Assert.True(report.EventObservations[0].IsIndependentEpisodeStart);
        Assert.False(report.EventObservations[1].IsIndependentEpisodeStart);
        Assert.True(report.EventObservations[1].PriorNewsGapHours < 24m);
    }

    [Fact]
    public void ProviderCreationAvailability_IsRejectedForHistoricalRestStudy()
    {
        var row = News(
            "story-1",
            "AAPL",
            Utc(2026, 7, 1, 12, 0),
            availability:
            NewsAvailabilityEvidence.ProviderTimestampOnly);

        Assert.Throws<ArgumentException>(() =>
            new ProviderUpdatedDailyNewsStudy().Analyze(
                [row],
                Bars(),
                Sessions(),
                new ProviderUpdatedDailyNewsStudyOptions("SPY", [0], 0m),
                Partitions()));
    }

    private static NewsRevisionEvidenceRow News(
        string articleId,
        string symbol,
        DateTimeOffset updatedAtUtc,
        string? revisionId = null,
        string headline = "Headline",
        NewsAvailabilityEvidence availability =
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly) =>
        new(
            1,
            "news-run",
            Hash,
            "tests",
            "alpaca_news",
            availability,
            "alpaca",
            articleId,
            revisionId ?? $"{articleId}-revision-1",
            headline,
            "Summary",
            $"https://example.test/{articleId}",
            [symbol],
            ["company"],
            updatedAtUtc.AddMinutes(-5),
            updatedAtUtc.AddMinutes(-5),
            updatedAtUtc,
            Utc(2026, 7, 6, 0, 0),
            [Source]);

    private static IReadOnlyList<MarketBarEvidenceRow> Bars()
    {
        var rows = new List<MarketBarEvidenceRow>();
        foreach (var date in new[]
                 {
                     new DateOnly(2026, 7, 1),
                     new DateOnly(2026, 7, 2),
                     new DateOnly(2026, 7, 3),
                     new DateOnly(2026, 7, 6)
                 })
        {
            rows.Add(Bar("AAPL", date, 100m, date.Day == 1 ? 110m : 101m));
            rows.Add(Bar("MSFT", date, 300m, 303m));
            rows.Add(Bar("SPY", date, 200m, date.Day == 1 ? 202m : 201m));
        }

        return rows;
    }

    private static MarketBarEvidenceRow Bar(
        string symbol,
        DateOnly date,
        decimal open,
        decimal close)
    {
        var nextDate = date.AddDays(1);
        return new(
            1,
            "bars-run",
            Hash,
            "tests",
            "sip",
            $"security-{symbol.ToLowerInvariant()}",
            symbol,
            Utc(date.Year, date.Month, date.Day, 4, 0),
            Utc(nextDate.Year, nextDate.Month, nextDate.Day, 4, 0),
            "1d",
            EvidenceFixedDecimal.ToPriceUnits(open),
            EvidenceFixedDecimal.ToPriceUnits(Math.Max(open, close)),
            EvidenceFixedDecimal.ToPriceUnits(Math.Min(open, close)),
            EvidenceFixedDecimal.ToPriceUnits(close),
            null,
            1_000_000,
            10_000,
            "raw",
            "USD",
            Utc(date.Year, date.Month, date.Day, 4, 0),
            Utc(2026, 7, 7, 0, 0),
            [Source]);
    }

    private static IReadOnlyList<ExchangeSessionEvidenceRow> Sessions() =>
        new[]
        {
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 2),
            new DateOnly(2026, 7, 3),
            new DateOnly(2026, 7, 6)
        }.Select(Session).ToArray();

    private static ExchangeSessionEvidenceRow Session(DateOnly date)
    {
        var nextDate = date.AddDays(1);
        return new(
            1,
            "calendar-run",
            Hash,
            "tests",
            "alpaca-trading",
            "alpaca",
            "XNYS",
            date,
            Utc(date.Year, date.Month, date.Day, 8, 0),
            Utc(date.Year, date.Month, date.Day, 13, 30),
            Utc(date.Year, date.Month, date.Day, 20, 0),
            Utc(nextDate.Year, nextDate.Month, nextDate.Day, 0, 0),
            false,
            "alpaca_official_market_calendar",
            Utc(2026, 7, 7, 0, 0),
            [Source]);
    }

    private static EvidenceStudyPartitions Partitions() =>
        new(
            new EvidenceStudyWindow(
                "development",
                Utc(2026, 7, 1, 0, 0),
                Utc(2026, 7, 3, 0, 0)),
            new EvidenceStudyWindow(
                "validation",
                Utc(2026, 7, 3, 0, 0),
                Utc(2026, 7, 4, 0, 0)),
            new EvidenceStudyWindow(
                "holdout",
                Utc(2026, 7, 4, 0, 0),
                Utc(2026, 7, 7, 0, 0)));

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
