using System.Globalization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Research.Intraday;
using TradingFlow.Research.Catalysts;

namespace TradingFlow.Tests;

public sealed class IntradayResearchPromotionEvaluatorTests
{
    [Fact]
    public void Evaluate_CompleteFrozenEvidenceAtGateBoundaries_Promotes()
    {
        var assessment = IntradayResearchPromotionEvaluator.Evaluate(
            PassingEvidence());

        Assert.True(
            assessment.IsPromotable,
            string.Join(
                Environment.NewLine,
                assessment.Blockers.Select(value => $"{value.Code}: {value.Message}")));
        Assert.Equal(
            IntradayResearchPromotionDecision.PromoteToPaperShadow,
            assessment.Decision);
        Assert.Empty(assessment.Blockers);
        Assert.Equal(300, assessment.Metrics.IndependentEpisodeCount);
        Assert.Equal(100, assessment.Metrics.ProposedCohortEpisodeCount);
        Assert.Equal(100, assessment.Metrics.ExecutableTradeCount);
        Assert.Equal(30, assessment.Metrics.HoldoutTradeCount);
        Assert.True(assessment.Metrics.DevelopmentNetExpectancyBps > 0m);
        Assert.True(assessment.Metrics.ValidationNetExpectancyBps > 0m);
        Assert.True(assessment.Metrics.HoldoutNetExpectancyBps > 0m);
        Assert.Equal(12m, assessment.Metrics.OverallGrossEdgeBps);
        Assert.True(assessment.Metrics.ProfitFactorAfterCosts >= 1.20m);
        Assert.True(assessment.Metrics.LargestTickerAbsolutePnlShare <= 0.10m);
        Assert.True(
            assessment.Metrics.LargestTradingDayAbsolutePnlShare <= 0.10m);
    }

    [Fact]
    public void Evaluate_MissingEvidence_EmitsExplicitBlockersAndRetainsResearch()
    {
        var evidence = PassingEvidence() with
        {
            Episodes = null,
            Trades = null,
            CostStresses = null,
            ClusteredInference = null,
            SessionBlockBootstrap = null,
            Concentration = null,
            LeaveOneOut = null,
            HumanApproval = null,
            EvidenceStudyReport = null
        };

        var assessment = IntradayResearchPromotionEvaluator.Evaluate(evidence);

        Assert.False(assessment.IsPromotable);
        Assert.Equal(
            IntradayResearchPromotionDecision.RetainResearch,
            assessment.Decision);
        AssertBlockers(
            assessment,
            "episode_evidence_missing",
            "trade_evidence_missing",
            "cost_stress_evidence_missing",
            "clustered_inference_evidence_missing",
            "session_block_bootstrap_evidence_missing",
            "concentration_evidence_missing",
            "leave_one_out_evidence_missing",
            "human_promotion_approval_missing",
            "intraday_evidence_study_report_missing");
    }

    [Fact]
    public void Evaluate_InsufficientAndUnprofitableTradeEvidence_BlocksEveryCoreGate()
    {
        var baseline = PassingEvidence();
        var episodes = baseline.Episodes!
            .Take(299)
            .Select((value, index) => index == 99
                ? value with { EventCohort = "other-cohort" }
                : value)
            .ToArray();
        var trades = baseline.Trades!
            .Take(99)
            .Select((value, index) =>
            {
                var isLargeLoss = index % 3 == 0;
                return value with
                {
                    GrossReturnBps = 3m,
                    NetReturnBps = isLargeLoss ? -1m : 8m,
                    NetProfitLoss = isLargeLoss ? -1000m : 10m
                };
            })
            .ToArray();
        var evidence = baseline with
        {
            Episodes = episodes,
            Trades = trades,
            FrozenRoundTripCostBps = 2m
        };

        var assessment = IntradayResearchPromotionEvaluator.Evaluate(evidence);

        Assert.False(assessment.IsPromotable);
        AssertBlockers(
            assessment,
            "independent_catalyst_episodes_below_minimum",
            "proposed_event_cohort_episodes_below_minimum",
            "executable_simulated_trades_below_minimum",
            "holdout_trades_below_minimum",
            "gross_edge_below_twice_round_trip_cost",
            "profit_factor_below_minimum");
    }

