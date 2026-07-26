using TradingFlow.Research.Statistics;

namespace TradingFlow.Research.Momentum;

public enum SwingPromotionCriterionStatus
{
    Passed = 0,
    Failed = 1,
    Blocked = 2
}

public sealed record SwingPromotionCandidate(
    string Cell,
    int ForwardHorizonBars);

public sealed record SwingFormationExcessReturn(
    string Segment,
    DateOnly DecisionDate,
    double NetExcessReturnPct);

public sealed record SwingQuantileCoherenceAssessment(
    string Segment,
    bool IsEconomicallyCoherent,
    string Rationale);

public sealed record SwingAdditiveTradeEvidence(
    int DevelopmentIndependentTrades,
    int ValidationIndependentTrades,
    int HoldoutIndependentTrades,
    int DistinctIssuerCount,
    bool PointInTimeIssuerIdentityConfirmed);

public sealed record SwingPromotionSupplementalEvidence(
    IReadOnlyList<SwingFormationExcessReturn> FormationExcessReturns,
    decimal? EqualWeightBenchmarkMaximumDrawdownPct,
    IReadOnlyList<SwingQuantileCoherenceAssessment> QuantileCoherence,
    SwingAdditiveTradeEvidence? AdditiveTradeEvidence);

public sealed record SwingFamilyAdjustedEvidence(
    string CandidateHypothesisId,
    IReadOnlyDictionary<string, double> RawPValues,
    double FamilyWiseAlpha);

public sealed record SwingHumanPromotionApproval(
    bool IsApproved,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    string Rationale);

public sealed record SwingPromotionBootstrapPolicy(
    int BlockLength,
    int Replicates,
    int Seed)
{
    public static SwingPromotionBootstrapPolicy Default { get; } =
        new(BlockLength: 3, Replicates: 10_000, Seed: 20_260_726);
}

public sealed record SwingPromotionCriterionResult(
    string Criterion,
    SwingPromotionCriterionStatus Status,
    string Detail);

public sealed record SwingResearchPromotionAssessment(
    string StudyName,
    string Cell,
    int ForwardHorizonBars,
    bool IsPromotable,
    IReadOnlyList<SwingPromotionCriterionResult> Criteria,
    BlockBootstrapInterval? ValidationExcessReturnInterval,
    BlockBootstrapInterval? HoldoutExcessReturnInterval,
    BlockBootstrapInterval? ValidationIncrementalReturnInterval,
    AdjustedHypothesisPValue? FamilyAdjustedEvidence)
{
    public IReadOnlyList<SwingPromotionCriterionResult> Blockers =>
        Criteria
            .Where(result => result.Status != SwingPromotionCriterionStatus.Passed)
            .ToArray();
}

/// <summary>
/// Applies the binding swing promotion contract to immutable research reports.
/// Missing or inconsistent evidence blocks promotion instead of being inferred.
/// </summary>
public static class SwingResearchPromotionEvaluator
{
    private static readonly string[] RequiredSegments =
    [
        MomentumStudySegment.Development,
        MomentumStudySegment.Validation,
        MomentumStudySegment.Holdout
    ];

    private static readonly decimal[] RequiredCostMultipliers = [1m, 2m, 3m];

    public static SwingResearchPromotionAssessment Evaluate(
        CrossSectionalMomentumReport report,
        MomentumResearchAuditReport audit,
        SwingPromotionCandidate candidate,
        SwingPromotionSupplementalEvidence? supplementalEvidence,
        SwingFamilyAdjustedEvidence? familyEvidence,
        SwingHumanPromotionApproval? humanApproval,
        SwingPromotionBootstrapPolicy? bootstrapPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Cell);
        if (candidate.ForwardHorizonBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }

        bootstrapPolicy ??= SwingPromotionBootstrapPolicy.Default;
        ValidateBootstrapPolicy(bootstrapPolicy);

