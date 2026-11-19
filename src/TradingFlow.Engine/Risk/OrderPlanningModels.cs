namespace TradingFlow.Engine.Risk;

public enum PlannedOrderSide
{
    Long,
    Short
}

public enum PlannedStopKind
{
    Atr,
    Structural,
    /// <summary>No protective stop: size by position notional only (long research backtests).</summary>
    None
}

public enum OrderPlanRejectionCode
{
    InvalidSymbol,
    InvalidAccountEquity,
    InvalidReferenceEntryPrice,
    InvalidRiskLimits,
    InvalidExecutionCosts,
    InvalidRequestedNotional,
    InvalidStop,
    AccountRiskBudgetConsumedByCosts,
    PositionNotionalTooSmall,
    PlannedLossExceedsAccountRiskBudget
}

/// <param name="EntryRiskScale">
/// The one per-entry multiplier of the account risk budget, from the strategy's regime rule (VIX sizing): 1 = the
/// strategy's normal risk, 0.5 = half. Every backtest and paper path passes the value its regime check returned, so the
/// same entry is sized the same way everywhere. Must be above 0 and at most 1.
/// </param>
public sealed record OrderPlanningRiskLimits(
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
    decimal EntryRiskScale = 1m)
{
    /// <summary>The account risk budget this entry may use, in percent of equity, after the per-entry scale.</summary>
    public decimal EffectiveAccountRiskBudgetPct => AccountRiskBudgetPct * EntryRiskScale;
}

public sealed record EstimatedOrderExecutionCosts(
    decimal SlippageBps = 0m,
    decimal FixedEntryFee = 0m,
    decimal FixedExitFee = 0m)
{
    public decimal TotalFixedFees => FixedEntryFee + FixedExitFee;
}

public sealed record PlannedStopRequest(
    PlannedStopKind Kind,
    decimal? StructuralStopPrice = null,
    decimal? Atr = null,
    decimal? AtrMultiple = null,
    decimal? ResolvedStopPrice = null)
{
    public static PlannedStopRequest FromAtr(decimal atr, decimal atrMultiple) =>
        new(PlannedStopKind.Atr, Atr: atr, AtrMultiple: atrMultiple);

    public static PlannedStopRequest FromStructuralPrice(decimal stopPrice) =>
        new(PlannedStopKind.Structural, StructuralStopPrice: stopPrice);

    public static PlannedStopRequest FromResolvedPrice(
        PlannedStopKind kind,
        decimal stopPrice) =>
        new(kind, ResolvedStopPrice: stopPrice);

    /// <summary>Request for a position with no protective stop order.</summary>
    public static PlannedStopRequest NoStop() => new(PlannedStopKind.None);
}

public sealed record OrderPlanningRequest(
    string Symbol,
    PlannedOrderSide Side,
    decimal AccountEquity,
    decimal ReferenceEntryPrice,
    PlannedStopRequest Stop,
    OrderPlanningRiskLimits RiskLimits,
    EstimatedOrderExecutionCosts ExecutionCosts,
    decimal? RequestedNotional = null);

public sealed record PlannedOrder(
    string Symbol,
    PlannedOrderSide Side,
    int Quantity,
    decimal EstimatedEntryPrice,
    decimal StopTriggerPrice,
    decimal EstimatedStopFillPrice,
    decimal DeployedNotional,
    decimal EstimatedPriceLossPerShare,
    decimal EstimatedFixedFees,
    decimal PlannedLoss,
    decimal AccountRiskBudget);

public sealed record OrderPlanRejection(
    OrderPlanRejectionCode Code,
    string Reason);

public sealed record OrderPlanningResult(
    bool IsAccepted,
    PlannedOrder? Order,
    OrderPlanRejection? Rejection)
{
    public static OrderPlanningResult Accept(PlannedOrder order) =>
        new(true, order, null);

    public static OrderPlanningResult Reject(OrderPlanRejectionCode code, string reason) =>
        new(false, null, new OrderPlanRejection(code, reason));
}
