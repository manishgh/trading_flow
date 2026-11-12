namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestCandidateTrade(
    string Ticker,
    string StrategyName,
    string Direction,
    DateTimeOffset EntryTimestamp,
    decimal EntryPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    DateTimeOffset? ExitTimestamp,
    decimal? ExitPrice,
    string ExitReason,
    decimal StopDistance,
    decimal EntryLiquidityEvidenceVolume = 0m,
    decimal SelectionScore = 0m,
    string InitialStopKind = "structural",
    string StrategyId = "",
    string CandidateId = "")
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CandidateExecutionReplay? ExecutionReplay { get; init; }

    // Sizing input known before the open; null keeps the original open-derived sizing.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PlanningReferencePrice { get; init; }

    // A completed exit decision can await an executable slice beyond the frozen study.
    public BacktestPendingExitDecision? PendingExitDecision { get; init; }
    public string DecisionStrategyId { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IntradayExecutionEvidence? IntradayExecution { get; init; }

    // The bar timeframe the orders fill on when it is finer than the strategy's execution timeframe (decision-hour timing:
    // a daily strategy filling on the last hourly bar); null fills on the strategy's own execution bars.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FillTimeframe { get; init; }
}

public sealed record BacktestPendingExitDecision(DateTimeOffset KnownAtUtc, string Reason);

public sealed record BacktestTrade(
    string Ticker,
    string StrategyName,
    string Direction,
    DateTimeOffset EntryTimestamp,
    DateTimeOffset ExitTimestamp,
    int ShareQuantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    string ExitReason,
    decimal GrossProfit,
    decimal Fees,
    decimal NetProfit,
    int RequestedShareQuantity = 0,
    string EntryFillStatus = "filled",
    decimal EntryEstimatedParticipationPct = 0m,
    string StrategyId = "",
    string CandidateId = "");