        var criteria = new List<SwingPromotionCriterionResult>();
        var comparisons = RequiredSegments.ToDictionary(
            segment => segment,
            segment => UniqueOrNull(
                report.Comparisons,
                value =>
                    value.Cell == candidate.Cell &&
                    value.Segment == segment &&
                    value.ForwardHorizonBars == candidate.ForwardHorizonBars),
            StringComparer.Ordinal);
        var robustness = RequiredSegments.ToDictionary(
            segment => segment,
            segment => UniqueOrNull(
                audit.Robustness,
                value =>
                    value.Cell == candidate.Cell &&
                    value.Segment == segment &&
                    value.ForwardHorizonBars == candidate.ForwardHorizonBars),
            StringComparer.Ordinal);

        EvaluateSourceEvidence(report, candidate, criteria);
        EvaluateCandidateCoverage(
            report,
            audit,
            candidate,
            comparisons,
            robustness,
            criteria);
        EvaluateQuantileCoherence(
            report,
            candidate,
            supplementalEvidence,
            criteria);
        EvaluateNetExcessReturns(comparisons, criteria);
        EvaluateAdditiveReturns(
            audit,
            candidate,
            comparisons,
            bootstrapPolicy,
            criteria,
            out var validationIncrementalInterval);
        EvaluateFormationQuality(robustness, criteria);
        EvaluateConcentration(robustness[MomentumStudySegment.Holdout], criteria);
        EvaluateDrawdown(
            robustness[MomentumStudySegment.Holdout],
            supplementalEvidence,
            criteria);
        EvaluateCostStress(robustness[MomentumStudySegment.Holdout], criteria);
        EvaluateLeaveOneOut(robustness[MomentumStudySegment.Holdout], criteria);
        EvaluateAdditiveTradeEvidence(candidate, supplementalEvidence, criteria);

        var validationInterval = EvaluateBootstrapInterval(
            MomentumStudySegment.Validation,
            confidenceLevel: 0.90,
            audit,
            candidate,
            supplementalEvidence,
            bootstrapPolicy,
            criteria);
        var holdoutInterval = EvaluateBootstrapInterval(
            MomentumStudySegment.Holdout,
            confidenceLevel: 0.95,
            audit,
            candidate,
            supplementalEvidence,
            bootstrapPolicy,
            criteria);
        var adjustedEvidence = EvaluateFamilyEvidence(familyEvidence, criteria);
        EvaluateHumanApproval(humanApproval, criteria);