    [Fact]
    public void Evaluate_CostAndLeaveOneOutDependence_RetainsResearch()
    {
        var baseline = PassingEvidence();
        var episodes = baseline.Episodes!
            .Select(value => value with
            {
                EventSubtype = "earnings",
                OpeningMinuteOfRegularSession = 5
            })
            .ToArray();
        var trades = baseline.Trades!
            .Select(value => value with
            {
                EventSubtype = "earnings",
                OpeningMinuteOfRegularSession = 5
            })
            .ToArray();
        var evidence = RebuildDerivedEvidence(
            baseline with
            {
                Episodes = episodes,
                Trades = trades,
                CostStresses =
                [
                    new IntradayCostStressEvidence(
                        1m,
                        trades.Average(value => value.NetReturnBps)),
                    new IntradayCostStressEvidence(2m, 3m),
                    new IntradayCostStressEvidence(3m, 0m)
                ]
            });

        var assessment = IntradayResearchPromotionEvaluator.Evaluate(evidence);

        Assert.False(assessment.IsPromotable);
        AssertBlockers(
            assessment,
            "cost_stress_3x_reverses_conclusion",
            "single_event_subtype_dependence",
            "single_opening_minute_dependence");
    }

    [Fact]
    public void Evaluate_DiagnosticReportAndUnboundApproval_RetainsResearch()
    {
        var baseline = PassingEvidence();
        var report = baseline.EvidenceStudyReport! with
        {
            Eligibility = IntradayEvidenceEligibility.DiagnosticOnly,
            Blockers = ["provider_timestamp_only_diagnostic"]
        };
        var approval = baseline.HumanApproval! with
        {
            Approved = false,
            ExperimentId = "different-experiment",
            EvidenceSnapshotHash = "different-snapshot"
        };

        var assessment = IntradayResearchPromotionEvaluator.Evaluate(
            baseline with
            {
                EvidenceStudyReport = report,
                HumanApproval = approval
            });

        Assert.False(assessment.IsPromotable);
        AssertBlockers(
            assessment,
            "intraday_evidence_study_not_promotion_eligible",
            "intraday_evidence_study_has_blockers",
            "human_promotion_not_approved",
            "human_promotion_approval_binding_mismatch");
    }

    [Fact]
    public void Evaluate_SuppliedInferenceAndConcentrationMustReconcileToTrades()
    {
        var baseline = PassingEvidence();
        var assessment = IntradayResearchPromotionEvaluator.Evaluate(
            baseline with
            {
                Concentration = baseline.Concentration! with
                {
                    LargestTickerAbsolutePnlShare = 0.01m,
                    LargestTradingDayAbsolutePnlShare = 0.01m
                },
                ClusteredInference = baseline.ClusteredInference! with
                {
                    ClusterCount = 1,
                    NetExpectancyBps = 999m
                },
                SessionBlockBootstrap = baseline.SessionBlockBootstrap! with
                {
                    SessionCount = 1,
                    NetExpectancyBps = 999m
                }
            });

        Assert.False(assessment.IsPromotable);
        AssertBlockers(
            assessment,
            "ticker_concentration_evidence_mismatch",
            "trading_day_concentration_evidence_mismatch",
            "clustered_inference_definition_or_count_mismatch",
            "clustered_inference_expectancy_mismatch",
            "session_block_bootstrap_configuration_mismatch",
            "session_block_bootstrap_expectancy_mismatch");
    }

    [Fact]
    public void Evaluate_NullPackage_FailsClosed()
    {
        var assessment = IntradayResearchPromotionEvaluator.Evaluate(null);

        Assert.False(assessment.IsPromotable);
        AssertBlockers(assessment, "promotion_evidence_missing");
    }

