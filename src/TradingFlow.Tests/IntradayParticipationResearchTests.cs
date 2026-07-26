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
                CompositeQualifierPrecision95LowerBound = 0.69m
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
        Assert.Equal(pValue.RawPValue, pValue.AdjustedPValue);
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

        var observation = Assert.Single(report.Observations);
        var horizon = Assert.Single(observation.Horizons);
        Assert.True(horizon.IsCensored);
        Assert.Equal("partition_end_embargo", horizon.CensorReason);
        Assert.Equal(IntradayEvidenceEligibility.Rejected, observation.Eligibility);
        Assert.Equal("all_horizons_censored", observation.CensorReason);
        Assert.Equal(IntradayEvidenceEligibility.DiagnosticOnly, report.Eligibility);
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

    [Fact]
    public void AnalyzeIntradayEvidence_UsesClassifierObservedClockForResponseAnchor()
    {
        var observedAt = EventOpen.AddMinutes(6).AddSeconds(30);
        var fixture = Fixture(classifierObservedAt: observedAt);

        var observation = Assert.Single(
            fixture.Runner.AnalyzeIntradayEvidence(fixture.Request).Observations);

        Assert.Equal(observedAt, observation.AvailableAtUtc);
        Assert.Equal(EventOpen.AddMinutes(7), observation.ResponseAnchorCompletedAtUtc);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_MorphologyIsPointInTimeForAnchorAndEachHorizon()
    {
        var fixture = Fixture();

        var observation = Assert.Single(
            fixture.Runner.AnalyzeIntradayEvidence(fixture.Request).Observations);
        var horizon = Assert.Single(observation.Horizons);

        Assert.Equal(IntradayMorphology.None, observation.Morphology);
        Assert.Equal(IntradayMorphology.None, horizon.MorphologyAtTarget);
        Assert.Equal(
            EventOpen.AddMinutes(7),
            observation.MorphologyTimeline.Max(value => value.CompletedAtUtc));
        Assert.Equal(7, observation.MorphologyTimeline.Count);
        Assert.All(
            observation.MorphologyTimeline,
            value => Assert.Equal(IntradayMorphology.None, value.State));
    }

    [Fact]
    public void AnalyzeIntradayEvidence_ComputesShortExecutableReturnFromEntryBid()
    {
        var fixture = Fixture(direction: CatalystDirection.Negative);
        var request = fixture.Request with
        {
            QuotesByTicker = new Dictionary<string, IReadOnlyList<IntradayQuoteEvidence>>
            {
                ["TEST"] =
                [
                    new("TEST", EventOpen.AddMinutes(6).AddSeconds(1), 100m, 101m, "sip"),
                    new("TEST", EventOpen.AddMinutes(7).AddSeconds(1), 89m, 90m, "sip")
                ]
            }
        };

        var horizon = Assert.Single(
            Assert.Single(fixture.Runner.AnalyzeIntradayEvidence(request).Observations)
                .Horizons);

        Assert.Equal(10m, horizon.ExecutableReturnPct);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_CensorsWhenBenchmarkTimestampIsNotExact()
    {
        var fixture = Fixture();
        var spy = fixture.Request.BenchmarkBarsByTicker["SPY"]
            .Where(value => value.Timestamp + TimeSpan.FromMinutes(1) !=
                            EventOpen.AddMinutes(6))
            .ToArray();
        var request = fixture.Request with
        {
            BenchmarkBarsByTicker =
                new Dictionary<string, IReadOnlyList<OhlcvBar>>
                {
                    ["SPY"] = spy,
                    ["XLK"] = fixture.Request.BenchmarkBarsByTicker["XLK"]
                }
        };

        var report = fixture.Runner.AnalyzeIntradayEvidence(request);
        var observation = Assert.Single(report.Observations);
        var horizon = Assert.Single(observation.Horizons);

        Assert.True(horizon.IsCensored);
        Assert.Equal("aligned_benchmark_evidence_missing", horizon.CensorReason);
        Assert.Equal(IntradayEvidenceEligibility.Rejected, observation.Eligibility);
        Assert.Equal("all_horizons_censored", observation.CensorReason);
        Assert.Equal(IntradayEvidenceEligibility.DiagnosticOnly, report.Eligibility);
    }

    [Fact]
    public void ClassifierValidation_EnforcesEveryFrozenA4AndB1Threshold()
    {
        var valid = Fixture().Request.ClassifierValidation;
        Assert.True(valid.MeetsTrackBMinimum);

        var failures = new IntradayClassifierValidation[]
        {
            valid with { CompositeQualifierPrecision = 0.79m },
            valid with { CompositeQualifierPrecision95LowerBound = 0.69m },
            valid with { CompositeQualifierRecall = 0.59m },
            valid with { CategoryMacroF1 = 0.69m },
            valid with { DirectionMacroF1 = 0.74m },
            valid with { MaterialityWeightedKappa = 0.59m },
            valid with { LabeledStoryCount = 499 },
            valid with { DoubleLabeledStoryCount = 99 },
            valid with { FrozenPromotableCategories = [] },
            valid with
            {
                UntouchedExamplesByPromotableCategory =
                    new Dictionary<CatalystNewsCategory, int>
                    {
                        [CatalystNewsCategory.MajorCommercialEvent] = 29
                    }
            }
        };

        Assert.All(failures, value => Assert.False(value.MeetsTrackBMinimum));
        Assert.Contains(
            "classifier_promotable_class_untouched_count_below_minimum",
            failures[^1].ValidationBlockers);
    }

    [Fact]
    public void AnalyzeIntradayEvidence_FailsClosedForEveryMandatoryB0EvidenceFlag()
    {
        var fixture = Fixture();
        var incomplete = new (IntradayB0EvidenceReadiness Evidence, string Blocker)[]
        {
            (fixture.Request.B0Evidence with { SipOneMinuteBarsVerified = false },
                "sip_one_minute_bars_not_verified"),
            (fixture.Request.B0Evidence with { SipNbboQuotesVerified = false },
                "sip_nbbo_quotes_not_verified"),
            (fixture.Request.B0Evidence with { OpeningAuctionStatusVerified = false },
                "opening_auction_status_not_verified"),
            (fixture.Request.B0Evidence with { HaltResumeAndLuldStatusVerified = false },
                "halt_resume_luld_status_not_verified"),
            (fixture.Request.B0Evidence with { CorporateActionsVerified = false },
                "corporate_actions_not_verified"),
            (fixture.Request.B0Evidence with
                {
                    PointInTimeSectorMembershipVerified = false
                },
                "point_in_time_sector_membership_not_verified"),
            (fixture.Request.B0Evidence with { GlobalStoryClustersVerified = false },
                "global_story_clusters_not_verified")
        };

        foreach (var (evidence, blocker) in incomplete)
        {
            var report = fixture.Runner.AnalyzeIntradayEvidence(
                fixture.Request with { B0Evidence = evidence });
            Assert.Equal(IntradayEvidenceEligibility.Rejected, report.Eligibility);
            Assert.Contains(blocker, report.Blockers);
            Assert.Empty(report.Observations);
        }
    }

    [Fact]
    public void AnalyzeIntradayEvidence_AppliesHolmAcrossOneFrozenFamily()
    {
        var fixture = Fixture(additionalIndependentTickers: 5);
        var request = fixture.Request with
        {
            Options = fixture.Request.Options with
            {
                Horizons =
                [
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(5)
                ]
            }
        };

        var pValues = fixture.Runner
            .AnalyzeIntradayEvidence(request)
            .HolmReadyPValues;

        Assert.Equal(2, pValues.Count);
        Assert.Single(pValues.Select(value => value.Family).Distinct());
        Assert.All(pValues, value =>
        {
            Assert.Equal(2, value.FamilySize);
            Assert.Equal(0.03125m, value.RawPValue);
            Assert.Equal(0.0625m, value.AdjustedPValue);
            Assert.False(value.RejectedAtFamilyWiseAlpha);
        });
        Assert.Contains(
            pValues,
            value => value.AdjustedPValue > value.RawPValue);
    }

    private static ResearchFixture Fixture(
        int priorSessionCount = 45,
        bool observedReceipt = true,
        DateTimeOffset? eventAt = null,
        DateTimeOffset? classifierCompletedAt = null,
        DateTimeOffset? classifierObservedAt = null,
        bool includeNextSession = false,
        bool secondTickerSameStory = false,
        CatalystDirection direction = CatalystDirection.Positive,
        int additionalIndependentTickers = 0)
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
            classifierObservedAt ?? completion,
            observedReceipt
                ? catalyst.ReceivedAt!.Value
                : catalyst.UpdatedAt ?? catalyst.Timestamp,
            direction: direction);
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

        for (var index = 0; index < additionalIndependentTickers; index++)
        {
            var ticker = $"INDEPENDENT{index + 1}";
            var articleId = $"independent-article-{index + 1}";
            var independent = Catalyst(ticker, eventTimestamp, articleId, observedReceipt);
            var independentRevision = CatalystAvailability.Resolve(independent).RevisionId;
            catalysts[ticker] = [independent];
            barsByTicker[ticker] = Bars(ticker, dates, EventDate, 2m, 0.20m);
            quotes[ticker] = Quotes(ticker, dates);
            classifications.Add(Classification(
                independentRevision,
                $"global-story-independent-{index + 1}",
                completion,
                completion,
                independent.ReceivedAt!.Value,
                providerArticleId: articleId,
                direction: direction));
            sectors.Add(Sector(ticker, dates[0]));
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
                0.80m,
                0.90m,
                0.90m,
                0.92m,
                0.85m,
                [CatalystNewsCategory.MajorCommercialEvent],
                new Dictionary<CatalystNewsCategory, int>
                {
                    [CatalystNewsCategory.MajorCommercialEvent] = 30
                }),
            new IntradayB0EvidenceReadiness(
                SipOneMinuteBarsVerified: true,
                SipNbboQuotesVerified: true,
                OpeningAuctionStatusVerified: true,
                HaltResumeAndLuldStatusVerified: true,
                CorporateActionsVerified: true,
                PointInTimeSectorMembershipVerified: true,
                GlobalStoryClustersVerified: true),
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
        string providerArticleId = "article-1",
        CatalystDirection direction = CatalystDirection.Positive) =>
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
            direction,
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
