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
    string? ConfigurationKey = null);

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
    JobFailureResponse? Failure);

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
    IReadOnlyList<RunStrategyProgress> Strategies);
public sealed record RunResultSummary(decimal? StartingCapital, decimal? EndingCapital, decimal? NetProfit,
    decimal? TotalReturnPct, decimal? AverageDailyReturnPct, decimal? MaxDrawdownPct, int? AcceptedTradeCount, int? CandidateTradeCount);
public sealed record RunStrategyProgress(string StrategyName, string Status, int CompletedTickerCount,
    int TotalTickerCount, string? CurrentTicker, IReadOnlyList<string> RecentEvents);
