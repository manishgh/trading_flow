using System.Globalization;
using TradingFlow.Domain.Research.Intraday;

namespace TradingFlow.Research.Catalysts;

public enum IntradayResearchPartition
{
    Development = 1,
    Validation = 2,
    Holdout = 3
}

public enum IntradayLeaveOneOutDimension
{
    Ticker = 1,
    TradingDay = 2,
    EventSubtype = 3,
    OpeningMinute = 4
}

public enum IntradayClusterDefinition
{
    CanonicalStoryEpisodeAndEntrySession = 1
}

public enum IntradayResearchPromotionDecision
{
    RetainResearch = 1,
    PromoteToPaperShadow = 2
}

public sealed record IntradayPromotionEpisodeEvidence(
    string EpisodeId,
    string CanonicalStoryCluster,
    DateOnly EntrySessionDate,
    string EventCohort,
    string EventSubtype,
    int OpeningMinuteOfRegularSession);

public sealed record IntradayPromotionTradeEvidence(
    string TradeId,
    string EpisodeId,
    IntradayResearchPartition Partition,
    string Ticker,
    DateOnly TradingDay,
    string EventSubtype,
    int OpeningMinuteOfRegularSession,
    decimal GrossReturnBps,
    decimal NetReturnBps,
    decimal NetProfitLoss);

public sealed record IntradayCostStressEvidence(
    decimal CostMultiplier,
    decimal NetExpectancyBps);

public sealed record IntradayClusteredInferenceEvidence(
    IntradayClusterDefinition ClusterDefinition,
    int ClusterCount,
    decimal NetExpectancyBps,
    decimal StandardErrorBps,
    decimal Lower95ConfidenceBoundBps,
    decimal Upper95ConfidenceBoundBps);

public sealed record IntradaySessionBlockBootstrapEvidence(
    int SessionCount,
    int ReplicationCount,
    int BlockLengthSessions,
    decimal ConfidenceLevel,
    decimal NetExpectancyBps,
    decimal LowerConfidenceBoundBps,
    decimal UpperConfidenceBoundBps);

public sealed record IntradayPromotionConcentrationEvidence(
    string LargestTicker,
    decimal LargestTickerAbsolutePnlShare,
    DateOnly LargestTradingDay,
    decimal LargestTradingDayAbsolutePnlShare);

public sealed record IntradayLeaveOneOutEvidence(
    IntradayLeaveOneOutDimension Dimension,
    string OmittedKey,
    int RemainingTradeCount,
    decimal NetExpectancyBps);

public sealed record IntradayHumanPromotionApproval(
    bool Approved,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    string DecisionId,
    string ExperimentId,
    string EvidenceSnapshotHash);

public sealed record IntradayResearchPromotionEvidence(
    string ExperimentId,
    string EvidenceSnapshotHash,
    string ProposedEventCohort,
    DateTimeOffset EvaluationAsOfUtc,
    decimal FrozenRoundTripCostBps,
    IReadOnlyList<IntradayPromotionEpisodeEvidence>? Episodes,
    IReadOnlyList<IntradayPromotionTradeEvidence>? Trades,
    IReadOnlyList<IntradayCostStressEvidence>? CostStresses,
    IntradayClusteredInferenceEvidence? ClusteredInference,
    IntradaySessionBlockBootstrapEvidence? SessionBlockBootstrap,
    IntradayPromotionConcentrationEvidence? Concentration,
    IReadOnlyList<IntradayLeaveOneOutEvidence>? LeaveOneOut,
    IntradayHumanPromotionApproval? HumanApproval,
    IntradayEvidenceStudyReport? EvidenceStudyReport);

public sealed record IntradayPromotionBlocker(
    string Code,
    string Message);

public sealed record IntradayPromotionMetrics(
    int IndependentEpisodeCount,
    int ProposedCohortEpisodeCount,
    int ExecutableTradeCount,
    int HoldoutTradeCount,
    decimal? DevelopmentNetExpectancyBps,
    decimal? ValidationNetExpectancyBps,
    decimal? HoldoutNetExpectancyBps,
    decimal? OverallGrossEdgeBps,
    decimal? ProfitFactorAfterCosts,
    decimal? LargestTickerAbsolutePnlShare,
    decimal? LargestTradingDayAbsolutePnlShare,
    decimal? SessionBootstrapLower95BoundBps);

public sealed record IntradayResearchPromotionAssessment(
    IntradayResearchPromotionDecision Decision,
    IReadOnlyList<IntradayPromotionBlocker> Blockers,
    IntradayPromotionMetrics Metrics)
{
    public bool IsPromotable =>
        Decision == IntradayResearchPromotionDecision.PromoteToPaperShadow &&
        Blockers.Count == 0;
}

