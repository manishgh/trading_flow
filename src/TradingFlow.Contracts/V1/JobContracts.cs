namespace TradingFlow.Contracts.V1;

public sealed record StartBacktestRequest(
    string ContractVersion,
    string RunName,
    Guid UniverseSnapshotId,
    IReadOnlyList<StrategyReference> Strategies,
    DateTimeOffset TestWindowStartUtc,
    DateTimeOffset TestWindowEndUtc,
    decimal StartingCapital,
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
    int MaxConcurrentPositions,
    string CachePolicy,
    string RetentionPolicy,
    string IdempotencyKey,
    string? ConfigurationKey = null,
    string? ConfigurationSha256 = null,
    DecisionContextEvidenceReference? DecisionContextEvidence = null,
    string? ExecutionReplayPolicyId = null);

public sealed record StartPaperRunRequest(
    string ContractVersion,
    string RunName,
    Guid UniverseSnapshotId,
    StrategyReference Strategy,
    bool NewsEnabled,
    bool AllowExtendedHoursTrading,
    string EntryOrderType,
    string TimeInForce,
    string ExecutionPolicy,
    string IdempotencyKey,
    string Mode,
    string? ConfigurationKey = null);

public sealed record CancelJobRequest(string ContractVersion, string Reason);

public sealed record RunJobResponse(
    string ContractVersion,
    Guid JobId,
    string JobKind,
    string RunName,
    string Status,
    RunProvenance? Provenance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    string CurrentStage,
    int CompletedWorkItemCount,
    int TotalWorkItemCount,
    long LatestEventSequence,
    string? ResultReference,
    JobFailureResponse? Failure,
    BacktestCohortExecution? Cohort = null,
    PreparedMarketDataSummaryResponse? PreparedMarketData = null,
    DecisionContextEvidenceSummary? EvidenceSummary = null);

public sealed record JobFailureResponse(
    string Code,
    string Message,
    bool IsRetryable,
    DateTimeOffset FailedAtUtc);

public sealed record JobProgressResponse(
    Guid JobId,
    string Status,
    string CurrentStage,
    int CompletedWorkItemCount,
    int TotalWorkItemCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record RunJobDetailsResponse(RunJobResponse Job, IReadOnlyList<string> Events, RunResultSummary? Result,
    IReadOnlyList<RunStrategyProgress> Strategies, IReadOnlyList<MonthlyRunResult>? MonthlyResults = null,
    string? MonthlyUnavailableReason = null, PreparedMarketDataSummaryResponse? PreparedMarketData = null,
    RunPortfolioStateResponse? PortfolioState = null, IReadOnlyList<RunHistoryEligibility>? HistoryEligibility = null,
    DecisionContextEvidenceSummary? EvidenceSummary = null);

/// <summary>The actual terminal book; reservations remain separate from cash and marked holdings.</summary>
public sealed record RunPortfolioStateResponse(string ValuationBasis, DateTimeOffset CutoffUtc,
    int AdmittedOrderCount, int FilledCandidateCount, int ClosedTradeCount, int FillCount,
    decimal? Cash, decimal? RealizedNetProfit, decimal? Fees, decimal? PositionValue,
    decimal? UnrealizedProfit, decimal? Equity, decimal? GrossExposurePct, string? UnavailableReason,
    IReadOnlyList<RunOpenPosition> OpenPositions, IReadOnlyList<RunPendingOrder> PendingOrders,
    IReadOnlyList<RunPendingExit> PendingExits,
    IReadOnlyList<RunIntradayDecisionDetail>? IntradayDecisions = null, int IntradayDecisionCount = 0);

/// <summary>Server-observed intraday intent and protection facts; clients display them without recalculating trading decisions.</summary>
public sealed record RunIntradayDecisionDetail(string CandidateId, string StrategyId, string Ticker, string Direction,
    string EntryMethod, decimal PreviousSessionHigh, decimal PreviousSessionLow,
    DateTimeOffset BreakoutTimestampUtc, DateTimeOffset ConfirmationTimestampUtc,
    DateTimeOffset ActiveAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset SessionDeadlineUtc,
    decimal LimitPrice, decimal FrozenStopPrice, decimal? ActualEntryPrice, decimal CurrentStopPrice, decimal? TargetPrice,
    string Status, string ResolutionReason, DateTimeOffset? LastObservedAtUtc, DateTimeOffset? ProtectionEffectiveAfterUtc,
    string EntryOrderMode = "next_bar_limit", decimal? TriggerPrice = null,
    decimal? OpeningRangeHigh = null, decimal? OpeningRangeLow = null, decimal? OpeningRangeOpen = null,
    decimal? OpeningRangeClose = null, DateTimeOffset? OpeningRangeCompletedAtUtc = null,
    decimal? OpeningRelativeVolume = null, int? OpeningVolumeBaselineSampleCount = null,
    int MaxFilledParentTradesPerSession = 1, bool RequireRangeReturnBeforeReentry = false);
public sealed record RunOpenPosition(string CandidateId, string StrategyId, string Ticker,
    int SignedQuantity, decimal EntryBasis, decimal? MarkPrice, DateTimeOffset? MarkTimestampUtc,
    decimal? UnrealizedProfit);
public sealed record RunPendingOrder(string CandidateId, string StrategyId, string Ticker,
    string OrderId, string Side, string OrderType, int RemainingQuantity, string Status,
    decimal ReservedNotional, DateTimeOffset KnownAtUtc);
public sealed record RunPendingExit(string CandidateId, string StrategyId, string Ticker,
    DateTimeOffset KnownAtUtc, string Reason);
public sealed record RunHistoryEligibility(string Ticker, string StrategyId, int RequiredBars,
    int AvailableAtStart, int AvailableAtCutoff, DateTimeOffset? FirstEligibleDecisionUtc, string Status);

/// <summary>Publishes committed candle evidence without server paths or provider credentials.</summary>
public sealed record PreparedMarketDataSummaryResponse(string SnapshotId, string SnapshotSha256,
    DateTimeOffset PreparedAtUtc, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<string> Providers, IReadOnlyList<string> DataFeeds, IReadOnlyList<string> PriceAdjustments,
    int ReusedQueryCount, int DownloadedQueryCount, string HistoricalEvidenceNote);
public sealed record RunResultSummary(decimal? StartingCapital, decimal? EndingCapital, decimal? NetProfit,
    decimal? TotalReturnPct, decimal? AverageDailyReturnPct, decimal? MaxDrawdownPct, int? AcceptedTradeCount, int? CandidateTradeCount);
public sealed record RunStrategyProgress(string StrategyName, string Status, int CompletedTickerCount,
    int TotalTickerCount, string? CurrentTicker, IReadOnlyList<string> RecentEvents);

