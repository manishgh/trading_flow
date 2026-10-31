namespace TradingFlow.Domain.Backtesting;

// Original execution stamps remain available beside the coarser source-session accounting date.
public sealed record BacktestPortfolioFill(string CandidateId, string StrategyId, string Ticker,
    bool IsBuy, bool IsEntry, int Quantity, decimal Price, decimal Fees, DateTimeOffset TimestampUtc,
    DateOnly SourceSessionDate);
public sealed record BacktestOpenPosition(string CandidateId, string StrategyId, string Ticker,
    int SignedQuantity, decimal EntryBasis, decimal? MarkPrice, DateTimeOffset? MarkTimestampUtc,
    decimal? UnrealizedProfit);
public sealed record BacktestPendingOrder(string CandidateId, string StrategyId, string Ticker,
    string OrderId, string Side, string OrderType, int RemainingQuantity, string Status,
    decimal ReservedNotional, DateTimeOffset KnownAtUtc);
public sealed record BacktestPendingExit(string CandidateId, string StrategyId, string Ticker,
    DateTimeOffset KnownAtUtc, string Reason);
public sealed record BacktestSessionObservation(DateOnly SessionDate, decimal Cash, decimal RealizedNetProfit,
    decimal Fees, decimal? PositionValue, decimal? UnrealizedProfit, decimal? Equity,
    decimal? GrossExposurePct, int OpenPositionCount, string? UnavailableReason);
public sealed record BacktestHistoryEligibility(string Ticker, string StrategyId, int RequiredBars,
    int AvailableAtStart, int AvailableAtCutoff, DateTimeOffset? FirstEligibleDecisionUtc, string Status);
public sealed record BacktestSessionPositionMark(DateOnly SessionDate, string CandidateId, string Ticker,
    int SignedQuantity, decimal EntryBasis, decimal? Price, DateTimeOffset? OriginalBarTimestampUtc);
public sealed record BacktestAdmittedOrder(string CandidateId, string StrategyId, string Ticker,
    int RequestedQuantity, decimal ReferencePrice, string Direction, DateTimeOffset EntryTimestampUtc)
{
    public string DecisionStrategyId { get; init; } = "";
}

/// <summary>Bounded display evidence copied from a frozen intraday hypothesis and its observed replay events.</summary>
public sealed record BacktestIntradayDecisionDetail(string CandidateId, string StrategyId, string Ticker, string Direction,
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

// A terminal book is a snapshot, not a command to sell its holdings.
public sealed record BacktestPortfolioState(string ValuationBasis, DateTimeOffset CutoffUtc,
    int AdmittedOrderCount, int FilledCandidateCount, int ClosedTradeCount,
    IReadOnlyList<BacktestPortfolioFill> Fills, IReadOnlyList<BacktestOpenPosition> OpenPositions,
    IReadOnlyList<BacktestPendingOrder> PendingOrders, IReadOnlyList<BacktestPendingExit> PendingExits,
    IReadOnlyList<BacktestSessionObservation> Sessions)
{
    public DateTimeOffset StartUtc { get; init; }
    public IReadOnlyList<BacktestSessionPositionMark> SessionMarks { get; init; } = [];
    public IReadOnlyList<BacktestAdmittedOrder> AdmittedOrders { get; init; } = [];
    public DateOnly? UnavailableFromSession { get; init; }
    public string? UnavailableReason { get; init; }
    public IReadOnlyList<BacktestIntradayDecisionDetail> IntradayDecisions { get; init; } = [];
    public int IntradayDecisionCount { get; init; }
}
