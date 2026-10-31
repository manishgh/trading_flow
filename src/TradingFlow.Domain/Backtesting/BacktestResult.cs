using TradingFlow.Domain.Orders;

using TradingFlow.Domain.Persistence;

namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestResult(
    string RunName,
    string ResultPath,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    int TradingDayCount,
    decimal MaxDrawdownPct,
    WinnerStrategySummary? Winner,
    int ProcessedBarCount,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<StrategyBacktestResult> StrategyResults,
    IReadOnlyList<TickerBacktestResult> TickerResults,
    BacktestValidationReport Validation,
    IReadOnlyList<BacktestTrade> CompletedTrades,
    IReadOnlyList<StrategyDiagnosticReport> Diagnostics,
    IReadOnlyList<MissedMoveAudit> MissedMoves,
    IReadOnlyList<FinalizedOrder> AcceptedOrders,
    UnifiedPortfolioBacktestResult? UnifiedPortfolio = null,
    UniversePromotionEligibility? UniversePromotion = null,
    string? CandidateDecisionAuditPath = null,
    IReadOnlyList<BacktestCandidateDecisionAudit>? CandidateDecisionAudit = null,
    string? ExecutionAuditPath = null,
    IReadOnlyList<BacktestExecutionAuditEvent>? ExecutionAudit = null,
    string? ArtifactManifestPath = null,
    IReadOnlyList<BacktestArtifactReference>? ArtifactReferences = null,
    string? CandidateHypothesisAuditPath = null,
    string? PortfolioExecutionAuditPath = null,
    string SummaryScope = "winner_strategy",
    string ExecutionEvidenceScope = "unified_portfolio",
    bool EconomicResultsComplete = true,
    PreparedMarketDataSummary? PreparedMarketData = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BacktestZoneDecisionDetail>? ZoneDecisions { get; init; }
    public BacktestPortfolioState? PortfolioState { get; init; }
    public IReadOnlyList<BacktestHistoryEligibility> HistoryEligibility { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DecisionContextEvidenceProvenance? DecisionContextEvidence { get; init; }
}

/// <summary>Identifies the verified context graph actually loaded by the runner without exposing server storage paths.</summary>
public sealed record DecisionContextEvidenceProvenance(string ManifestSha256, string DependencyGraphSha256,
    string Provider, string Version, IReadOnlyList<string> SourceSha256s,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticPolicyId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DiagnosticPolicySha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? StrictCatalystProviderUsed = null);

public sealed record BacktestArtifactReference(
    int SchemaVersion,
    string Kind,
    string Path,
    long RecordCount,
    long ByteLength,
    string Sha256,
    bool Complete);

public sealed record BacktestArtifactManifest(
    int SchemaVersion,
    string ArtifactSetId,
    string RunName,
    DateTimeOffset PublishedAtUtc,
    bool PublicationComplete,
    BacktestArtifactCoverage Coverage,
    IReadOnlyList<BacktestArtifactReference> Artifacts);

public sealed record BacktestArtifactCoverage(
    int ExpectedWorkItemCount,
    int SucceededWorkItemCount,
    int FailedWorkItemCount,
    IReadOnlyList<string> FailedWorkItems,
    int ExecutionFailureCount = 0,
    IReadOnlyList<BacktestExecutionFailure>? ExecutionFailures = null)
{
    public string Status => FailedWorkItemCount == 0 && ExecutionFailureCount == 0
        ? "complete"
        : "partial";
}

public sealed record BacktestExecutionFailure(
    string CandidateId,
    string Ticker,
    string StrategyName,
    string StrategyId,
    string Direction,
    DateTimeOffset EntryTimestamp,
    string Reason,
    int RequestedQuantity,
    int EntryFilledQuantity,
    int ExitFilledQuantity,
    int OpenSignedQuantity,
    decimal EntryFilledNotional,
    decimal ExitFilledNotional,
    decimal Fees,
    decimal? LastMarkedPrice,
    DateTimeOffset? LastMarketTimestampUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool EvidenceUnavailable = false);

public sealed record BacktestExecutionAuditEvent(
    string Ticker,
    string StrategyName,
    DateTimeOffset Timestamp,
    string State,
    string Message,
    string EvidenceScope,
    string? ReferenceId = null,
    string? EvidenceJson = null);

public sealed record BacktestCandidateDecisionAudit(
    CandidateRecord Candidate,
    IReadOnlyList<CandidateTransitionRecord> Transitions);

public sealed record WinnerStrategySummary(
    string StrategyId,
    string StrategyName,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    decimal MaxDrawdownPct,
    int AcceptedTradeCount,
    int RejectedTradeCount);

public sealed record UnifiedPortfolioBacktestResult(
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal MaxDrawdownPct,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<BacktestTrade> CompletedTrades,
    IReadOnlyDictionary<string, int>? AdmissionRejectionCounts = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? AdmissionRejectionExamples = null,
    IReadOnlyList<BacktestExecutionFailure>? ExecutionFailures = null,
    bool EconomicResultsComplete = true)
{
    public BacktestPortfolioState? PortfolioState { get; init; }
}


public sealed record MissedMoveAudit(
    string Ticker,
    string Timeframe,
    DateTimeOffset StartTimestamp,
    DateTimeOffset PeakTimestamp,
    decimal StartClose,
    decimal PeakHigh,
    decimal MovePct,
    int BarsToPeak,
    IReadOnlyList<string> StrategiesWithEntries,
    IReadOnlyList<string> StrategiesWithoutEntries);
public sealed record StrategyBacktestResult(
    string StrategyId,
    string StrategyName,
    string Source,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    int TradingDayCount,
    decimal MaxDrawdownPct,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<BacktestTrade> CompletedTrades,
    IReadOnlyList<BacktestExecutionFailure>? ExecutionFailures = null,
    bool EconomicResultsComplete = true)
{
    public BacktestPortfolioState? PortfolioState { get; init; }
}

public sealed record TickerBacktestResult(
    string Ticker,
    bool Succeeded,
    int ProcessedBarCount,
    int CandidateOrderCount,
    string? ErrorMessage)
{
    public static TickerBacktestResult Completed(string ticker, int processedBarCount, int candidateOrderCount)
    {
        return new TickerBacktestResult(ticker, true, processedBarCount, candidateOrderCount, null);
    }

    public static TickerBacktestResult Failed(string ticker, Exception exception)
    {
        return new TickerBacktestResult(ticker, false, 0, 0, exception.Message);
    }
}

public sealed record StrategyDiagnosticReport(
    string StrategyId,
    string StrategyName,
    int EvaluatedBarCount,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    decimal WinRatePct,
    decimal AverageWin,
    decimal AverageLoss,
    decimal RealizedRewardRiskRatio,
    DailyPnlSummary DailyPnl,
    IReadOnlyList<DirectionPnlSummary> DirectionPnl,
    IReadOnlyDictionary<string, int> ExitReasonCounts,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RejectionExamples,
    IReadOnlyList<string> Suggestions,
    int ExecutionFailureCount = 0,
    bool EconomicResultsComplete = true);

public sealed record DailyPnlSummary(
    int TradingDayCount,
    int ActiveTradeDayCount,
    int WinningDayCount,
    int LosingDayCount,
    decimal NetProfit,
    decimal AverageNetProfitPerTradingDay,
    decimal AverageNetProfitPerActiveTradeDay,
    decimal BestDayNetProfit,
    DateOnly? BestDay,
    decimal WorstDayNetProfit,
    DateOnly? WorstDay);

public sealed record DirectionPnlSummary(
    string Direction,
    int TradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    decimal NetProfit,
    decimal AverageNetProfit,
    decimal BestTradeNetProfit,
    decimal WorstTradeNetProfit);




/// <summary>Frozen zone and filter evidence plus observed execution; absent prices remain absent.</summary>
public sealed record BacktestZoneDecisionDetail(string CandidateId, string StrategyId, string Ticker, string Direction,
    string Pattern, long ZoneId, int ZoneVersion, string ZoneRole, decimal ZoneBottom, decimal ZoneTop, int PivotCount,
    DateTimeOffset PivotBarStartUtc, DateTimeOffset PivotCompletedAtUtc, DateTimeOffset PivotConfirmedAtUtc,
    DateTimeOffset ZoneKnownAtUtc, DateTimeOffset? FlippedAtUtc, DateTimeOffset ConfirmationBarStartUtc,
    DateTimeOffset ConfirmationCompletedAtUtc, decimal ConfirmationClose, decimal? Vwap, decimal? SlotRelativeVolume,
    decimal? SlotMedianVolume, int SlotSampleCount, decimal FrozenStopPrice, DateTimeOffset SessionDeadlineUtc,
    string Status, string ResolutionReason, decimal? ActualEntryPrice, decimal? ActualExitPrice,
    string? ZonePolicyId = null, DateTimeOffset? EntryCutoffUtc = null, decimal? FixedTargetPrice = null,
    decimal? Atr = null, long? TargetZoneId = null, decimal? TargetZoneBottom = null, decimal? TargetZoneTop = null,
    BacktestZoneVolumeSpreadDetail? VolumeSpread = null,
    IReadOnlyList<BacktestZoneAlternativeDecisionDetail>? AlternativeDecisions = null,
    TradingFlow.Domain.Strategies.IntradayZoneTargetPlanningEvidence? TargetPlan = null,
    string? LongBounceVolumeMode = null, string? StopBasis = null, decimal? PreviousLow = null,
    decimal? ConfirmationLow = null, decimal? RawStopPrice = null, decimal? TickSize = null,
    TradingFlow.Domain.Strategies.IntradayZoneRetestEvidence? Retest = null, int? TargetZoneVersion = null,
    DateTimeOffset? TargetZoneKnownAtUtc = null);

/// <summary>Synthetic quote assumptions and the selected alternative's quantity-dependent ticket-fee estimate.</summary>
public sealed record BacktestZoneVolumeSpreadDetail(decimal BidPrice, decimal AskPrice, decimal FullSpreadBps,
    decimal MaximumSpreadBps, decimal MinimumRewardRisk, int OrderedAlternativeIndex, decimal? PlannedQuantity,
    decimal? EstimatedRoundTripTicketFees, decimal? EstimatedNetRiskDollars, decimal? EstimatedNetRewardDollars,
    decimal? SelectedRewardRisk);

/// <summary>Chronological admission's observed alternative checks; absence of a ratio means it was not calculated.</summary>
public sealed record BacktestZoneAlternativeDecisionDetail(int AlternativeIndex, string Direction, long ZoneId,
    decimal? PlannedQuantity, decimal? EstimatedNetRiskDollars, decimal? EstimatedNetRewardDollars,
    decimal? RewardRisk, bool Selected, string Reason,
    TradingFlow.Domain.Strategies.IntradayZoneTargetPlanningEvidence? TargetPlan = null);
