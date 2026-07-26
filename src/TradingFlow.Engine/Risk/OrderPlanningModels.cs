namespace TradingFlow.Engine.Risk;

public enum PlannedOrderSide
{
    Long,
    Short
}

public enum PlannedStopKind
{
    Atr,
    Structural
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

public sealed record OrderPlanningRiskLimits(
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct);

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
