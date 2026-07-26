using TradingFlow.Research.Momentum;
using Xunit;

namespace TradingFlow.Tests;

public sealed class MomentumResearchAuditAnalyzerTests
{
    [Fact]
    public void Analyze_ExplainsSegmentsAndDerivesFullPeriod()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation("development", new DateOnly(2025, 1, 1), "A", 10m),
            Observation("development", new DateOnly(2025, 1, 1), "B", 0m),
            Observation("validation", new DateOnly(2025, 2, 1), "A", -2m),
            Observation("validation", new DateOnly(2025, 2, 1), "B", 4m)
        ],
        decisionCadenceBars: 21);

        Assert.Equal(6, report.Tickers.Count);
        var full = Assert.Single(
            report.Robustness,
            value => value.Segment == MomentumStudySegment.Full);
        Assert.Equal(2, full.FormationDateCount);
        Assert.Equal(2, full.UniqueTickerCount);
        Assert.Equal(3m, full.EqualWeightedFormationMeanNetReturnPct);
        Assert.Equal(100m, full.PositiveFormationRatePct);
        Assert.Equal("A", full.TopTicker);
        Assert.Equal("B", full.BottomTicker);
        Assert.False(full.OutcomesOverlap);
        Assert.NotNull(full.PortfolioPath);
    }

    [Fact]
    public void Analyze_RecomputesMeansWithoutBestTickerAndBestFormation()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation("holdout", new DateOnly(2025, 1, 1), "A", 20m),
            Observation("holdout", new DateOnly(2025, 1, 1), "B", 0m),
            Observation("holdout", new DateOnly(2025, 2, 1), "A", -2m),
            Observation("holdout", new DateOnly(2025, 2, 1), "B", 2m)
        ],
        decisionCadenceBars: 21);

        var holdout = Assert.Single(
            report.Robustness,
            value => value.Segment == MomentumStudySegment.Holdout);
        Assert.Equal(5m, holdout.EqualWeightedFormationMeanNetReturnPct);
        Assert.Equal(0.5m, holdout.MeanNetReturnWithoutTopTickerPct);
        Assert.Equal(0m, holdout.MeanNetReturnWithoutTopFormationPct);
        Assert.Equal(new DateOnly(2025, 1, 1), holdout.TopFormationDate);
    }

    [Fact]
    public void Analyze_DoesNotCompoundOverlappingOutcomes()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation("holdout", new DateOnly(2025, 1, 1), "A", 10m) with
            {
                ForwardHorizonBars = 60
            },
            Observation("holdout", new DateOnly(2025, 2, 1), "A", 10m) with
            {
                ForwardHorizonBars = 60
            },
            Observation("holdout", new DateOnly(2025, 3, 1), "A", 10m) with
            {
                ForwardHorizonBars = 60
            }
        ],
        decisionCadenceBars: 21);

        var holdout = Assert.Single(
            report.Robustness,
            value => value.Segment == MomentumStudySegment.Holdout);
        Assert.True(holdout.OutcomesOverlap);
        Assert.Equal(3, holdout.ConcurrentCohortCount);
        Assert.Equal(1, holdout.ApproximateIndependentFormationCount);
        Assert.Null(holdout.PortfolioPath);
    }

    [Fact]
    public void Analyze_IgnoresNonPrimarySelections()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation(
                "development",
                new DateOnly(2025, 1, 1),
                "A",
                10m,
                isPrimary: false)
        ]);

        Assert.Empty(report.Tickers);
        Assert.Empty(report.Formations);
        Assert.Empty(report.Robustness);
    }

    [Fact]
    public void Analyze_ReportsFixedInvestedAndCashSlots()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation(
                "validation",
                new DateOnly(2025, 1, 1),
                "A",
                4m) with
            {
                Cell = MomentumResearchCell.MomentumStockTrend,
                ParentCell = MomentumResearchCell.MomentumOnly
            },
            Observation(
                "validation",
                new DateOnly(2025, 1, 1),
                "B",
                0.1m) with
            {
                Cell = MomentumResearchCell.MomentumStockTrend,
                ParentCell = MomentumResearchCell.MomentumOnly,
                GatePassed = false,
                SlotReturnSource = MomentumSlotReturnSource.Cash,
                AssetGrossForwardReturnPct = -8m
            }
        ]);

        var formation = Assert.Single(
            report.Formations,
            value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.Segment == MomentumStudySegment.Validation);
        Assert.Equal(2, formation.ConfiguredSlotCount);
        Assert.Equal(1, formation.InvestedSlotCount);
        Assert.Equal(1, formation.CashSlotCount);
        Assert.Equal(1, formation.GateFailureCount);
        var robustness = Assert.Single(
            report.Robustness,
            value =>
                value.Cell == MomentumResearchCell.MomentumStockTrend &&
                value.Segment == MomentumStudySegment.Validation);
        Assert.Equal(MomentumResearchCell.MomentumOnly, robustness.ParentCell);
        Assert.Equal(50m, robustness.GatePassRatePct);
        Assert.Equal(1, robustness.CashSlotObservationCount);
    }

    [Fact]
    public void Analyze_ReportsPnlConcentrationLeaveOneOutAndFrozenCostStress()
    {
        var observations = new[]
        {
            Observation("validation", new DateOnly(2024, 1, 31), "A", 10m),
            Observation("validation", new DateOnly(2024, 1, 31), "B", -2m),
            Observation("validation", new DateOnly(2024, 2, 29), "A", 4m),
            Observation("validation", new DateOnly(2024, 2, 29), "B", 0m),
            Observation("validation", new DateOnly(2025, 1, 31), "A", -1m),
            Observation("validation", new DateOnly(2025, 1, 31), "B", 3m)
        };
        var sectors = observations
            .Select(value => value.DecisionDate)
            .Distinct()
            .ToDictionary(
                date => date,
                _ => (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["A"] = "technology",
                        ["B"] = "financials"
                    });
        var evidence = new MomentumAdditiveResearchEvidence(
            new Dictionary<DateOnly, IReadOnlySet<string>>(),
            sectors)
        {
            PointInTimeSectorCoverageConfirmed = true
        };
        var costs = new MomentumExecutionCostAssumptions(
            CommissionPerSideBps: 10m,
            RegulatoryExitBps: 0m,
            FullSpreadBps: 40m,
            SlippagePerSideBps: 20m);

        var report = new MomentumResearchAuditAnalyzer().Analyze(
            observations,
            decisionCadenceBars: 21,
            executionCosts: costs,
            additiveEvidence: evidence);

        var validation = Assert.Single(
            report.Robustness,
            value => value.Segment == MomentumStudySegment.Validation);
        Assert.Equal(75m, validation.LargestTickerAbsolutePnlContributionPct);
        Assert.Equal(60m, validation.LargestMonthAbsolutePnlContributionPct);
        Assert.Equal(60m, validation.LargestFormationAbsolutePnlContributionPct);
        Assert.Equal("2024-01", validation.BestMonth);
        Assert.Equal(1.5m, validation.MeanNetReturnWithoutBestMonthPct);
        Assert.Equal(2, validation.LeaveOneTicker.Count);
        Assert.Equal(2, validation.LeaveOneSector.Count);
        Assert.Equal(2, validation.LeaveOneYear.Count);
        Assert.Equal([1m, 2m, 3m], validation.CostStress.Select(value => value.CostMultiplier));
        Assert.Equal(
            [1.333333m, 0.333333m, -0.666667m],
            validation.CostStress.Select(value =>
                value.EqualWeightedFormationMeanNetReturnPct));
        Assert.Empty(validation.RobustnessBlockers);
    }

    [Fact]
    public void Analyze_FailsClosedForSectorAndCostAuditsWithoutEvidence()
    {
        var report = new MomentumResearchAuditAnalyzer().Analyze(
        [
            Observation("development", new DateOnly(2025, 1, 1), "A", 1m)
        ]);

        var robustness = Assert.Single(
            report.Robustness,
            value => value.Segment == MomentumStudySegment.Development);
        Assert.Empty(robustness.LeaveOneSector);
        Assert.Empty(robustness.CostStress);
        Assert.Contains(
            "point_in_time_sector_evidence_unavailable",
            robustness.RobustnessBlockers);
        Assert.Contains(
            "execution_cost_assumptions_unavailable",
            robustness.RobustnessBlockers);
    }

    private static MomentumRankObservation Observation(
        string segment,
        DateOnly date,
        string ticker,
        decimal returnPct,
        bool isPrimary = true) =>
        new(
            MomentumResearchCell.MomentumOnly,
            date,
            new DateTimeOffset(
                date.ToDateTime(TimeOnly.MinValue),
                TimeSpan.Zero),
            segment,
            ticker,
            1,
            1,
            isPrimary,
            false,
            10m,
            100_000_000m,
            20,
            returnPct,
            returnPct,
            returnPct,
            Math.Max(returnPct, 0m),
            Math.Min(returnPct, 0m));
}