    private static IntradayResearchPromotionEvidence PassingEvidence()
    {
        var evaluationAt = new DateTimeOffset(
            2026,
            7,
            26,
            12,
            0,
            0,
            TimeSpan.Zero);
        var episodes = Enumerable.Range(0, 300)
            .Select(index => new IntradayPromotionEpisodeEvidence(
                $"episode-{index:D3}",
                $"story-{index:D3}",
                new DateOnly(2026, 1, 2).AddDays(index % 25),
                index < 100 ? "earnings-positive" : "other-cohort",
                $"subtype-{index % 4}",
                5 + ((index % 4) * 5)))
            .ToArray();
        var trades = Enumerable.Range(0, 100)
            .Select(index =>
            {
                var occurrenceForTicker = index / 10;
                var isLoss = occurrenceForTicker < 2;
                return new IntradayPromotionTradeEvidence(
                    $"trade-{index:D3}",
                    episodes[index].EpisodeId,
                    index < 35
                        ? IntradayResearchPartition.Development
                        : index < 70
                            ? IntradayResearchPartition.Validation
                            : IntradayResearchPartition.Holdout,
                    $"T{index % 10:D2}",
                    episodes[index].EntrySessionDate,
                    episodes[index].EventSubtype,
                    episodes[index].OpeningMinuteOfRegularSession,
                    12m,
                    isLoss ? -2m : 8m,
                    isLoss ? -25m : 100m);
            })
            .ToArray();
        var netExpectancy = trades.Average(value => value.NetReturnBps);
        var concentration = Concentration(trades);
        var evidence = new IntradayResearchPromotionEvidence(
            "intraday-catalyst-v1",
            "sha256:promotion-snapshot",
            "earnings-positive",
            evaluationAt,
            2m,
            episodes,
            trades,
            [
                new IntradayCostStressEvidence(1m, netExpectancy),
                new IntradayCostStressEvidence(2m, 3m),
                new IntradayCostStressEvidence(3m, 1m)
            ],
            new IntradayClusteredInferenceEvidence(
                IntradayClusterDefinition.CanonicalStoryEpisodeAndEntrySession,
                100,
                netExpectancy,
                0.25m,
                1m,
                10m),
            new IntradaySessionBlockBootstrapEvidence(
                25,
                10_000,
                5,
                0.95m,
                netExpectancy,
                0.50m,
                10m),
            concentration,
            LeaveOneOut(trades),
            new IntradayHumanPromotionApproval(
                true,
                "research-owner",
                evaluationAt.AddMinutes(-1),
                "promotion-decision-001",
                "intraday-catalyst-v1",
                "sha256:promotion-snapshot"),
            Report(episodes, evaluationAt));
        return evidence;
    }

    private static IntradayResearchPromotionEvidence RebuildDerivedEvidence(
        IntradayResearchPromotionEvidence evidence)
    {
        var trades = evidence.Trades!;
        var netExpectancy = trades.Average(value => value.NetReturnBps);
        return evidence with
        {
            ClusteredInference = evidence.ClusteredInference! with
            {
                ClusterCount = trades
                    .Select(value => (value.EpisodeId, value.TradingDay))
                    .Distinct()
                    .Count(),
                NetExpectancyBps = netExpectancy
            },
            SessionBlockBootstrap = evidence.SessionBlockBootstrap! with
            {
                SessionCount = trades
                    .Select(value => value.TradingDay)
                    .Distinct()
                    .Count(),
                NetExpectancyBps = netExpectancy
            },
            Concentration = Concentration(trades),
            LeaveOneOut = LeaveOneOut(trades)
        };
    }