        return new SwingResearchPromotionAssessment(
            report.StudyName,
            candidate.Cell,
            candidate.ForwardHorizonBars,
            criteria.All(result => result.Status == SwingPromotionCriterionStatus.Passed),
            criteria,
            validationInterval,
            holdoutInterval,
            validationIncrementalInterval,
            adjustedEvidence);
    }

    private static void EvaluateSourceEvidence(
        CrossSectionalMomentumReport report,
        SwingPromotionCandidate candidate,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        var sourceFailures = new List<string>();
        if (!report.PointInTimeUniverseEvidence)
        {
            sourceFailures.Add("point-in-time universe evidence is absent");
        }

        if (!report.AdjustedPricesConfirmed)
        {
            sourceFailures.Add("adjusted prices are not confirmed");
        }

        if (!report.DataEvidenceReady)
        {
            sourceFailures.Add("source data evidence is not ready");
        }

        if (report.ExecutionCosts is null)
        {
            sourceFailures.Add("execution costs are absent");
        }

        sourceFailures.AddRange(
            report.PromotionBlockers.Select(blocker => $"source report: {blocker}"));
        Add(
            criteria,
            "source_evidence",
            sourceFailures.Count == 0
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Blocked,
            sourceFailures.Count == 0
                ? "Point-in-time universe, adjusted prices, costs, and source evidence are complete."
                : String.Join("; ", sourceFailures));

        Add(
            criteria,
            "candidate_registered_in_frozen_study",
            report.Options.Cells.Contains(candidate.Cell, StringComparer.Ordinal) &&
            report.Options.ForwardHorizons.Contains(candidate.ForwardHorizonBars)
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Blocked,
            report.Options.Cells.Contains(candidate.Cell, StringComparer.Ordinal) &&
            report.Options.ForwardHorizons.Contains(candidate.ForwardHorizonBars)
                ? "Candidate cell and horizon are part of the frozen report options."
                : "Candidate cell or horizon is absent from the frozen report options.");
    }

    private static void EvaluateCandidateCoverage(
        CrossSectionalMomentumReport report,
        MomentumResearchAuditReport audit,
        SwingPromotionCandidate candidate,
        IReadOnlyDictionary<string, MomentumComparisonResult?> comparisons,
        IReadOnlyDictionary<string, MomentumRobustnessAudit?> robustness,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        var minimumDates = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MomentumStudySegment.Development] = 60,
            [MomentumStudySegment.Validation] = 24,
            [MomentumStudySegment.Holdout] = 24
        };
        var reportDates = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MomentumStudySegment.Development] = report.DevelopmentDecisionDateCount,
            [MomentumStudySegment.Validation] = report.ValidationDecisionDateCount,
            [MomentumStudySegment.Holdout] = report.HoldoutDecisionDateCount
        };

        foreach (var segment in RequiredSegments)
        {
            var segmentRobustness = robustness[segment];
            var candidateDates = segmentRobustness?.FormationDateCount;
            var minimum = minimumDates[segment];
            var status = comparisons[segment] is null || segmentRobustness is null
                ? SwingPromotionCriterionStatus.Blocked
                : reportDates[segment] >= minimum && candidateDates >= minimum
                    ? SwingPromotionCriterionStatus.Passed
                    : SwingPromotionCriterionStatus.Failed;
            Add(
                criteria,
                $"{segment}_formation_dates",
                status,
                candidateDates is null
                    ? $"Candidate comparison or robustness evidence is missing for {segment}."
                    : $"Report={reportDates[segment]}, candidate={candidateDates}, required={minimum}.");
        }

        var holdoutSelections = report.FormationLedger
            .Where(value =>
                value.Cell == candidate.Cell &&
                value.Segment == MomentumStudySegment.Holdout &&
                value.ForwardHorizonBars == candidate.ForwardHorizonBars &&
                value.IsPrimarySelection)
            .Select(value => value.Ticker)
            .Where(value => !String.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Add(
            criteria,
            "holdout_distinct_selected_securities",
            holdoutSelections >= 50
                ? SwingPromotionCriterionStatus.Passed
                : report.FormationLedger.Count == 0
                    ? SwingPromotionCriterionStatus.Blocked
                    : SwingPromotionCriterionStatus.Failed,
            report.FormationLedger.Count == 0
                ? "Formation ledger is missing."
                : $"Observed={holdoutSelections}, required=50.");

        var duplicateFormationRows = audit.Formations
            .Where(value =>
                value.Cell == candidate.Cell &&
                value.ForwardHorizonBars == candidate.ForwardHorizonBars &&
                RequiredSegments.Contains(value.Segment, StringComparer.Ordinal))
            .GroupBy(value => new { value.Segment, value.DecisionDate })
            .Any(group => group.Count() != 1);
        Add(
            criteria,
            "candidate_formation_audit_integrity",
            duplicateFormationRows
                ? SwingPromotionCriterionStatus.Blocked
                : SwingPromotionCriterionStatus.Passed,
            duplicateFormationRows
                ? "Candidate formation audit contains duplicate segment/date rows."
                : "Candidate formation audit has at most one row per segment and date.");
    }

    private static void EvaluateQuantileCoherence(
        CrossSectionalMomentumReport report,
        SwingPromotionCandidate candidate,
        SwingPromotionSupplementalEvidence? supplementalEvidence,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        foreach (var segment in RequiredSegments)
        {
            var quantiles = report.Cohorts
                .Where(value =>
                    value.Cell == candidate.Cell &&
                    value.Segment == segment &&
                    value.ForwardHorizonBars == candidate.ForwardHorizonBars)
                .OrderBy(value => value.Quantile)
                .ToArray();
            var complete =
                quantiles.Length == report.Options.QuantileCount &&
                quantiles.Select(value => value.Quantile)
                    .SequenceEqual(Enumerable.Range(1, report.Options.QuantileCount)) &&
                quantiles.All(value => value.DecisionDateCount > 0);
            if (!complete)
            {
                Add(
                    criteria,
                    $"{segment}_momentum_quantiles",
                    SwingPromotionCriterionStatus.Blocked,
                    $"Complete quantile evidence is unavailable for {segment}.");
                continue;
            }

            var monotonic = quantiles
                .Zip(quantiles.Skip(1))
                .All(pair => pair.First.MeanReturnPct >= pair.Second.MeanReturnPct);
            var assessment = supplementalEvidence?.QuantileCoherence
                .SingleOrDefault(value => value.Segment == segment);
            var coherentOverride =
                assessment?.IsEconomicallyCoherent == true &&
                !String.IsNullOrWhiteSpace(assessment.Rationale);
            Add(
                criteria,
                $"{segment}_momentum_quantiles",
                monotonic || coherentOverride
                    ? SwingPromotionCriterionStatus.Passed
                    : assessment is null || String.IsNullOrWhiteSpace(assessment.Rationale)
                        ? SwingPromotionCriterionStatus.Blocked
                        : SwingPromotionCriterionStatus.Failed,
                monotonic
                    ? "Quantile mean returns are monotonic from strongest to weakest momentum."
                    : coherentOverride
                        ? $"Explicit economic-coherence evidence: {assessment!.Rationale}"
                        : assessment is null || String.IsNullOrWhiteSpace(assessment.Rationale)
                            ? "Quantiles are not monotonic and no reasoned coherence evidence was supplied."
                            : $"Quantiles are not monotonic and were assessed incoherent: {assessment.Rationale}");
        }
    }

    private static void EvaluateNetExcessReturns(
        IReadOnlyDictionary<string, MomentumComparisonResult?> comparisons,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        foreach (var segment in RequiredSegments)
        {
            var comparison = comparisons[segment];
            if (comparison is null)
            {
                Add(
                    criteria,
                    $"{segment}_net_excess_return",
                    SwingPromotionCriterionStatus.Blocked,
                    $"A unique comparison is unavailable for {segment}.");
                continue;
            }

            if (comparison.BenchmarkBlockers.Count > 0)
            {
                Add(
                    criteria,
                    $"{segment}_net_excess_return",
                    SwingPromotionCriterionStatus.Blocked,
                    $"Benchmark evidence is blocked: {String.Join(", ", comparison.BenchmarkBlockers)}.");
                continue;
            }

            Add(
                criteria,
                $"{segment}_net_excess_return",
                comparison.PrimaryMinusBenchmarkPct > 0m
                    ? SwingPromotionCriterionStatus.Passed
                    : SwingPromotionCriterionStatus.Failed,
                $"Net excess return={comparison.PrimaryMinusBenchmarkPct:0.######}%.");
        }
    }

    private static void EvaluateAdditiveReturns(
        MomentumResearchAuditReport audit,
        SwingPromotionCandidate candidate,
        IReadOnlyDictionary<string, MomentumComparisonResult?> comparisons,
        SwingPromotionBootstrapPolicy bootstrapPolicy,
        ICollection<SwingPromotionCriterionResult> criteria,
        out BlockBootstrapInterval? validationIncrementalInterval)
    {
        validationIncrementalInterval = null;
        var parentCell = MomentumResearchCell.ParentOf(candidate.Cell);
        if (parentCell is null)
        {
            Add(
                criteria,
                "accepted_addition_incremental_return",
                SwingPromotionCriterionStatus.Passed,
                "Baseline momentum cell has no additive gate.");
            return;
        }

        foreach (var segment in RequiredSegments)
        {
            var comparison = comparisons[segment];
            Add(
                criteria,
                $"{segment}_incremental_return",
                comparison?.IncrementalReturnVsParentPct is null
                    ? SwingPromotionCriterionStatus.Blocked
                    : comparison.IncrementalReturnVsParentPct > 0m
                        ? SwingPromotionCriterionStatus.Passed
                        : SwingPromotionCriterionStatus.Failed,
                comparison?.IncrementalReturnVsParentPct is null
                    ? $"Incremental return against parent '{parentCell}' is unavailable."
                    : $"Incremental return={comparison.IncrementalReturnVsParentPct:0.######}%.");
        }

        if (!RequiresAdditiveTradeEvidence(candidate.Cell))
        {
            return;
        }

        var child = FormationRows(
            audit,
            candidate,
            MomentumStudySegment.Validation);
        var parent = audit.Formations
            .Where(value =>
                value.Cell == parentCell &&
                value.Segment == MomentumStudySegment.Validation &&
                value.ForwardHorizonBars == candidate.ForwardHorizonBars)
            .ToDictionary(value => value.DecisionDate);
        if (child.Count == 0 ||
            child.Any(value => !parent.ContainsKey(value.DecisionDate)) ||
            child.Count < bootstrapPolicy.BlockLength)
        {
            Add(
                criteria,
                "validation_incremental_return_interval",
                SwingPromotionCriterionStatus.Blocked,
                "Paired validation formation evidence for the additive cell and parent is incomplete.");
            return;
        }

        var increments = child
            .OrderBy(value => value.DecisionDate)
            .Select(value =>
                (double)(value.MeanNetReturnPct - parent[value.DecisionDate].MeanNetReturnPct))
            .ToArray();
        validationIncrementalInterval = ResearchInference.BlockBootstrapMean(
            increments,
            bootstrapPolicy.BlockLength,
            bootstrapPolicy.Replicates,
            confidenceLevel: 0.90,
            bootstrapPolicy.Seed + 17);
        Add(
            criteria,
            "validation_incremental_return_interval",
            validationIncrementalInterval.Estimate <= 0
                ? SwingPromotionCriterionStatus.Failed
                : validationIncrementalInterval.LowerBound > 0
                    ? SwingPromotionCriterionStatus.Passed
                    : SwingPromotionCriterionStatus.Blocked,
            $"Estimate={validationIncrementalInterval.Estimate:0.######}%, " +
            $"90% lower={validationIncrementalInterval.LowerBound:0.######}%.");
    }

    private static void EvaluateFormationQuality(
        IReadOnlyDictionary<string, MomentumRobustnessAudit?> robustness,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        var holdout = robustness[MomentumStudySegment.Holdout];
        Add(
            criteria,
            "holdout_positive_formation_rate",
            holdout is null
                ? SwingPromotionCriterionStatus.Blocked
                : holdout.PositiveFormationRatePct > 50m
                    ? SwingPromotionCriterionStatus.Passed
                    : SwingPromotionCriterionStatus.Failed,
            holdout is null
                ? "Holdout robustness evidence is unavailable."
                : $"Observed={holdout.PositiveFormationRatePct:0.######}%, required>50%.");
    }

    private static void EvaluateConcentration(
        MomentumRobustnessAudit? holdout,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        if (holdout is null)
        {
            Add(
                criteria,
                "holdout_ticker_pnl_concentration",
                SwingPromotionCriterionStatus.Blocked,
                "Holdout robustness evidence is unavailable.");
            Add(
                criteria,
                "holdout_month_pnl_concentration",
                SwingPromotionCriterionStatus.Blocked,
                "Holdout robustness evidence is unavailable.");
            return;
        }

        Add(
            criteria,
            "holdout_ticker_pnl_concentration",
            holdout.LargestTickerAbsolutePnlContributionPct <= 10m
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Largest ticker={holdout.LargestTickerAbsolutePnlContributionPct:0.######}%, limit=10%.");
        Add(
            criteria,
            "holdout_month_pnl_concentration",
            holdout.LargestMonthAbsolutePnlContributionPct <= 10m
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Largest month={holdout.LargestMonthAbsolutePnlContributionPct:0.######}%, limit=10%.");
    }

    private static void EvaluateDrawdown(
        MomentumRobustnessAudit? holdout,
        SwingPromotionSupplementalEvidence? supplementalEvidence,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        if (holdout?.PortfolioPath is null)
        {
            Add(
                criteria,
                "holdout_maximum_drawdown",
                SwingPromotionCriterionStatus.Blocked,
                "Non-overlapping holdout portfolio-path evidence is unavailable.");
            return;
        }

        var strategyDrawdown = Math.Abs(holdout.PortfolioPath.MaximumDrawdownPct);
        var benchmarkDrawdown = supplementalEvidence?.EqualWeightBenchmarkMaximumDrawdownPct;
        if (benchmarkDrawdown is null || benchmarkDrawdown < 0m)
        {
            Add(
                criteria,
                "holdout_maximum_drawdown",
                SwingPromotionCriterionStatus.Blocked,
                "Equal-weight benchmark maximum drawdown evidence is unavailable or invalid.");
            return;
        }

        var benchmarkLimit = 1.25m * benchmarkDrawdown.Value;
        Add(
            criteria,
            "holdout_maximum_drawdown",
            strategyDrawdown <= 25m && strategyDrawdown <= benchmarkLimit
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Strategy={strategyDrawdown:0.######}%, absolute limit=25%, " +
            $"benchmark={benchmarkDrawdown:0.######}%, relative limit={benchmarkLimit:0.######}%.");
    }

    private static void EvaluateCostStress(
        MomentumRobustnessAudit? holdout,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        foreach (var multiplier in RequiredCostMultipliers)
        {
            var matches = holdout?.CostStress
                .Where(value => value.CostMultiplier == multiplier)
                .ToArray() ?? [];
            Add(
                criteria,
                $"holdout_cost_stress_{multiplier:0}x",
                matches.Length != 1
                    ? SwingPromotionCriterionStatus.Blocked
                    : matches[0].EqualWeightedFormationMeanNetReturnPct > 0m
                        ? SwingPromotionCriterionStatus.Passed
                        : SwingPromotionCriterionStatus.Failed,
                matches.Length != 1
                    ? $"Exactly one {multiplier:0}x cost-stress result is required."
                    : $"Mean net return={matches[0].EqualWeightedFormationMeanNetReturnPct:0.######}%.");
        }
    }

    private static void EvaluateLeaveOneOut(
        MomentumRobustnessAudit? holdout,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        EvaluateLeaveOneDimension(
            holdout?.LeaveOneTicker,
            "holdout_leave_one_ticker",
            criteria);
        EvaluateLeaveOneDimension(
            holdout?.LeaveOneSector,
            "holdout_leave_one_sector",
            criteria);
        EvaluateLeaveOneDimension(
            holdout?.LeaveOneYear,
            "holdout_leave_one_year",
            criteria);

        Add(
            criteria,
            "holdout_leave_best_month",
            holdout?.MeanNetReturnWithoutBestMonthPct is null ||
            String.IsNullOrWhiteSpace(holdout.BestMonth)
                ? SwingPromotionCriterionStatus.Blocked
                : holdout.MeanNetReturnWithoutBestMonthPct > 0m
                    ? SwingPromotionCriterionStatus.Passed
                    : SwingPromotionCriterionStatus.Failed,
            holdout?.MeanNetReturnWithoutBestMonthPct is null ||
            String.IsNullOrWhiteSpace(holdout.BestMonth)
                ? "Leave-best-month evidence is unavailable."
                : $"Excluding {holdout.BestMonth}: " +
                  $"{holdout.MeanNetReturnWithoutBestMonthPct:0.######}%.");
    }

    private static void EvaluateLeaveOneDimension(
        IReadOnlyList<MomentumLeaveOneOutAudit>? values,
        string criterion,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        if (values is null || values.Count == 0)
        {
            Add(
                criteria,
                criterion,
                SwingPromotionCriterionStatus.Blocked,
                "Leave-one-out evidence is unavailable.");
            return;
        }

        var worst = values.Min(value => value.EqualWeightedFormationMeanNetReturnPct);
        Add(
            criteria,
            criterion,
            worst > 0m
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Worst leave-one-out mean net return={worst:0.######}% across {values.Count} exclusions.");
    }

    private static void EvaluateAdditiveTradeEvidence(
        SwingPromotionCandidate candidate,
        SwingPromotionSupplementalEvidence? supplementalEvidence,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        if (!RequiresAdditiveTradeEvidence(candidate.Cell))
        {
            Add(
                criteria,
                "additive_independent_trade_breadth",
                SwingPromotionCriterionStatus.Passed,
                "The baseline or trend-only cell does not require the A3/A4 trade-count gate.");
            return;
        }

        var evidence = supplementalEvidence?.AdditiveTradeEvidence;
        if (evidence is null || !evidence.PointInTimeIssuerIdentityConfirmed)
        {
            Add(
                criteria,
                "additive_independent_trade_breadth",
                SwingPromotionCriterionStatus.Blocked,
                "A3/A4 independent-trade counts and point-in-time issuer identity are unavailable.");
            return;
        }

        var passes =
            evidence.DevelopmentIndependentTrades >= 100 &&
            evidence.ValidationIndependentTrades >= 50 &&
            evidence.HoldoutIndependentTrades >= 30 &&
            evidence.DistinctIssuerCount >= 20;
        Add(
            criteria,
            "additive_independent_trade_breadth",
            passes
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Trades dev/validation/holdout=" +
            $"{evidence.DevelopmentIndependentTrades}/" +
            $"{evidence.ValidationIndependentTrades}/" +
            $"{evidence.HoldoutIndependentTrades}; issuers={evidence.DistinctIssuerCount}. " +
            "Required=100/50/30 trades and 20 issuers.");
    }

    private static BlockBootstrapInterval? EvaluateBootstrapInterval(
        string segment,
        double confidenceLevel,
        MomentumResearchAuditReport audit,
        SwingPromotionCandidate candidate,
        SwingPromotionSupplementalEvidence? supplementalEvidence,
        SwingPromotionBootstrapPolicy bootstrapPolicy,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        var criterion = $"{segment}_excess_return_bootstrap";
        var formationDates = FormationRows(audit, candidate, segment)
            .Select(value => value.DecisionDate)
            .OrderBy(value => value)
            .ToArray();
        var supplied = supplementalEvidence?.FormationExcessReturns
            .Where(value => value.Segment == segment)
            .OrderBy(value => value.DecisionDate)
            .ToArray() ?? [];
        var suppliedDates = supplied.Select(value => value.DecisionDate).ToArray();
        var finite = supplied.All(value => Double.IsFinite(value.NetExcessReturnPct));
        if (formationDates.Length == 0 ||
            supplied.Length != formationDates.Length ||
            !suppliedDates.SequenceEqual(formationDates) ||
            suppliedDates.Distinct().Count() != suppliedDates.Length ||
            !finite ||
            supplied.Length < bootstrapPolicy.BlockLength)
        {
            Add(
                criteria,
                criterion,
                SwingPromotionCriterionStatus.Blocked,
                "Dated formation-level benchmark-excess evidence is missing, non-finite, " +
                "duplicated, or inconsistent with the candidate formation audit.");
            return null;
        }

        var interval = ResearchInference.BlockBootstrapMean(
            supplied.Select(value => value.NetExcessReturnPct).ToArray(),
            bootstrapPolicy.BlockLength,
            bootstrapPolicy.Replicates,
            confidenceLevel,
            bootstrapPolicy.Seed + (segment == MomentumStudySegment.Validation ? 31 : 47));
        Add(
            criteria,
            criterion,
            interval.LowerBound > 0
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Estimate={interval.Estimate:0.######}%, " +
            $"{confidenceLevel:P0} lower={interval.LowerBound:0.######}%.");
        return interval;
    }

    private static AdjustedHypothesisPValue? EvaluateFamilyEvidence(
        SwingFamilyAdjustedEvidence? familyEvidence,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        if (familyEvidence is null ||
            String.IsNullOrWhiteSpace(familyEvidence.CandidateHypothesisId) ||
            familyEvidence.RawPValues.Count == 0)
        {
            Add(
                criteria,
                "multiple_testing_adjustment",
                SwingPromotionCriterionStatus.Blocked,
                "Frozen-family raw p-values and candidate hypothesis identity are unavailable.");
            return null;
        }

        if (!familyEvidence.RawPValues.ContainsKey(familyEvidence.CandidateHypothesisId))
        {
            Add(
                criteria,
                "multiple_testing_adjustment",
                SwingPromotionCriterionStatus.Blocked,
                "The candidate hypothesis is absent from the supplied frozen family.");
            return null;
        }

        IReadOnlyList<AdjustedHypothesisPValue> adjusted;
        try
        {
            adjusted = ResearchInference.HolmAdjust(
                familyEvidence.RawPValues,
                familyEvidence.FamilyWiseAlpha);
        }
        catch (ArgumentException exception)
        {
            Add(
                criteria,
                "multiple_testing_adjustment",
                SwingPromotionCriterionStatus.Blocked,
                $"Family evidence is invalid: {exception.Message}");
            return null;
        }

        var candidate = adjusted.Single(
            value => value.HypothesisId == familyEvidence.CandidateHypothesisId);
        Add(
            criteria,
            "multiple_testing_adjustment",
            candidate.Rejected
                ? SwingPromotionCriterionStatus.Passed
                : SwingPromotionCriterionStatus.Failed,
            $"Holm adjusted p={candidate.AdjustedPValue:0.######}, " +
            $"family alpha={familyEvidence.FamilyWiseAlpha:0.######}, " +
            $"family size={familyEvidence.RawPValues.Count}.");
        return candidate;
    }

    private static void EvaluateHumanApproval(
        SwingHumanPromotionApproval? approval,
        ICollection<SwingPromotionCriterionResult> criteria)
    {
        var complete =
            approval?.IsApproved == true &&
            !String.IsNullOrWhiteSpace(approval.ApprovedBy) &&
            !String.IsNullOrWhiteSpace(approval.Rationale) &&
            approval.ApprovedAtUtc.Offset == TimeSpan.Zero;
        Add(
            criteria,
            "explicit_human_promotion_approval",
            complete
                ? SwingPromotionCriterionStatus.Passed
                : approval is null ||
                  String.IsNullOrWhiteSpace(approval.ApprovedBy) ||
                  String.IsNullOrWhiteSpace(approval.Rationale) ||
                  approval.ApprovedAtUtc.Offset != TimeSpan.Zero
                    ? SwingPromotionCriterionStatus.Blocked
                    : SwingPromotionCriterionStatus.Failed,
            complete
                ? $"Approved by {approval!.ApprovedBy} at {approval.ApprovedAtUtc:O}."
                : approval is null
                    ? "Explicit human approval is unavailable."
                    : "Approval was denied or lacks UTC timestamp, approver identity, or rationale.");
    }

    private static IReadOnlyList<MomentumFormationAudit> FormationRows(
        MomentumResearchAuditReport audit,
        SwingPromotionCandidate candidate,
        string segment) =>
        audit.Formations
            .Where(value =>
                value.Cell == candidate.Cell &&
                value.Segment == segment &&
                value.ForwardHorizonBars == candidate.ForwardHorizonBars)
            .OrderBy(value => value.DecisionDate)
            .ToArray();

    private static bool RequiresAdditiveTradeEvidence(string cell) =>
        cell == MomentumResearchCell.MomentumStockTrendVcpV7 ||
        MomentumResearchCell.RequiresCatalyst(cell);

    private static T? UniqueOrNull<T>(
        IEnumerable<T> source,
        Func<T, bool> predicate)
        where T : class
    {
        var matches = source.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void Add(
        ICollection<SwingPromotionCriterionResult> criteria,
        string criterion,
        SwingPromotionCriterionStatus status,
        string detail) =>
        criteria.Add(new SwingPromotionCriterionResult(criterion, status, detail));

    private static void ValidateBootstrapPolicy(SwingPromotionBootstrapPolicy policy)
    {
        if (policy.BlockLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.Replicates < 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "At least 100 bootstrap replicates are required.");
        }
    }
}
