using TradingFlow.Domain.Market;
using TradingFlow.Engine.Execution;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Tests;

public sealed class CatalystTechnicalEventStudyRunnerTests
{
    [Fact]
    public void Deduplicate_RemovesSameHeadlineInsideWindow()
    {
        var deduper = new CatalystDeduper();
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var catalysts = new[]
        {
            Catalyst("POET", start, "POET wins new AI optical contract"),
            Catalyst("POET", start.AddHours(1), "POET wins new AI optical contract"),
            Catalyst("POET", start.AddHours(7), "POET wins new AI optical contract")
        };

        var deduped = deduper.Deduplicate(catalysts, TimeSpan.FromHours(6));

        Assert.Equal(2, deduped.Count);
        Assert.Equal(start, deduped[0].Timestamp);
        Assert.Equal(start.AddHours(7), deduped[1].Timestamp);
    }

    [Fact]
    public void NoveltyScore_DropsForRepeatedRelatedHeadline()
    {
        var scorer = new CatalystNoveltyScorer();
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var prior = new[]
        {
            Catalyst("RGTI", start, "Rigetti announces new quantum computing contract")
        };
        var repeated = Catalyst("RGTI", start.AddHours(2), "Rigetti announces quantum computing contract update");
        var unrelated = Catalyst("RGTI", start.AddHours(2), "Rigetti reports earnings and raises guidance");

        var repeatedScore = scorer.Score(repeated, prior, TimeSpan.FromDays(7));
        var unrelatedScore = scorer.Score(unrelated, prior, TimeSpan.FromDays(7));

        Assert.True(repeatedScore < unrelatedScore);
        Assert.InRange(repeatedScore, 0m, 1m);
        Assert.InRange(unrelatedScore, 0m, 1m);
    }

    [Theory]
    [InlineData("Company beats earnings and raises guidance", "earnings_or_guidance")]
    [InlineData("Biotech wins FDA approval for phase 3 drug", "biotech_regulatory")]
    [InlineData("Analyst upgrades stock and raises price target", "analyst_positive")]
    [InlineData("Company announces registered direct offering", "financing_or_dilution")]
    public void Classifier_MapsCommonTradingCatalysts(string headline, string expected)
    {
        var classifier = new CatalystEventClassifier();

        var category = classifier.Classify(Catalyst("ABC", DateTimeOffset.UtcNow, headline));

        Assert.Equal(expected, category);
    }