    private static IntradayPromotionConcentrationEvidence Concentration(
        IReadOnlyList<IntradayPromotionTradeEvidence> trades)
    {
        var total = trades.Sum(value => Math.Abs(value.NetProfitLoss));
        var ticker = trades
            .GroupBy(value => value.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Share = group.Sum(value => Math.Abs(value.NetProfitLoss)) / total
            })
            .OrderByDescending(value => value.Share)
            .First();
        var day = trades
            .GroupBy(value => value.TradingDay)
            .Select(group => new
            {
                Key = group.Key,
                Share = group.Sum(value => Math.Abs(value.NetProfitLoss)) / total
            })
            .OrderByDescending(value => value.Share)
            .First();
        return new IntradayPromotionConcentrationEvidence(
            ticker.Key,
            ticker.Share,
            day.Key,
            day.Share);
    }

    private static IReadOnlyList<IntradayLeaveOneOutEvidence> LeaveOneOut(
        IReadOnlyList<IntradayPromotionTradeEvidence> trades)
    {
        var evidence = new List<IntradayLeaveOneOutEvidence>();
        Add(
            IntradayLeaveOneOutDimension.Ticker,
            trades.Select(value => value.Ticker)
                .Distinct(StringComparer.OrdinalIgnoreCase),
            (trade, key) => trade.Ticker.Equals(
                key,
                StringComparison.OrdinalIgnoreCase));
        Add(
            IntradayLeaveOneOutDimension.TradingDay,
            trades.Select(value =>
                    value.TradingDay.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal),
            (trade, key) => trade.TradingDay.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture)
                .Equals(key, StringComparison.Ordinal));
        Add(
            IntradayLeaveOneOutDimension.EventSubtype,
            trades.Select(value => value.EventSubtype)
                .Distinct(StringComparer.Ordinal),
            (trade, key) => trade.EventSubtype.Equals(
                key,
                StringComparison.Ordinal));
        Add(
            IntradayLeaveOneOutDimension.OpeningMinute,
            trades.Select(value =>
                    value.OpeningMinuteOfRegularSession.ToString(
                        CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal),
            (trade, key) => trade.OpeningMinuteOfRegularSession.ToString(
                    CultureInfo.InvariantCulture)
                .Equals(key, StringComparison.Ordinal));
        return evidence;

        void Add(
            IntradayLeaveOneOutDimension dimension,
            IEnumerable<string> keys,
            Func<IntradayPromotionTradeEvidence, string, bool> omitted)
        {
            foreach (var key in keys)
            {
                var remaining = trades.Where(value => !omitted(value, key)).ToArray();
                evidence.Add(new IntradayLeaveOneOutEvidence(
                    dimension,
                    key,
                    remaining.Length,
                    remaining.Length == 0
                        ? 0m
                        : remaining.Average(value => value.NetReturnBps)));
            }
        }
    }

    private static IntradayEvidenceStudyReport Report(
        IReadOnlyList<IntradayPromotionEpisodeEvidence> episodes,
        DateTimeOffset evaluationAt)
    {
        var observations = episodes
            .Select((episode, index) =>
            {
                var availableAt = new DateTimeOffset(
                    episode.EntrySessionDate.ToDateTime(new TimeOnly(14, 0)),
                    TimeSpan.Zero);
                return new IntradayEventEvidenceObservation(
                    $"T{index % 10:D2}",
                    episode.CanonicalStoryCluster,
                    $"revision-{index:D3}",
                    index < 100 ? "development" : "validation",
                    "regular",
                    "regular",
                    availableAt.AddMinutes(-2),
                    availableAt.AddMinutes(-1),
                    availableAt,
                    availableAt,
                    availableAt.AddMinutes(1),
                    CatalystNewsCategory.Earnings,
                    CatalystDirection.Positive,
                    CatalystMateriality.High,
                    IntradayEvidenceEligibility.PromotionEligible,
                    null,
                    2m,
                    63,
                    2m,
                    1_000_000m,
                    5m,
                    4m,
                    IntradayMorphology.PullbackReclaim,
                    [],
                    []);
            })
            .ToArray();
        return new IntradayEvidenceStudyReport(
            evaluationAt.AddMinutes(-2),
            IntradayEvidenceEligibility.PromotionEligible,
            [],
            observations,
            [
                new IntradayHolmPValue(
                    "frozen-event-family",
                    "earnings-positive-15m",
                    100,
                    0.01m,
                    0.02m,
                    true,
                    1,
                    1)
            ]);
    }

    private static void AssertBlockers(
        IntradayResearchPromotionAssessment assessment,
        params string[] expected)
    {
        var actual = assessment.Blockers
            .Select(value => value.Code)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var code in expected)
        {
            Assert.Contains(code, actual);
        }
    }
}
