using TradingFlow.Research.Momentum;
using Xunit;

namespace TradingFlow.Tests;

public sealed class SwingResearchPromotionEvaluatorTests
{
    [Fact]
    public void Evaluate_PromotesOnlyWhenEveryBindingCriterionPasses()
    {
        var fixture = PassingFixture();

        var result = SwingResearchPromotionEvaluator.Evaluate(
            fixture.Report,
            fixture.Audit,
            fixture.Candidate,
            fixture.SupplementalEvidence,
            fixture.FamilyEvidence,
            fixture.HumanApproval,
            TestBootstrapPolicy());

        Assert.True(result.IsPromotable);
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.ValidationExcessReturnInterval);
        Assert.True(result.ValidationExcessReturnInterval!.LowerBound > 0);
        Assert.NotNull(result.HoldoutExcessReturnInterval);
        Assert.True(result.HoldoutExcessReturnInterval!.LowerBound > 0);
        Assert.True(result.FamilyAdjustedEvidence!.Rejected);
        Assert.All(
            result.Criteria,
            criterion => Assert.Equal(
                SwingPromotionCriterionStatus.Passed,
                criterion.Status));
    }

    [Fact]
    public void Evaluate_FailsWhenSuppliedEvidenceViolatesPromotionThresholds()
    {
        var fixture = PassingFixture();
        var comparisons = fixture.Report.Comparisons
            .Select(value => value.Segment == MomentumStudySegment.Holdout
                ? value with { PrimaryMinusBenchmarkPct = -0.1m }
                : value)
            .ToArray();
        var robustness = fixture.Audit.Robustness
            .Select(value => value.Segment == MomentumStudySegment.Holdout
                ? value with
                {
                    PositiveFormationRatePct = 50m,
                    LargestTickerAbsolutePnlContributionPct = 10.01m,
                    LargestMonthAbsolutePnlContributionPct = 12m,
                    PortfolioPath = value.PortfolioPath! with
                    {
                        MaximumDrawdownPct = 26m
                    },
                    CostStress =
                    [
                        new(1m, 24, 1m),
                        new(2m, 24, 0m),
                        new(3m, 24, -1m)
                    ],
                    LeaveOneTicker =
                    [
                        new(
                            MomentumRobustnessDimension.Ticker,
                            "LOSS",
                            23,
                            -0.5m)
                    ],
                    MeanNetReturnWithoutBestMonthPct = 0m
                }
                : value)
            .ToArray();

        var result = SwingResearchPromotionEvaluator.Evaluate(
            fixture.Report with { Comparisons = comparisons },
            fixture.Audit with { Robustness = robustness },
            fixture.Candidate,
            fixture.SupplementalEvidence,
            fixture.FamilyEvidence,
            fixture.HumanApproval,
            TestBootstrapPolicy());

        Assert.False(result.IsPromotable);
        AssertCriterionFailed(result, "holdout_net_excess_return");
        AssertCriterionFailed(result, "holdout_positive_formation_rate");
        AssertCriterionFailed(result, "holdout_ticker_pnl_concentration");
        AssertCriterionFailed(result, "holdout_month_pnl_concentration");
        AssertCriterionFailed(result, "holdout_maximum_drawdown");
        AssertCriterionFailed(result, "holdout_cost_stress_2x");
        AssertCriterionFailed(result, "holdout_cost_stress_3x");
        AssertCriterionFailed(result, "holdout_leave_one_ticker");
        AssertCriterionFailed(result, "holdout_leave_best_month");
    }

    [Fact]
    public void Evaluate_BlocksInsteadOfAssumingMissingEvidencePasses()
    {
        var fixture = PassingFixture();

        var result = SwingResearchPromotionEvaluator.Evaluate(
            fixture.Report,
            fixture.Audit,
            fixture.Candidate,
            supplementalEvidence: null,
            familyEvidence: null,
            humanApproval: null,
            TestBootstrapPolicy());

        Assert.False(result.IsPromotable);
        AssertCriterionBlocked(result, "validation_excess_return_bootstrap");
        AssertCriterionBlocked(result, "holdout_excess_return_bootstrap");
        AssertCriterionBlocked(result, "holdout_maximum_drawdown");
        AssertCriterionBlocked(result, "multiple_testing_adjustment");
        AssertCriterionBlocked(result, "explicit_human_promotion_approval");
    }

    [Fact]
    public void Evaluate_BlocksA3A4WithoutIndependentTradeAndIssuerEvidence()
    {
        var fixture = PassingFixture(
            MomentumResearchCell.MomentumStockTrendVcpV7,
            parentCell: MomentumResearchCell.MomentumStockTrend);
        var supplemental = fixture.SupplementalEvidence with
        {
            AdditiveTradeEvidence = null
        };

        var result = SwingResearchPromotionEvaluator.Evaluate(
            fixture.Report,
            fixture.Audit,
            fixture.Candidate,
            supplemental,
            fixture.FamilyEvidence,
            fixture.HumanApproval,
            TestBootstrapPolicy());

        Assert.False(result.IsPromotable);
        AssertCriterionBlocked(result, "additive_independent_trade_breadth");
        Assert.Equal(
            SwingPromotionCriterionStatus.Passed,
            Assert.Single(
                result.Criteria,
                value => value.Criterion == "validation_incremental_return_interval").Status);
    }

    private static PromotionFixture PassingFixture(
        string cell = MomentumResearchCell.MomentumOnly,
        string? parentCell = null)
    {
        const int horizon = 21;
        var candidate = new SwingPromotionCandidate(cell, horizon);
        var dates = new Dictionary<string, DateOnly[]>(StringComparer.Ordinal)
        {
            [MomentumStudySegment.Development] =
                Dates(new DateOnly(2010, 1, 29), 60),
            [MomentumStudySegment.Validation] =
                Dates(new DateOnly(2015, 1, 30), 24),
            [MomentumStudySegment.Holdout] =
                Dates(new DateOnly(2017, 1, 31), 24)
        };
        var options = new CrossSectionalMomentumResearchOptions(
            MomentumLookbackBars: 252,
            SkipRecentBars: 21,
            FastTrendSmaBars: 50,
            SlowTrendSmaBars: 200,
            AverageDollarVolumeBars: 20,
            MinimumAverageDollarVolume: 20_000_000m,
            FormationSchedule: MomentumFormationSchedule.MonthEnd,
            DecisionCadenceBars: 21,
            ForwardHorizons: [horizon],
            QuantileCount: 10,
            PrimarySelectionFraction: 0.30m,
            MinimumCandidatesPerDate: 100,
            DevelopmentFraction: 0.60m,
            ValidationFraction: 0.20m,
            HoldoutFraction: 0.20m,
            MinimumFormationDates: 108,
            MinimumHoldoutFormationDates: 24,
            MinimumDistinctSelectedTickers: 50,
            Cells: parentCell is null ? [cell] : [parentCell, cell],
            ExchangeTimezone: "America/New_York")
        {
            ValidationStartDate = dates[MomentumStudySegment.Validation][0],
            HoldoutStartDate = dates[MomentumStudySegment.Holdout][0]
        };
        var cohorts = RequiredSegments()
            .SelectMany(segment =>
                Enumerable.Range(1, 10)
                    .Select(quantile => new MomentumCohortResult(
                        cell,
                        segment,
                        horizon,
                        quantile,
                        ObservationCount: dates[segment].Length * 30,
                        DecisionDateCount: dates[segment].Length,
                        UniqueTickerCount: 100,
                        MeanReturnPct: 11m - quantile,
                        MedianReturnPct: 11m - quantile,
                        WinRatePct: 60m,
                        LargestTickerAbsolutePnlContributionPct: 5m)))
            .ToArray();
        var comparisons = RequiredSegments()
            .Select(segment => new MomentumComparisonResult(
                cell,
                segment,
                horizon,
                DecisionDateCount: dates[segment].Length,
                PrimaryObservationCount: dates[segment].Length * 30,
                PrimaryMeanReturnPct: 2m,
                PrimaryMedianReturnPct: 2m,
                BottomMeanReturnPct: -1m,
                EligibleUniverseMeanReturnPct: 0.5m,
                BenchmarkMeanReturnPct: 0.5m,
                PrimaryMinusBottomPct: 3m,
                PrimaryMinusUniversePct: 1.5m,
                PrimaryMinusBenchmarkPct: 1.5m,
                LargestTickerAbsolutePnlContributionPct: 5m,
                LargestMonthAbsolutePnlContributionPct: 5m,
                LargestFormationAbsolutePnlContributionPct: 5m)
            {
                ParentCell = parentCell,
                ParentPrimaryMeanReturnPct = parentCell is null ? null : 1m,
                IncrementalReturnVsParentPct = parentCell is null ? null : 1m
            })
            .ToArray();
        var ledger = Enumerable.Range(1, 50)
            .Select(index => new MomentumFormationLedgerEntry(
                cell,
                parentCell,
                dates[MomentumStudySegment.Holdout][index % 24],
                new DateTimeOffset(
                    dates[MomentumStudySegment.Holdout][index % 24].ToDateTime(
                        new TimeOnly(16, 0)),
                    TimeSpan.Zero),
                MomentumStudySegment.Holdout,
                $"T{index:000}",
                index,
                Quantile: 1,
                IsPrimarySelection: true,
                IsBottomSelection: false,
                GatePassed: true,
                MomentumSlotReturnSource.Asset,
                horizon,
                MomentumReturnPct: 20m,
                AssetGrossForwardReturnPct: 2m,
                SlotGrossForwardReturnPct: 2m,
                SlotNetForwardReturnPct: 1.5m))
            .ToArray();
        var report = new CrossSectionalMomentumReport(
            StudyName: "frozen-swing-study",
            UniverseDescription: "point-in-time liquid US equities",
            BenchmarkTicker: "SPY",
            PointInTimeUniverseEvidence: true,
            AdjustedPricesConfirmed: true,
            DataEvidenceReady: true,
            PromotionEligible: true,
            PromotionBlockers: [],
            LoadedTickerCount: 500,
            DecisionDateCount: 108,
            DevelopmentDecisionDateCount: 60,
            ValidationDecisionDateCount: 24,
            HoldoutDecisionDateCount: 24,
            ValidationStartDate: options.ValidationStartDate,
            HoldoutStartDate: options.HoldoutStartDate,
            PurgedOutcomeCount: 0,
            SkippedForCandidateCount: 0,
            ExecutionCosts: new MomentumExecutionCostAssumptions(
                CommissionPerSideBps: 0m,
                RegulatoryExitBps: 1m,
                FullSpreadBps: 10m,
                SlippagePerSideBps: 5m),
            Options: options,
            Cohorts: cohorts,
            Comparisons: comparisons,
            RankObservations: [])
        {
            FormationLedger = ledger
        };

        var formations = RequiredSegments()
            .SelectMany(segment => dates[segment]
                .Select(date => Formation(cell, segment, horizon, date)))
            .ToList();
        if (parentCell is not null)
        {
            formations.AddRange(RequiredSegments()
                .SelectMany(segment => dates[segment]
                    .Select(date => Formation(
                        parentCell,
                        segment,
                        horizon,
                        date,
                        meanReturnPct: 0.5m))));
        }

        var robustness = RequiredSegments()
            .Select(segment => Robustness(
                cell,
                parentCell,
                segment,
                horizon,
                dates[segment].Length))
            .ToArray();
        var audit = new MomentumResearchAuditReport(
            Tickers: [],
            Formations: formations,
            Robustness: robustness);
        var excessReturns = new[]
            {
                MomentumStudySegment.Validation,
                MomentumStudySegment.Holdout
            }
            .SelectMany(segment => dates[segment]
                .Select(date => new SwingFormationExcessReturn(segment, date, 1.25)))
            .ToArray();
        var supplemental = new SwingPromotionSupplementalEvidence(
            excessReturns,
            EqualWeightBenchmarkMaximumDrawdownPct: 12m,
            QuantileCoherence: [],
            AdditiveTradeEvidence: parentCell is null
                ? null
                : new SwingAdditiveTradeEvidence(
                    DevelopmentIndependentTrades: 100,
                    ValidationIndependentTrades: 50,
                    HoldoutIndependentTrades: 30,
                    DistinctIssuerCount: 20,
                    PointInTimeIssuerIdentityConfirmed: true));
        var family = new SwingFamilyAdjustedEvidence(
            CandidateHypothesisId: "candidate",
            RawPValues: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["candidate"] = 0.001,
                ["other"] = 0.02
            },
            FamilyWiseAlpha: 0.05);
        var approval = new SwingHumanPromotionApproval(
            IsApproved: true,
            ApprovedBy: "research-owner",
            ApprovedAtUtc: new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
            Rationale: "Reviewed the frozen holdout and approved paper-shadow promotion.");
        return new PromotionFixture(
            report,
            audit,
            candidate,
            supplemental,
            family,
            approval);
    }

    private static MomentumFormationAudit Formation(
        string cell,
        string segment,
        int horizon,
        DateOnly date,
        decimal meanReturnPct = 1.5m) =>
        new(
            cell,
            segment,
            horizon,
            date,
            SelectedTickerCount: 30,
            MeanNetReturnPct: meanReturnPct,
            MedianNetReturnPct: meanReturnPct,
            WinRatePct: 60m,
            WorstTickerReturnPct: -1m,
            BestTickerReturnPct: 4m)
        {
            ConfiguredSlotCount = 30,
            InvestedSlotCount = 30
        };

    private static MomentumRobustnessAudit Robustness(
        string cell,
        string? parentCell,
        string segment,
        int horizon,
        int formationCount) =>
        new(
            cell,
            segment,
            horizon,
            FormationDateCount: formationCount,
            UniqueTickerCount: 100,
            EqualWeightedFormationMeanNetReturnPct: 1.5m,
            PositiveFormationRatePct: 60m,
            TopTicker: "TOP",
            TopTickerEqualWeightedPnlContributionPct: 5m,
            BottomTicker: "BOTTOM",
            BottomTickerEqualWeightedPnlContributionPct: -3m,
            TopFormationDate: new DateOnly(2017, 1, 31),
            TopFormationMeanNetReturnPct: 3m,
            BottomFormationDate: new DateOnly(2017, 2, 28),
            BottomFormationMeanNetReturnPct: -1m,
            MeanNetReturnWithoutTopTickerPct: 1m,
            MeanNetReturnWithoutTopFormationPct: 1m,
            DecisionCadenceBars: 21,
            OutcomesOverlap: false,
            ConcurrentCohortCount: 1,
            ApproximateIndependentFormationCount: formationCount,
            PortfolioPath: new MomentumPortfolioPathAudit(
                formationCount,
                FormationPeriodsPerYear: 12m,
                CumulativeNetReturnPct: 20m,
                AnnualizedNetReturnPct: 10m,
                MaximumDrawdownPct: 10m,
                AnnualizedSharpe: 1m))
        {
            ParentCell = parentCell,
            GatePassRatePct = 100m,
            LargestTickerAbsolutePnlContributionPct = 5m,
            LargestMonthAbsolutePnlContributionPct = 5m,
            LargestFormationAbsolutePnlContributionPct = 5m,
            BestMonth = "2017-01",
            MeanNetReturnWithoutBestMonthPct = 1m,
            LeaveOneTicker =
            [
                new(MomentumRobustnessDimension.Ticker, "TOP", formationCount, 1m)
            ],
            LeaveOneSector =
            [
                new(MomentumRobustnessDimension.Sector, "technology", formationCount, 1m)
            ],
            LeaveOneYear =
            [
                new(MomentumRobustnessDimension.Year, "2017", formationCount, 1m)
            ],
            CostStress =
            [
                new(1m, formationCount, 1.5m),
                new(2m, formationCount, 1m),
                new(3m, formationCount, 0.5m)
            ]
        };

    private static DateOnly[] Dates(DateOnly first, int count) =>
        Enumerable.Range(0, count)
            .Select(index => first.AddMonths(index))
            .ToArray();

    private static string[] RequiredSegments() =>
    [
        MomentumStudySegment.Development,
        MomentumStudySegment.Validation,
        MomentumStudySegment.Holdout
    ];

    private static SwingPromotionBootstrapPolicy TestBootstrapPolicy() =>
        new(BlockLength: 3, Replicates: 500, Seed: 42);

    private static void AssertCriterionFailed(
        SwingResearchPromotionAssessment result,
        string criterion) =>
        Assert.Equal(
            SwingPromotionCriterionStatus.Failed,
            Assert.Single(
                result.Criteria,
                value => value.Criterion == criterion).Status);

    private static void AssertCriterionBlocked(
        SwingResearchPromotionAssessment result,
        string criterion) =>
        Assert.Equal(
            SwingPromotionCriterionStatus.Blocked,
            Assert.Single(
                result.Criteria,
                value => value.Criterion == criterion).Status);

    private sealed record PromotionFixture(
        CrossSectionalMomentumReport Report,
        MomentumResearchAuditReport Audit,
        SwingPromotionCandidate Candidate,
        SwingPromotionSupplementalEvidence SupplementalEvidence,
        SwingFamilyAdjustedEvidence FamilyEvidence,
        SwingHumanPromotionApproval HumanApproval);
}