    [Fact]
    public void Analyze_UsesOnlyBarsCompletedBeforeProviderEventTime()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 80)
            .Select(i => new OhlcvBar(
                "POET",
                start.AddMinutes(i * 5),
                "5m",
                10m + i * 0.01m,
                10.20m + i * 0.01m,
                9.90m + i * 0.01m,
                10m + i * 0.10m,
                1000m + i * 10m))
            .ToArray();
        var eventTime = start.AddMinutes(17);
        var receivedAt = eventTime.AddSeconds(12);
        var catalysts = new[] { Catalyst("POET", eventTime, "POET wins commercial supply contract", 0.75m, receivedAt) };
        var runner = CreateRunner();

        var report = runner.Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase) { ["POET"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>(StringComparer.OrdinalIgnoreCase) { ["POET"] = catalysts },
            start,
            start.AddHours(8),
            "5m",
            Options(start, start.AddHours(8), TimeSpan.FromHours(1)));

        Assert.Equal(start.AddHours(8), report.GeneratedAtUtc);
        var observation = Assert.Single(report.Observations);
        Assert.Equal(eventTime.ToUniversalTime(), observation.ProviderPublishedTimestampUtc);
        Assert.Equal(receivedAt.ToUniversalTime(), observation.ReceivedTimestampUtc);
        Assert.Equal(eventTime.ToUniversalTime(), observation.AvailableTimestampUtc);
        Assert.Equal(
            CatalystAvailabilityEvidence.ProviderTimestampOnly,
            observation.AvailabilityEvidence);
        Assert.Equal(start.AddMinutes(15), observation.AnchorTimestampUtc);
        Assert.Equal(-2m, observation.AnchorBarOffsetMinutes);
        Assert.Equal(start.AddMinutes(20), observation.FirstConfirmableTimestampUtc);
        Assert.Equal(3m, observation.FirstConfirmableDelayMinutes);
        Assert.Equal(bars[2].Close, observation.AnchorClose);
        Assert.Null(observation.PreNewsReturn15mPct);
        Assert.True(observation.PostNewsReturn15mPct > 0m);
        var oneHour = Assert.Single(observation.ForwardReturns);
        Assert.Equal("1h", oneHour.Horizon);
        Assert.Equal(start.AddMinutes(80), oneHour.TargetTimestampUtc);
        Assert.True(oneHour.ReturnPct > 0m);
    }

    [Fact]
    public void Analyze_ObservedReceiptClock_ExcludesContainingCandle()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 12)
            .Select(i => new OhlcvBar(
                "RGTI",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i,
                1000m))
            .ToArray();
        var publishedAt = start.AddMinutes(14);
        var receivedAt = start.AddMinutes(17).AddSeconds(12);
        var catalyst = Catalyst(
            "RGTI",
            publishedAt,
            "Rigetti receives quantum contract",
            0.75m,
            receivedAt,
            CatalystAvailabilityEvidence.ObservedReceiptTime);

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["RGTI"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>> { ["RGTI"] = [catalyst] },
            start,
            start.AddHours(1),
            "5m",
            Options(start, start.AddHours(1), TimeSpan.FromMinutes(15)));

        var observation = Assert.Single(report.Observations);
        Assert.Equal(receivedAt, observation.AvailableTimestampUtc);
        Assert.Equal(start.AddMinutes(15), observation.AnchorTimestampUtc);
        Assert.Equal(bars[2].Close, observation.AnchorClose);
        Assert.Equal(start.AddMinutes(20), observation.FirstConfirmableTimestampUtc);
        Assert.Equal(2.8m, observation.FirstConfirmableDelayMinutes);
    }

    [Fact]
    public void Analyze_RevisedArticle_UsesProviderUpdatedTimeAsAvailability()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 20)
            .Select(i => new OhlcvBar(
                "POET",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i,
                1000m))
            .ToArray();
        var publishedAt = start.AddMinutes(14);
        var updatedAt = start.AddMinutes(17);
        var catalyst = Catalyst(
            "POET",
            publishedAt,
            "POET updates contract announcement",
            updatedAt: updatedAt);

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["POET"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>> { ["POET"] = [catalyst] },
            start,
            start.AddHours(2),
            "5m",
            Options(start, start.AddHours(2), TimeSpan.FromMinutes(15)));

        var observation = Assert.Single(report.Observations);
        Assert.Equal(updatedAt, observation.AvailableTimestampUtc);
        Assert.Equal(
            CatalystAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            observation.AvailabilityEvidence);
        Assert.Equal(start.AddMinutes(15), observation.AnchorTimestampUtc);
        Assert.Equal(start.AddMinutes(20), observation.FirstConfirmableTimestampUtc);
    }

    [Fact]
    public void Analyze_CensorsOutcomeThatCrossesMarketSession()
    {
        var start = DateTimeOffset.Parse("2026-06-01T19:30:00Z");
        var bars = Enumerable.Range(0, 24)
            .Select(i => new OhlcvBar(
                "RGTI",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i,
                1000m))
            .ToArray();
        var catalyst = Catalyst(
            "RGTI",
            start.AddMinutes(17),
            "Rigetti receives quantum contract");

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["RGTI"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>> { ["RGTI"] = [catalyst] },
            start,
            start.AddHours(2),
            "5m",
            Options(start, start.AddHours(2), TimeSpan.FromMinutes(30)));

        var observation = Assert.Single(report.Observations);
        Assert.Equal("regular", observation.MarketSession);
        var outcome = Assert.Single(observation.ForwardReturns);
        Assert.Null(outcome.TargetTimestampUtc);
        Assert.Null(outcome.ReturnPct);
    }

    [Fact]
    public void Analyze_ExcludesEventsOutsideDeclaredStudyWindow()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 30)
            .Select(i => new OhlcvBar(
                "RGTI",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i,
                1000m))
            .ToArray();
        var catalysts = new[]
        {
            Catalyst("RGTI", start.AddMinutes(-1), "Before window"),
            Catalyst("RGTI", start.AddMinutes(17), "Inside window"),
            Catalyst("RGTI", start.AddHours(1), "At window end")
        };

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["RGTI"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>> { ["RGTI"] = catalysts },
            start,
            start.AddHours(1),
            "5m",
            Options(start, start.AddHours(1)));

        var observation = Assert.Single(report.Observations);
        Assert.Equal("Inside window", observation.Headline);
    }

    [Fact]
    public void Analyze_CensorsForwardOutcomePastDeclaredStudyEnd()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 30)
            .Select(i => new OhlcvBar(
                "RGTI",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i,
                1000m))
            .ToArray();

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["RGTI"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>
            {
                ["RGTI"] = [Catalyst("RGTI", start.AddMinutes(17), "Inside window")]
            },
            start,
            start.AddMinutes(30),
            "5m",
            Options(start, start.AddMinutes(30), TimeSpan.FromMinutes(15)));

        var outcome = Assert.Single(Assert.Single(report.Observations).ForwardReturns);
        Assert.Null(outcome.TargetTimestampUtc);
        Assert.Null(outcome.ReturnPct);
    }

    [Fact]
    public void Analyze_UpdatedStoryDoesNotLeakIntoEarlierStoryNovelty()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 48)
            .Select(i => new OhlcvBar(
                "RGTI",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i * 0.01m,
                1000m))
            .ToArray();
        var updatedLater = Catalyst(
            "RGTI",
            start.AddMinutes(5),
            "Rigetti quantum contract expands",
            updatedAt: start.AddMinutes(35));
        var availableEarlier = Catalyst(
            "RGTI",
            start.AddMinutes(20),
            "Rigetti quantum contract expands internationally");

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["RGTI"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>
            {
                ["RGTI"] = [updatedLater, availableEarlier]
            },
            start,
            start.AddHours(3),
            "5m",
            Options(start, start.AddHours(3), TimeSpan.FromMinutes(15)));

        Assert.Equal(2, report.Observations.Count);
        var earlier = report.Observations.Single(x => x.Headline.Contains("internationally"));
        var revision = report.Observations.Single(x => x.Headline == "Rigetti quantum contract expands");
        Assert.Equal(1m, earlier.NoveltyScore);
        Assert.True(revision.NoveltyScore < earlier.NoveltyScore);
        Assert.True(earlier.AvailableTimestampUtc < revision.AvailableTimestampUtc);
        Assert.NotEqual(earlier.RevisionId, revision.RevisionId);
    }

    [Fact]
    public void Analyze_BucketsRemainSeparateByPartitionSessionAndAvailabilityEvidence()
    {
        var start = DateTimeOffset.Parse("2026-06-01T08:00:00Z");
        var end = DateTimeOffset.Parse("2026-06-01T22:00:00Z");
        var bars = Enumerable.Range(0, 168)
            .Select(i => new OhlcvBar(
                "POET",
                start.AddMinutes(i * 5),
                "5m",
                10m,
                11m,
                9m,
                10m + i * 0.01m,
                1000m))
            .ToArray();
        var premarketProviderClock = Catalyst(
            "POET",
            DateTimeOffset.Parse("2026-06-01T12:15:00Z"),
            "POET wins contract");
        var regularObservedReceipt = Catalyst(
            "POET",
            DateTimeOffset.Parse("2026-06-01T14:10:00Z"),
            "POET wins second contract",
            receivedAt: DateTimeOffset.Parse("2026-06-01T14:10:10Z"),
            availabilityEvidence: CatalystAvailabilityEvidence.ObservedReceiptTime);
        var split = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var options = new CatalystEventStudyOptions
        {
            Horizons = [TimeSpan.FromMinutes(15)],
            StudyPartitions =
            [
                new CatalystStudyPartition("development", start, split),
                new CatalystStudyPartition("holdout", split, end)
            ]
        };

        var report = CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["POET"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>
            {
                ["POET"] = [premarketProviderClock, regularObservedReceipt]
            },
            start,
            end,
            "5m",
            options);

        Assert.Equal(2, report.Buckets.Count);
        Assert.Contains(report.Buckets, bucket =>
            bucket.StudyPartition == "development" &&
            bucket.MarketSession == "premarket" &&
            bucket.AvailabilityEvidence == CatalystAvailabilityEvidence.ProviderTimestampOnly);
        Assert.Contains(report.Buckets, bucket =>
            bucket.StudyPartition == "holdout" &&
            bucket.MarketSession == "regular" &&
            bucket.AvailabilityEvidence == CatalystAvailabilityEvidence.ObservedReceiptTime);
    }

    [Fact]
    public void Analyze_RequiresCallerSuppliedFrozenStudyPartitions()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = new[]
        {
            new OhlcvBar("POET", start, "5m", 10m, 11m, 9m, 10m, 1000m)
        };

        var error = Assert.Throws<ArgumentException>(() => CreateRunner().Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["POET"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>(),
            start,
            start.AddMinutes(5),
            "5m"));

        Assert.Contains("caller-supplied", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionResolver_UsesEarlyCloseAndTreatsExactCloseAsPostmarket()
    {
        var date = new DateOnly(2026, 11, 27);
        var resolver = CreateResolver(
            new TradingSessionSnapshot(
                date,
                EquityTradingSession.Regular,
                DateTimeOffset.Parse("2026-11-27T10:00:00Z"),
                DateTimeOffset.Parse("2026-11-27T14:30:00Z"),
                DateTimeOffset.Parse("2026-11-27T18:00:00Z")));

        Assert.Equal(
            "regular",
            resolver.Resolve(DateTimeOffset.Parse("2026-11-27T17:59:59Z")).Label);
        Assert.Equal(
            "postmarket",
            resolver.Resolve(DateTimeOffset.Parse("2026-11-27T18:00:00Z")).Label);
    }

    [Fact]
    public void SessionResolver_UsesCalendarUtcBoundsAcrossDst()
    {
        var winter = new TradingSessionSnapshot(
            new DateOnly(2026, 1, 5),
            EquityTradingSession.Regular,
            DateTimeOffset.Parse("2026-01-05T12:00:00Z"),
            DateTimeOffset.Parse("2026-01-05T14:30:00Z"),
            DateTimeOffset.Parse("2026-01-05T21:00:00Z"));
        var summer = new TradingSessionSnapshot(
            new DateOnly(2026, 7, 6),
            EquityTradingSession.Regular,
            DateTimeOffset.Parse("2026-07-06T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T13:30:00Z"),
            DateTimeOffset.Parse("2026-07-06T20:00:00Z"));
        var resolver = CreateResolver(winter, summer);

        Assert.Equal(
            "regular",
            resolver.Resolve(DateTimeOffset.Parse("2026-01-05T14:30:00Z")).Label);
        Assert.Equal(
            "regular",
            resolver.Resolve(DateTimeOffset.Parse("2026-07-06T13:30:00Z")).Label);
        Assert.Equal(
            "postmarket",
            resolver.Resolve(DateTimeOffset.Parse("2026-07-06T20:00:00Z")).Label);
    }

    private static CatalystTechnicalEventStudyRunner CreateRunner() =>
        new(CreateResolver(
            new TradingSessionSnapshot(
                new DateOnly(2026, 6, 1),
                EquityTradingSession.Regular,
                DateTimeOffset.Parse("2026-06-01T08:00:00Z"),
                DateTimeOffset.Parse("2026-06-01T13:30:00Z"),
                DateTimeOffset.Parse("2026-06-01T20:00:00Z"))));

    private static HistoricalExchangeSessionResolver CreateResolver(
        params TradingSessionSnapshot[] days) =>
        new(
            days,
            ResolveEasternTimeZone(),
            new TimeOnly(4, 0),
            new TimeOnly(20, 0));

    private static CatalystEventStudyOptions Options(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        params TimeSpan[] horizons) =>
        new()
        {
            Horizons = horizons.Length == 0
                ? [TimeSpan.FromMinutes(15)]
                : horizons,
            StudyPartitions =
            [
                new CatalystStudyPartition("development", startUtc, endUtc)
            ]
        };

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the platform-specific identifier.
            }
        }

        throw new InvalidOperationException("America/New_York timezone is unavailable.");
    }

    private static CatalystEvent Catalyst(
        string ticker,
        DateTimeOffset timestamp,
        string headline,
        decimal sentiment = 0.3m,
        DateTimeOffset? receivedAt = null,
        string? availabilityEvidence = null,
        DateTimeOffset? updatedAt = null) =>
        new(
            ticker,
            timestamp,
            CatalystType.NewsReport,
            headline,
            sentiment,
            Provider: "test",
            Source: "unit",
            Url: null,
            ReceivedAt: receivedAt,
            UpdatedAt: updatedAt,
            AvailabilityEvidence: availabilityEvidence);
}