/// <summary>
/// Applies the frozen intraday promotion gate without selecting parameters or
/// reconstructing missing evidence. Any missing, malformed, or inconsistent input
/// retains the experiment in research.
/// </summary>
public static class IntradayResearchPromotionEvaluator
{
    private const int MinimumIndependentEpisodes = 300;
    private const int MinimumCohortEpisodes = 100;
    private const int MinimumExecutableTrades = 100;
    private const int MinimumHoldoutTrades = 30;
    private const decimal MinimumProfitFactor = 1.20m;
    private const decimal MaximumConcentrationShare = 0.10m;
    private const decimal RequiredBootstrapConfidence = 0.95m;
    private const decimal ComparisonTolerance = 0.000001m;

    public static IntradayResearchPromotionAssessment Evaluate(
        IntradayResearchPromotionEvidence? evidence)
    {
        var blockers = new List<IntradayPromotionBlocker>();
        if (evidence is null)
        {
            Add(
                blockers,
                "promotion_evidence_missing",
                "The intraday promotion evidence package is required.");
            return Assessment(blockers, EmptyMetrics());
        }

        ValidateIdentity(evidence, blockers);
        var episodes = ValidateEpisodes(evidence, blockers);
        var trades = ValidateTrades(evidence, episodes, blockers);
        ValidateEvidenceStudyReport(evidence, episodes, blockers);

        var independentEpisodeCount = episodes.Count;
        var proposedCohortEpisodeCount = episodes.Count(value =>
            value.EventCohort.Equals(
                evidence.ProposedEventCohort,
                StringComparison.Ordinal));
        if (independentEpisodeCount < MinimumIndependentEpisodes)
        {
            Add(
                blockers,
                "independent_catalyst_episodes_below_minimum",
                $"Independent catalyst episodes are {independentEpisodeCount}; at least {MinimumIndependentEpisodes} are required.");
        }

        if (proposedCohortEpisodeCount < MinimumCohortEpisodes)
        {
            Add(
                blockers,
                "proposed_event_cohort_episodes_below_minimum",
                $"Independent episodes in cohort '{evidence.ProposedEventCohort}' are {proposedCohortEpisodeCount}; at least {MinimumCohortEpisodes} are required.");
        }

        if (trades.Count < MinimumExecutableTrades)
        {
            Add(
                blockers,
                "executable_simulated_trades_below_minimum",
                $"Executable simulated trades are {trades.Count}; at least {MinimumExecutableTrades} are required.");
        }

        var holdoutTradeCount = trades.Count(value =>
            value.Partition == IntradayResearchPartition.Holdout);
        if (holdoutTradeCount < MinimumHoldoutTrades)
        {
            Add(
                blockers,
                "holdout_trades_below_minimum",
                $"Holdout trades are {holdoutTradeCount}; at least {MinimumHoldoutTrades} are required.");
        }

        var developmentExpectancy = PartitionExpectancy(
            trades,
            IntradayResearchPartition.Development,
            "development",
            blockers);
        var validationExpectancy = PartitionExpectancy(
            trades,
            IntradayResearchPartition.Validation,
            "validation",
            blockers);
        var holdoutExpectancy = PartitionExpectancy(
            trades,
            IntradayResearchPartition.Holdout,
            "holdout",
            blockers);

        decimal? overallGrossEdge = trades.Count == 0
            ? null
            : trades.Average(value => value.GrossReturnBps);
        ValidateGrossEdge(evidence, overallGrossEdge, blockers);

        var profitFactor = ValidateProfitFactor(trades, blockers);
        var concentration = ValidateConcentration(evidence, trades, blockers);
        decimal? overallNetExpectancy = trades.Count == 0
            ? null
            : trades.Average(value => value.NetReturnBps);
        ValidateClusteredInference(
            evidence.ClusteredInference,
            trades,
            overallNetExpectancy,
            blockers);
        ValidateSessionBootstrap(
            evidence.SessionBlockBootstrap,
            trades,
            overallNetExpectancy,
            blockers);
        ValidateCostStresses(
            evidence.CostStresses,
            overallNetExpectancy,
            blockers);
        ValidateLeaveOneOut(evidence.LeaveOneOut, trades, blockers);
        ValidateHumanApproval(evidence, blockers);

        var metrics = new IntradayPromotionMetrics(
            independentEpisodeCount,
            proposedCohortEpisodeCount,
            trades.Count,
            holdoutTradeCount,
            developmentExpectancy,
            validationExpectancy,
            holdoutExpectancy,
            overallGrossEdge,
            profitFactor,
            concentration.TickerShare,
            concentration.TradingDayShare,
            evidence.SessionBlockBootstrap?.LowerConfidenceBoundBps);
        return Assessment(blockers, metrics);
    }

