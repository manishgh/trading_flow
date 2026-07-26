using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Research.Intraday;
using TradingFlow.Engine.Execution;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Tests;

public sealed class IntradayParticipationResearchTests
{
    private static readonly DateOnly EventDate = new(2026, 7, 20);
    private static readonly DateTimeOffset EventOpen =
        new(2026, 7, 20, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ClassifiedEvidence_AvailabilityIsLaterOfNewsAndClassifierObservation()
    {
        var completed = EventOpen.AddMinutes(2);
        var observed = completed.AddSeconds(3);
        var row = Classification(
            "revision-1",
            "story-1",
            completed,
            observed,
            EventOpen.AddMinutes(1));

        Assert.Equal(observed, row.AvailableAtUtc);
        Assert.Equal(completed, row.InferenceCompletedAtUtc);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_RejectsMissingValidatedClassifierEvidence()
    {
        var fixture = Fixture();
        var request = fixture.Request with
        {
            ClassifierValidation = fixture.Request.ClassifierValidation with
            {
                PassedFrozenValidation = false
            },
            Classifications = []
        };

        var report = fixture.Runner.AnalyzeIntradayEvidence(request);

        Assert.Equal(IntradayEvidenceEligibility.Rejected, report.Eligibility);
        Assert.Contains("classifier_validation_not_ready", report.Blockers);
        Assert.Contains("classified_catalyst_evidence_missing", report.Blockers);
        Assert.Empty(report.Observations);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_RequiresObservedReceiptUnlessDiagnosticIsExplicit()
    {
        var fixture = Fixture(observedReceipt: false);

        var blocked = fixture.Runner.AnalyzeIntradayEvidence(fixture.Request);

        Assert.Equal(IntradayEvidenceEligibility.Rejected, blocked.Eligibility);
        Assert.Contains("observed_news_receipt_missing", blocked.Blockers);

        var diagnostic = fixture.Runner.AnalyzeIntradayEvidence(
            fixture.Request with
            {
                Options = fixture.Request.Options with
                {
                    AllowProviderTimestampDiagnosticMode = true
                }
            });

        Assert.Equal(IntradayEvidenceEligibility.DiagnosticOnly, diagnostic.Eligibility);
        Assert.All(
            diagnostic.Observations,
            value => Assert.Equal(
                IntradayEvidenceEligibility.DiagnosticOnly,
                value.Eligibility));
        Assert.Contains(
            diagnostic.Observations,
            value => value.CensorReason == "provider_timestamp_only_diagnostic");
    }

    [Fact]
    public void AnalyzeIntradayEvidence_ComputesCumulativeRvolAndSignedAbnormalReturns()
    {
        var fixture = Fixture();

        var report = fixture.Runner.AnalyzeIntradayEvidence(fixture.Request);

        Assert.Equal(IntradayEvidenceEligibility.PromotionEligible, report.Eligibility);
        var observation = Assert.Single(report.Observations);
        Assert.Equal(45, observation.ComparableSessionCount);
        Assert.InRange(observation.CumulativeRegularSessionRvol!.Value, 1.95m, 2.01m);
        Assert.NotNull(observation.RobustLogVolumeZScore);
        Assert.Equal("regular", observation.ResponseSession);
        Assert.Equal(EventOpen.AddMinutes(6), observation.ResponseAnchorCompletedAtUtc);
        var horizon = Assert.Single(observation.Horizons);
        Assert.False(horizon.IsCensored);
        Assert.True(horizon.RawSignedReturnPct > 0m);
        Assert.True(horizon.SpyAbnormalReturnPct > 0m);
        Assert.True(horizon.SectorAbnormalReturnPct > 0m);
        Assert.NotNull(horizon.ExecutableReturnPct);
        Assert.NotNull(horizon.MfePct);
        Assert.NotNull(horizon.MaePct);
        var pValue = Assert.Single(report.HolmReadyPValues);
        Assert.Equal(1, pValue.SampleCount);
        Assert.Equal(1, pValue.HolmRank);
        Assert.Equal(1, pValue.FamilySize);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_FailsObservationBelowFortyComparableSessions()
    {
        var fixture = Fixture(priorSessionCount: 39);

        var report = fixture.Runner.AnalyzeIntradayEvidence(fixture.Request);

        Assert.Equal(IntradayEvidenceEligibility.DiagnosticOnly, report.Eligibility);
        var observation = Assert.Single(report.Observations);
        Assert.Equal(IntradayEvidenceEligibility.Rejected, observation.Eligibility);
        Assert.Equal(39, observation.ComparableSessionCount);
        Assert.Equal(
            "comparable_regular_sessions_below_minimum:39/40",
            observation.CensorReason);
        Assert.Empty(observation.Horizons);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_CensorsAtPartitionEnd()
    {
        var fixture = Fixture();
        var partitionEnd = EventOpen.AddMinutes(6).AddSeconds(30);
        var request = fixture.Request with
        {
            EndUtc = partitionEnd,
            Options = fixture.Request.Options with
            {
                Partitions =
                [
                    new CatalystStudyPartitionDefinition(
                        "development",
                        fixture.Request.StartUtc,
                        partitionEnd)
                ]
            }
        };

        var report = fixture.Runner.AnalyzeIntradayEvidence(request);

        var horizon = Assert.Single(Assert.Single(report.Observations).Horizons);
        Assert.True(horizon.IsCensored);
        Assert.Equal("partition_end_embargo", horizon.CensorReason);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_MapsPremarketToOpeningAndPostmarketToNextOpen()
    {
        var premarket = Fixture(
            eventAt: EventOpen.AddMinutes(-30),
            classifierCompletedAt: EventOpen.AddMinutes(-20));
        var premarketReport = premarket.Runner.AnalyzeIntradayEvidence(premarket.Request);
        Assert.Equal(
            EventOpen.AddMinutes(1),
            Assert.Single(premarketReport.Observations).ResponseAnchorCompletedAtUtc);

        var postmarketAt = EventOpen.AddHours(7);
        var nextOpen = EventOpen.AddDays(1);
        var postmarket = Fixture(
            eventAt: postmarketAt,
            classifierCompletedAt: postmarketAt.AddSeconds(2),
            includeNextSession: true);
        var postmarketReport = postmarket.Runner.AnalyzeIntradayEvidence(postmarket.Request);
        Assert.Equal(
            nextOpen.AddMinutes(1),
            Assert.Single(postmarketReport.Observations).ResponseAnchorCompletedAtUtc);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_ClustersOneStoryGloballyBeforeTickerExpansion()
    {
        var fixture = Fixture(secondTickerSameStory: true);

        var report = fixture.Runner.AnalyzeIntradayEvidence(fixture.Request);

        Assert.Equal(2, report.Observations.Count);
        Assert.Single(
            report.Observations
                .Select(value => value.GlobalStoryCluster)
                .Distinct(StringComparer.Ordinal));
        var pValue = Assert.Single(report.HolmReadyPValues);
        Assert.Equal(1, pValue.SampleCount);
    }

    private static ResearchFixture Fixture(
        int priorSessionCount = 45,
        bool observedReceipt = true,
        DateTimeOffset? eventAt = null,
        DateTimeOffset? classifierCompletedAt = null,
        bool includeNextSession = false,
        bool secondTickerSameStory = false)
    {
        var eventTimestamp = eventAt ?? EventOpen.AddMinutes(5).AddSeconds(30);
        var completion = classifierCompletedAt ?? eventTimestamp.AddSeconds(10);
        var dates = Enumerable.Range(1, priorSessionCount)
            .Select(offset => EventDate.AddDays(-offset))
            .OrderBy(value => value)
            .Concat([EventDate])
            .Concat(includeNextSession ? [EventDate.AddDays(1)] : [])
            .ToArray();
        var sessions = dates.Select(Session).ToArray();
        var runner = new CatalystTechnicalEventStudyRunner(Resolver(sessions));
        var bars = Bars("TEST", dates, EventDate, 2m, 0.20m);
        var spyBars = Bars("SPY", dates, EventDate, 1m, 0.01m);
        var sectorBars = Bars("XLK", dates, EventDate, 1m, 0.02m);
        var catalyst = Catalyst("TEST", eventTimestamp, "article-1", observedReceipt);
        var revisionId = CatalystAvailability.Resolve(catalyst).RevisionId;
        var classification = Classification(
            revisionId,
            "global-story-1",
            completion,
            completion,
            observedReceipt
                ? catalyst.ReceivedAt!.Value
                : catalyst.UpdatedAt ?? catalyst.Timestamp);
        var catalysts = new Dictionary<string, IReadOnlyList<CatalystEvent>>
        {
            ["TEST"] = [catalyst]
        };
        var barsByTicker = new Dictionary<string, IReadOnlyList<OhlcvBar>>
        {
            ["TEST"] = bars
        };
        var quotes = new Dictionary<string, IReadOnlyList<IntradayQuoteEvidence>>
        {
            ["TEST"] = Quotes("TEST", dates)
        };
        var classifications = new List<ClassifiedCatalystEvidenceRow> { classification };
        var sectors = new List<PointInTimeSectorEvidence>
        {
            Sector("TEST", dates[0])
        };

        if (secondTickerSameStory)
        {
            var second = Catalyst("SECOND", eventTimestamp, "article-2", observedReceipt);
            var secondRevision = CatalystAvailability.Resolve(second).RevisionId;
            catalysts["SECOND"] = [second];
            barsByTicker["SECOND"] = Bars("SECOND", dates, EventDate, 2m, 0.20m);
            quotes["SECOND"] = Quotes("SECOND", dates);
            classifications.Add(Classification(
                secondRevision,
                "global-story-1",
                completion,
                completion,
                second.ReceivedAt!.Value,
                providerArticleId: "article-2"));
            sectors.Add(Sector("SECOND", dates[0]));
        }

        var start = new DateTimeOffset(
            EventDate.Year,
            EventDate.Month,
            EventDate.Day,
            0,
            0,
            0,
            TimeSpan.Zero);
        var end = start.AddDays(includeNextSession ? 2 : 1);
        var request = new IntradayEvidenceStudyRequest(
            barsByTicker,
            catalysts,
            quotes,
            new Dictionary<string, IReadOnlyList<OhlcvBar>>
            {
                ["SPY"] = spyBars,
                ["XLK"] = sectorBars
            },
            sectors,
            classifications,
            new IntradayClassifierValidation(
                500,
                100,
                0.90m,
                0.90m,
                0.90m,
                0.92m,
                0.85m,
                true),
            start,
            end,
            "1m",
            new IntradayEvidenceStudyOptions
            {
                Horizons = [TimeSpan.FromMinutes(1)],
                Partitions =
                [
                    new CatalystStudyPartitionDefinition(
                        "development",
                        start,
                        end)
                ],
                AllowProviderTimestampDiagnosticMode = false
            });
        return new ResearchFixture(runner, request);
    }

    private static CatalystEvent Catalyst(
        string ticker,
        DateTimeOffset timestamp,
        string externalId,
        bool observedReceipt)
    {
        DateTimeOffset? receipt = observedReceipt ? timestamp.AddSeconds(5) : null;
        return new CatalystEvent(
            ticker,
            timestamp,
            CatalystType.NewsReport,
            $"{ticker} announces material commercial contract",
            0.8m,
            Provider: "test",
            ExternalId: externalId,
            Summary: "Material customer award",
            Source: "unit",
            Url: $"https://example.test/{externalId}",
            ReceivedAt: receipt,
            UpdatedAt: observedReceipt ? null : timestamp.AddSeconds(2),
            AvailabilityEvidence: observedReceipt
                ? CatalystAvailabilityEvidence.ObservedReceiptTime
                : CatalystAvailabilityEvidence.ProviderUpdatedTimestampOnly);
    }

    private static ClassifiedCatalystEvidenceRow Classification(
        string revisionId,
        string storyCluster,
        DateTimeOffset completedAt,
        DateTimeOffset observedAt,
        DateTimeOffset newsAvailableAt,
        string providerArticleId = "article-1") =>
        new(
            1,
            "run-1",
            new string('a', 64),
            "code-1",
            "test",
            $"classification-{providerArticleId}",
            "test",
            providerArticleId,
            revisionId,
            new string('b', 64),
            storyCluster,
            CatalystNewsCategory.MajorCommercialEvent,
            CatalystDirection.Positive,
            CatalystMateriality.High,
            "unit",
            "classifier",
            "1.0",
            new string('c', 64),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            completedAt,
            observedAt,
            newsAvailableAt,
            [new EvidenceRowSourceAddress("source-1", new string('d', 64))]);

    private static IReadOnlyList<OhlcvBar> Bars(
        string ticker,
        IReadOnlyList<DateOnly> dates,
        DateOnly eventDate,
        decimal eventVolumeMultiplier,
        decimal eventMinuteGain)
    {
        var output = new List<OhlcvBar>();
        for (var dateIndex = 0; dateIndex < dates.Count; dateIndex++)
        {
            var date = dates[dateIndex];
            var open = Open(date);
            var isEventSession = date == eventDate;
            for (var minute = 0; minute < 70; minute++)
            {
                var baseline = 100m + dateIndex * 0.01m;
                var close = baseline + (isEventSession ? minute * eventMinuteGain : minute * 0.001m);
                var normalVolume = 100m + dateIndex % 5;
                var volume = normalVolume * (isEventSession ? eventVolumeMultiplier : 1m);
                output.Add(new OhlcvBar(
                    ticker,
                    open.AddMinutes(minute),
                    "1m",
                    close - 0.02m,
                    close + 0.05m,
                    close - 0.05m,
                    close,
                    volume));
            }
        }

        return output;
    }

    private static IReadOnlyList<IntradayQuoteEvidence> Quotes(
        string ticker,
        IReadOnlyList<DateOnly> dates) =>
        dates.SelectMany(date =>
        {
            var open = Open(date);
            return Enumerable.Range(1, 70)
                .Select(minute =>
                {
                    var price = 100m + minute * 0.20m;
                    return new IntradayQuoteEvidence(
                        ticker,
                        open.AddMinutes(minute).AddSeconds(1),
                        price - 0.01m,
                        price + 0.01m,
                        "sip");
                });
        }).ToArray();

    private static PointInTimeSectorEvidence Sector(string ticker, DateOnly firstDate) =>
        new(
            ticker,
            "technology",
            "XLK",
            Open(firstDate).AddDays(-1),
            EventOpen.AddYears(1),
            Open(firstDate).AddDays(-1));

    private static TradingSessionSnapshot Session(DateOnly date) =>
        new(
            date,
            EquityTradingSession.Regular,
            Open(date).AddHours(-1),
            Open(date),
            Open(date).AddHours(6.5));

    private static DateTimeOffset Open(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 13, 30, 0, TimeSpan.Zero);

    private static HistoricalExchangeSessionResolver Resolver(
        params TradingSessionSnapshot[] sessions) =>
        new(
            sessions,
            ResolveEasternTimeZone(),
            new TimeOnly(4, 0),
            new TimeOnly(20, 0));

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

    private sealed record ResearchFixture(
        CatalystTechnicalEventStudyRunner Runner,
        IntradayEvidenceStudyRequest Request);
}