    private static void ValidateIdentity(
        IntradayResearchPromotionEvidence evidence,
        List<IntradayPromotionBlocker> blockers)
    {
        RequiredText(
            evidence.ExperimentId,
            "experiment_id_missing",
            "A frozen experiment ID is required.",
            blockers);
        RequiredText(
            evidence.EvidenceSnapshotHash,
            "evidence_snapshot_hash_missing",
            "An immutable evidence snapshot hash is required.",
            blockers);
        RequiredText(
            evidence.ProposedEventCohort,
            "proposed_event_cohort_missing",
            "The proposed event cohort must be explicit.",
            blockers);
        if (evidence.EvaluationAsOfUtc == default)
        {
            Add(
                blockers,
                "evaluation_timestamp_missing",
                "The promotion evaluation timestamp is required.");
        }
    }

    private static IReadOnlyList<IntradayPromotionEpisodeEvidence> ValidateEpisodes(
        IntradayResearchPromotionEvidence evidence,
        List<IntradayPromotionBlocker> blockers)
    {
        if (evidence.Episodes is null)
        {
            Add(
                blockers,
                "episode_evidence_missing",
                "Independent catalyst episode evidence is required.");
            return [];
        }

        var episodes = evidence.Episodes.Where(value => value is not null).ToArray();
        if (episodes.Length != evidence.Episodes.Count)
        {
            Add(
                blockers,
                "episode_evidence_contains_null",
                "Episode evidence cannot contain null rows.");
        }

        var malformed = episodes.Any(value =>
            string.IsNullOrWhiteSpace(value.EpisodeId) ||
            string.IsNullOrWhiteSpace(value.CanonicalStoryCluster) ||
            value.EntrySessionDate == default ||
            string.IsNullOrWhiteSpace(value.EventCohort) ||
            string.IsNullOrWhiteSpace(value.EventSubtype) ||
            value.OpeningMinuteOfRegularSession < 0);
        if (malformed)
        {
            Add(
                blockers,
                "episode_evidence_malformed",
                "Every episode requires identity, canonical story, entry session, cohort, subtype, and a non-negative opening minute.");
        }

        if (episodes
            .GroupBy(value => value.EpisodeId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            Add(
                blockers,
                "episode_id_not_unique",
                "Each independent catalyst episode ID must occur exactly once.");
        }

        return episodes
            .Where(value =>
                !string.IsNullOrWhiteSpace(value.EpisodeId) &&
                !string.IsNullOrWhiteSpace(value.CanonicalStoryCluster) &&
                value.EntrySessionDate != default &&
                !string.IsNullOrWhiteSpace(value.EventCohort) &&
                !string.IsNullOrWhiteSpace(value.EventSubtype) &&
                value.OpeningMinuteOfRegularSession >= 0)
            .GroupBy(value => value.EpisodeId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
    }

    private static IReadOnlyList<IntradayPromotionTradeEvidence> ValidateTrades(
        IntradayResearchPromotionEvidence evidence,
        IReadOnlyList<IntradayPromotionEpisodeEvidence> episodes,
        List<IntradayPromotionBlocker> blockers)
    {
        if (evidence.Trades is null)
        {
            Add(
                blockers,
                "trade_evidence_missing",
                "Executable simulated trade evidence is required.");
            return [];
        }

        var trades = evidence.Trades.Where(value => value is not null).ToArray();
        if (trades.Length != evidence.Trades.Count)
        {
            Add(
                blockers,
                "trade_evidence_contains_null",
                "Trade evidence cannot contain null rows.");
        }

        if (trades
            .GroupBy(value => value.TradeId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            Add(
                blockers,
                "trade_id_not_unique",
                "Each executable simulated trade ID must occur exactly once.");
        }

        var episodeById = episodes.ToDictionary(
            value => value.EpisodeId,
            StringComparer.Ordinal);
        var malformed = false;
        var orphaned = false;
        var inconsistent = false;
        var signMismatch = false;
        foreach (var trade in trades)
        {
            malformed |=
                string.IsNullOrWhiteSpace(trade.TradeId) ||
                string.IsNullOrWhiteSpace(trade.EpisodeId) ||
                !Enum.IsDefined(trade.Partition) ||
                string.IsNullOrWhiteSpace(trade.Ticker) ||
                trade.TradingDay == default ||
                string.IsNullOrWhiteSpace(trade.EventSubtype) ||
                trade.OpeningMinuteOfRegularSession < 0;
            if (!episodeById.TryGetValue(trade.EpisodeId, out var episode))
            {
                orphaned = true;
                continue;
            }

            inconsistent |=
                trade.TradingDay != episode.EntrySessionDate ||
                !trade.EventSubtype.Equals(
                    episode.EventSubtype,
                    StringComparison.Ordinal) ||
                trade.OpeningMinuteOfRegularSession !=
                    episode.OpeningMinuteOfRegularSession;
            signMismatch |=
                trade.NetReturnBps != 0m &&
                trade.NetProfitLoss != 0m &&
                Math.Sign(trade.NetReturnBps) != Math.Sign(trade.NetProfitLoss);
        }

        if (malformed)
        {
            Add(
                blockers,
                "trade_evidence_malformed",
                "Every trade requires identity, partition, ticker, trading day, event subtype, and a non-negative opening minute.");
        }

        if (orphaned)
        {
            Add(
                blockers,
                "trade_episode_reference_missing",
                "Every trade must reference one admitted independent catalyst episode.");
        }

        if (inconsistent)
        {
            Add(
                blockers,
                "trade_episode_evidence_mismatch",
                "Trade session, event subtype, and opening minute must match the referenced episode.");
        }

        if (signMismatch)
        {
            Add(
                blockers,
                "trade_return_pnl_sign_mismatch",
                "Net return and net profit/loss must have consistent signs.");
        }

        return trades
            .Where(value =>
                !string.IsNullOrWhiteSpace(value.TradeId) &&
                !string.IsNullOrWhiteSpace(value.EpisodeId) &&
                Enum.IsDefined(value.Partition) &&
                !string.IsNullOrWhiteSpace(value.Ticker) &&
                value.TradingDay != default &&
                !string.IsNullOrWhiteSpace(value.EventSubtype) &&
                value.OpeningMinuteOfRegularSession >= 0 &&
                episodeById.ContainsKey(value.EpisodeId))
            .GroupBy(value => value.TradeId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
    }

    private static void ValidateEvidenceStudyReport(
        IntradayResearchPromotionEvidence evidence,
        IReadOnlyList<IntradayPromotionEpisodeEvidence> episodes,
        List<IntradayPromotionBlocker> blockers)
    {
        var report = evidence.EvidenceStudyReport;
        if (report is null)
        {
            Add(
                blockers,
                "intraday_evidence_study_report_missing",
                "The frozen IntradayEvidenceStudyReport is required.");
            return;
        }

        if (report.Eligibility != IntradayEvidenceEligibility.PromotionEligible)
        {
            Add(
                blockers,
                "intraday_evidence_study_not_promotion_eligible",
                $"The evidence study eligibility is {report.Eligibility}, not PromotionEligible.");
        }

        if (report.Blockers is null || report.Blockers.Count != 0)
        {
            Add(
                blockers,
                "intraday_evidence_study_has_blockers",
                "The evidence study report must contain no admission or point-in-time blockers.");
        }

        if (report.GeneratedAtUtc == default ||
            evidence.EvaluationAsOfUtc != default &&
            report.GeneratedAtUtc > evidence.EvaluationAsOfUtc)
        {
            Add(
                blockers,
                "intraday_evidence_study_timestamp_invalid",
                "The evidence study must be generated before the promotion evaluation.");
        }

        if (report.Observations is null || report.Observations.Count == 0)
        {
            Add(
                blockers,
                "intraday_evidence_study_observations_missing",
                "The evidence study must contain point-in-time observations.");
        }
        else
        {
            var reportClusters = report.Observations
                .Select(value => value.GlobalStoryCluster)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            if (episodes.Any(value =>
                    !reportClusters.Contains(value.CanonicalStoryCluster)))
            {
                Add(
                    blockers,
                    "episode_not_reconciled_to_evidence_study",
                    "Every promotion episode must reconcile to a canonical story cluster in the evidence study.");
            }
        }

        if (report.HolmReadyPValues is null ||
            report.HolmReadyPValues.Count == 0)
        {
            Add(
                blockers,
                "intraday_holm_evidence_missing",
                "The frozen event-study family must include Holm-adjusted evidence.");
        }
        else if (report.HolmReadyPValues.Any(value =>
                     value.SampleCount <= 0 ||
                     value.RawPValue is < 0m or > 1m ||
                     value.AdjustedPValue is < 0m or > 1m ||
                     value.AdjustedPValue < value.RawPValue ||
                     value.HolmRank <= 0 ||
                     value.FamilySize <= 0 ||
                     value.HolmRank > value.FamilySize))
        {
            Add(
                blockers,
                "intraday_holm_evidence_malformed",
                "Holm evidence contains an invalid sample, probability, rank, or family size.");
        }
    }

    private static decimal? PartitionExpectancy(
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        IntradayResearchPartition partition,
        string label,
        List<IntradayPromotionBlocker> blockers)
    {
        var partitionTrades = trades
            .Where(value => value.Partition == partition)
            .ToArray();
        if (partitionTrades.Length == 0)
        {
            Add(
                blockers,
                $"{label}_trade_evidence_missing",
                $"Executable {label} trade evidence is required.");
            return null;
        }

        var expectancy = partitionTrades.Average(value => value.NetReturnBps);
        if (expectancy <= 0m)
        {
            Add(
                blockers,
                $"{label}_net_expectancy_not_positive",
                $"{label} net expectancy is {expectancy:F6} bps; it must be positive.");
        }

        return expectancy;
    }

    private static void ValidateGrossEdge(
        IntradayResearchPromotionEvidence evidence,
        decimal? grossEdge,
        List<IntradayPromotionBlocker> blockers)
    {
        if (evidence.FrozenRoundTripCostBps <= 0m)
        {
            Add(
                blockers,
                "frozen_round_trip_cost_missing_or_invalid",
                "Frozen round-trip cost must be explicit and greater than zero.");
            return;
        }

        if (grossEdge is null)
        {
            Add(
                blockers,
                "gross_edge_evidence_missing",
                "Expected gross edge cannot be calculated without executable trades.");
            return;
        }

        var required = 2m * evidence.FrozenRoundTripCostBps;
        if (grossEdge < required)
        {
            Add(
                blockers,
                "gross_edge_below_twice_round_trip_cost",
                $"Expected gross edge is {grossEdge:F6} bps; at least {required:F6} bps is required.");
        }
    }

    private static decimal? ValidateProfitFactor(
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        List<IntradayPromotionBlocker> blockers)
    {
        if (trades.Count == 0)
        {
            Add(
                blockers,
                "profit_factor_evidence_missing",
                "Profit factor cannot be calculated without executable trades.");
            return null;
        }

        var grossProfit = trades
            .Where(value => value.NetProfitLoss > 0m)
            .Sum(value => value.NetProfitLoss);
        var grossLoss = Math.Abs(trades
            .Where(value => value.NetProfitLoss < 0m)
            .Sum(value => value.NetProfitLoss));
        if (grossProfit <= 0m)
        {
            Add(
                blockers,
                "profit_factor_not_positive",
                "After-cost trade evidence contains no gross profit.");
            return 0m;
        }

        if (grossLoss == 0m)
        {
            return null;
        }

        var profitFactor = grossProfit / grossLoss;
        if (profitFactor < MinimumProfitFactor)
        {
            Add(
                blockers,
                "profit_factor_below_minimum",
                $"After-cost profit factor is {profitFactor:F6}; at least {MinimumProfitFactor:F2} is required.");
        }

        return profitFactor;
    }

    private static (decimal? TickerShare, decimal? TradingDayShare)
        ValidateConcentration(
            IntradayResearchPromotionEvidence evidence,
            IReadOnlyList<IntradayPromotionTradeEvidence> trades,
            List<IntradayPromotionBlocker> blockers)
    {
        if (evidence.Concentration is null)
        {
            Add(
                blockers,
                "concentration_evidence_missing",
                "Ticker and trading-day absolute P&L concentration evidence is required.");
            return (null, null);
        }

        var totalAbsolutePnl = trades.Sum(value => Math.Abs(value.NetProfitLoss));
        if (totalAbsolutePnl <= 0m)
        {
            Add(
                blockers,
                "absolute_pnl_evidence_zero",
                "Absolute profit and loss must be greater than zero for concentration analysis.");
            return (null, null);
        }

        var tickerGroups = trades
            .GroupBy(value => value.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Key = group.Key,
                Share = group.Sum(value => Math.Abs(value.NetProfitLoss)) /
                    totalAbsolutePnl
            })
            .OrderByDescending(value => value.Share)
            .ToArray();
        var dayGroups = trades
            .GroupBy(value => value.TradingDay)
            .Select(group => new
            {
                Key = group.Key,
                Share = group.Sum(value => Math.Abs(value.NetProfitLoss)) /
                    totalAbsolutePnl
            })
            .OrderByDescending(value => value.Share)
            .ToArray();
        var tickerShare = tickerGroups.First().Share;
        var dayShare = dayGroups.First().Share;

        var suppliedTicker = tickerGroups.SingleOrDefault(value =>
            value.Key.Equals(
                evidence.Concentration.LargestTicker,
                StringComparison.OrdinalIgnoreCase));
        if (suppliedTicker is null ||
            !Close(
                suppliedTicker.Share,
                evidence.Concentration.LargestTickerAbsolutePnlShare) ||
            !Close(tickerShare, evidence.Concentration.LargestTickerAbsolutePnlShare))
        {
            Add(
                blockers,
                "ticker_concentration_evidence_mismatch",
                "Supplied ticker concentration does not reconcile to trade-level absolute P&L.");
        }

        var suppliedDay = dayGroups.SingleOrDefault(value =>
            value.Key == evidence.Concentration.LargestTradingDay);
        if (suppliedDay is null ||
            !Close(
                suppliedDay.Share,
                evidence.Concentration.LargestTradingDayAbsolutePnlShare) ||
            !Close(dayShare, evidence.Concentration.LargestTradingDayAbsolutePnlShare))
        {
            Add(
                blockers,
                "trading_day_concentration_evidence_mismatch",
                "Supplied trading-day concentration does not reconcile to trade-level absolute P&L.");
        }

        if (tickerShare > MaximumConcentrationShare)
        {
            Add(
                blockers,
                "ticker_absolute_pnl_concentration_above_maximum",
                $"Largest ticker absolute P&L share is {tickerShare:P4}; it must not exceed {MaximumConcentrationShare:P0}.");
        }

        if (dayShare > MaximumConcentrationShare)
        {
            Add(
                blockers,
                "trading_day_absolute_pnl_concentration_above_maximum",
                $"Largest trading-day absolute P&L share is {dayShare:P4}; it must not exceed {MaximumConcentrationShare:P0}.");
        }

        return (tickerShare, dayShare);
    }

    private static void ValidateClusteredInference(
        IntradayClusteredInferenceEvidence? clustered,
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        decimal? netExpectancy,
        List<IntradayPromotionBlocker> blockers)
    {
        if (clustered is null)
        {
            Add(
                blockers,
                "clustered_inference_evidence_missing",
                "Canonical-story/session clustered inference evidence is required.");
            return;
        }

        var expectedClusters = trades
            .Select(value => (value.EpisodeId, value.TradingDay))
            .Distinct()
            .Count();
        if (clustered.ClusterDefinition !=
                IntradayClusterDefinition.CanonicalStoryEpisodeAndEntrySession ||
            clustered.ClusterCount != expectedClusters ||
            clustered.ClusterCount <= 0)
        {
            Add(
                blockers,
                "clustered_inference_definition_or_count_mismatch",
                "Clustered inference must use canonical story episode plus entry session and reconcile to trade evidence.");
        }

        if (netExpectancy is null ||
            !Close(clustered.NetExpectancyBps, netExpectancy.Value))
        {
            Add(
                blockers,
                "clustered_inference_expectancy_mismatch",
                "Clustered inference point estimate does not reconcile to trade-level net expectancy.");
        }

        if (clustered.StandardErrorBps < 0m ||
            clustered.Lower95ConfidenceBoundBps >
                clustered.Upper95ConfidenceBoundBps ||
            clustered.NetExpectancyBps <
                clustered.Lower95ConfidenceBoundBps ||
            clustered.NetExpectancyBps >
                clustered.Upper95ConfidenceBoundBps)
        {
            Add(
                blockers,
                "clustered_inference_evidence_malformed",
                "Clustered inference standard error or confidence interval is invalid.");
        }
    }

    private static void ValidateSessionBootstrap(
        IntradaySessionBlockBootstrapEvidence? bootstrap,
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        decimal? netExpectancy,
        List<IntradayPromotionBlocker> blockers)
    {
        if (bootstrap is null)
        {
            Add(
                blockers,
                "session_block_bootstrap_evidence_missing",
                "Session-date block-bootstrap evidence is required.");
            return;
        }

        var sessionCount = trades.Select(value => value.TradingDay).Distinct().Count();
        if (bootstrap.SessionCount != sessionCount ||
            bootstrap.SessionCount <= 0 ||
            bootstrap.ReplicationCount <= 0 ||
            bootstrap.BlockLengthSessions <= 0)
        {
            Add(
                blockers,
                "session_block_bootstrap_configuration_mismatch",
                "Bootstrap session count must reconcile to trades and replication/block lengths must be positive.");
        }

        if (bootstrap.ConfidenceLevel != RequiredBootstrapConfidence)
        {
            Add(
                blockers,
                "session_block_bootstrap_confidence_not_95_percent",
                "The promotion gate requires a 95% session-block-bootstrap interval.");
        }

        if (netExpectancy is null ||
            !Close(bootstrap.NetExpectancyBps, netExpectancy.Value))
        {
            Add(
                blockers,
                "session_block_bootstrap_expectancy_mismatch",
                "Bootstrap point estimate does not reconcile to trade-level net expectancy.");
        }

        if (bootstrap.LowerConfidenceBoundBps >
                bootstrap.UpperConfidenceBoundBps ||
            bootstrap.NetExpectancyBps <
                bootstrap.LowerConfidenceBoundBps ||
            bootstrap.NetExpectancyBps >
                bootstrap.UpperConfidenceBoundBps)
        {
            Add(
                blockers,
                "session_block_bootstrap_interval_malformed",
                "The session-block-bootstrap interval is invalid.");
        }

        if (bootstrap.LowerConfidenceBoundBps <= 0m)
        {
            Add(
                blockers,
                "session_block_bootstrap_lower_bound_not_positive",
                $"The 95% session-block-bootstrap lower bound is {bootstrap.LowerConfidenceBoundBps:F6} bps; it must be positive.");
        }
    }

    private static void ValidateCostStresses(
        IReadOnlyList<IntradayCostStressEvidence>? costStresses,
        decimal? netExpectancy,
        List<IntradayPromotionBlocker> blockers)
    {
        if (costStresses is null)
        {
            Add(
                blockers,
                "cost_stress_evidence_missing",
                "Frozen 1x, 2x, and 3x cost-stress evidence is required.");
            return;
        }

        var required = new[] { 1m, 2m, 3m };
        var grouped = costStresses
            .GroupBy(value => value.CostMultiplier)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (grouped.Keys.Except(required).Any() ||
            required.Any(multiplier =>
                !grouped.TryGetValue(multiplier, out var rows) ||
                rows.Length != 1))
        {
            Add(
                blockers,
                "cost_stress_multiplier_set_not_frozen",
                "Cost stress evidence must contain exactly one 1x, 2x, and 3x result.");
            return;
        }

        if (netExpectancy is null ||
            !Close(grouped[1m].Single().NetExpectancyBps, netExpectancy.Value))
        {
            Add(
                blockers,
                "one_x_cost_stress_expectancy_mismatch",
                "The 1x cost-stress estimate must reconcile to observed net expectancy.");
        }

        foreach (var multiplier in required)
        {
            var result = grouped[multiplier].Single();
            if (result.NetExpectancyBps <= 0m)
            {
                Add(
                    blockers,
                    $"cost_stress_{multiplier.ToString("0", CultureInfo.InvariantCulture)}x_reverses_conclusion",
                    $"{multiplier:0}x cost-stress net expectancy is {result.NetExpectancyBps:F6} bps; it must remain positive.");
            }
        }
    }

    private static void ValidateLeaveOneOut(
        IReadOnlyList<IntradayLeaveOneOutEvidence>? leaveOneOut,
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        List<IntradayPromotionBlocker> blockers)
    {
        if (leaveOneOut is null)
        {
            Add(
                blockers,
                "leave_one_out_evidence_missing",
                "Leave-one-ticker/day/event-subtype/opening-minute evidence is required.");
            return;
        }

        ValidateLeaveOneDimension(
            IntradayLeaveOneOutDimension.Ticker,
            trades.Select(value => value.Ticker).Distinct(StringComparer.OrdinalIgnoreCase),
            leaveOneOut,
            trades,
            (trade, key) => trade.Ticker.Equals(
                key,
                StringComparison.OrdinalIgnoreCase),
            blockers);
        ValidateLeaveOneDimension(
            IntradayLeaveOneOutDimension.TradingDay,
            trades.Select(value =>
                    value.TradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal),
            leaveOneOut,
            trades,
            (trade, key) => trade.TradingDay.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture)
                .Equals(key, StringComparison.Ordinal),
            blockers);
        ValidateLeaveOneDimension(
            IntradayLeaveOneOutDimension.EventSubtype,
            trades.Select(value => value.EventSubtype).Distinct(StringComparer.Ordinal),
            leaveOneOut,
            trades,
            (trade, key) => trade.EventSubtype.Equals(key, StringComparison.Ordinal),
            blockers);
        ValidateLeaveOneDimension(
            IntradayLeaveOneOutDimension.OpeningMinute,
            trades.Select(value =>
                    value.OpeningMinuteOfRegularSession.ToString(
                        CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal),
            leaveOneOut,
            trades,
            (trade, key) => trade.OpeningMinuteOfRegularSession.ToString(
                    CultureInfo.InvariantCulture)
                .Equals(key, StringComparison.Ordinal),
            blockers);

        if (leaveOneOut.Any(value => !Enum.IsDefined(value.Dimension)))
        {
            Add(
                blockers,
                "leave_one_out_dimension_invalid",
                "Leave-one-out evidence contains an unsupported dimension.");
        }
    }

    private static void ValidateLeaveOneDimension(
        IntradayLeaveOneOutDimension dimension,
        IEnumerable<string> expectedKeys,
        IReadOnlyList<IntradayLeaveOneOutEvidence> evidence,
        IReadOnlyList<IntradayPromotionTradeEvidence> trades,
        Func<IntradayPromotionTradeEvidence, string, bool> isOmitted,
        List<IntradayPromotionBlocker> blockers)
    {
        var expected = expectedKeys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var supplied = evidence
            .Where(value => value.Dimension == dimension)
            .ToArray();
        var suppliedGroups = supplied
            .GroupBy(value => value.OmittedKey ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (suppliedGroups.Keys.Except(expected, StringComparer.Ordinal).Any() ||
            expected.Any(key =>
                !suppliedGroups.TryGetValue(key, out var rows) ||
                rows.Length != 1))
        {
            Add(
                blockers,
                $"leave_one_{DimensionCode(dimension)}_coverage_incomplete",
                $"Leave-one-{DimensionCode(dimension)} evidence must contain exactly one result for every observed key.");
        }

        foreach (var key in expected)
        {
            if (!suppliedGroups.TryGetValue(key, out var rows) || rows.Length != 1)
            {
                continue;
            }

            var remaining = trades.Where(value => !isOmitted(value, key)).ToArray();
            var row = rows.Single();
            if (remaining.Length == 0)
            {
                Add(
                    blockers,
                    $"single_{DimensionCode(dimension)}_dependence",
                    $"Removing {dimension} '{key}' leaves no executable trades.");
                continue;
            }

            var expectedMean = remaining.Average(value => value.NetReturnBps);
            if (row.RemainingTradeCount != remaining.Length ||
                !Close(row.NetExpectancyBps, expectedMean))
            {
                Add(
                    blockers,
                    $"leave_one_{DimensionCode(dimension)}_evidence_mismatch",
                    $"Leave-one-{DimensionCode(dimension)} result for '{key}' does not reconcile to trade evidence.");
            }

            if (expectedMean <= 0m)
            {
                Add(
                    blockers,
                    $"leave_one_{DimensionCode(dimension)}_reverses_conclusion",
                    $"Removing {dimension} '{key}' produces {expectedMean:F6} bps net expectancy.");
            }
        }
    }

    private static void ValidateHumanApproval(
        IntradayResearchPromotionEvidence evidence,
        List<IntradayPromotionBlocker> blockers)
    {
        var approval = evidence.HumanApproval;
        if (approval is null)
        {
            Add(
                blockers,
                "human_promotion_approval_missing",
                "Explicit human promotion approval is required.");
            return;
        }

        if (!approval.Approved)
        {
            Add(
                blockers,
                "human_promotion_not_approved",
                "The human promotion decision is not approved.");
        }

        if (string.IsNullOrWhiteSpace(approval.ApprovedBy) ||
            string.IsNullOrWhiteSpace(approval.DecisionId) ||
            approval.ApprovedAtUtc == default)
        {
            Add(
                blockers,
                "human_promotion_approval_provenance_missing",
                "Human approval requires approver, decision ID, and timestamp.");
        }

        if (!approval.ExperimentId.Equals(
                evidence.ExperimentId,
                StringComparison.Ordinal) ||
            !approval.EvidenceSnapshotHash.Equals(
                evidence.EvidenceSnapshotHash,
                StringComparison.Ordinal))
        {
            Add(
                blockers,
                "human_promotion_approval_binding_mismatch",
                "Human approval must bind to the evaluated experiment and evidence snapshot.");
        }

        if (evidence.EvaluationAsOfUtc != default &&
            approval.ApprovedAtUtc > evidence.EvaluationAsOfUtc)
        {
            Add(
                blockers,
                "human_promotion_approval_after_evaluation",
                "Human approval cannot occur after the evaluation timestamp.");
        }
    }

    private static IntradayResearchPromotionAssessment Assessment(
        IReadOnlyList<IntradayPromotionBlocker> blockers,
        IntradayPromotionMetrics metrics) =>
        new(
            blockers.Count == 0
                ? IntradayResearchPromotionDecision.PromoteToPaperShadow
                : IntradayResearchPromotionDecision.RetainResearch,
            blockers.ToArray(),
            metrics);

    private static IntradayPromotionMetrics EmptyMetrics() =>
        new(0, 0, 0, 0, null, null, null, null, null, null, null, null);

    private static void RequiredText(
        string? value,
        string code,
        string message,
        List<IntradayPromotionBlocker> blockers)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(blockers, code, message);
        }
    }

    private static void Add(
        List<IntradayPromotionBlocker> blockers,
        string code,
        string message)
    {
        if (blockers.All(value => !value.Code.Equals(code, StringComparison.Ordinal)))
        {
            blockers.Add(new IntradayPromotionBlocker(code, message));
        }
    }

    private static bool Close(decimal left, decimal right) =>
        Math.Abs(left - right) <= ComparisonTolerance;

    private static string DimensionCode(IntradayLeaveOneOutDimension dimension) =>
        dimension switch
        {
            IntradayLeaveOneOutDimension.Ticker => "ticker",
            IntradayLeaveOneOutDimension.TradingDay => "day",
            IntradayLeaveOneOutDimension.EventSubtype => "event_subtype",
            IntradayLeaveOneOutDimension.OpeningMinute => "opening_minute",
            _ => "unsupported"
        };
}
