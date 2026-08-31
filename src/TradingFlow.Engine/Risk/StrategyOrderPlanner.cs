using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Engine.Risk;

public sealed record StrategyOrderPlanningRequest(
    StrategyOrderPlan OrderPlan,
    TradeSignal ExecutionSignal,
    decimal AccountEquity,
    OrderPlanningRiskLimits RiskLimits,
    decimal FixedEntryFee,
    decimal FixedExitFee,
    decimal? RequestedNotional = null);

public sealed record StrategyOrderPlanningResult(
    FinalizedOrder? Order,
    OrderPlanRejection? RiskRejection,
    string? StopRejectionReason)
{
    public bool IsAccepted => Order is not null;
}

/// <summary>
/// Applies account risk to the immutable order plan emitted by the decision
/// kernel. It must not recalculate strategy direction or the pre-fill stop.
/// </summary>
public sealed class StrategyOrderPlanner
{
    private readonly SharedOrderRiskPlanner riskPlanner = new();

    public StrategyOrderPlanningResult Plan(StrategyOrderPlanningRequest request)
    {
        var side = request.OrderPlan.Direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? PlannedOrderSide.Short
            : request.OrderPlan.Direction.Equals("long", StringComparison.OrdinalIgnoreCase)
                ? PlannedOrderSide.Long
                : throw new InvalidOperationException(
                    $"Unsupported canonical order direction '{request.OrderPlan.Direction}'.");

        var riskResult = riskPlanner.Plan(
            new OrderPlanningRequest(
                request.ExecutionSignal.Ticker,
                side,
                request.AccountEquity,
                request.ExecutionSignal.CurrentPrice,
                PlannedStopRequest.FromResolvedPrice(
                    PlannedStopKind.Structural,
                    request.OrderPlan.InitialStopPrice),
                request.RiskLimits,
                new EstimatedOrderExecutionCosts(
                    request.OrderPlan.SlippageBps,
                    request.FixedEntryFee,
                    request.FixedExitFee),
                request.RequestedNotional));
        if (!riskResult.IsAccepted || riskResult.Order is null)
        {
            return new StrategyOrderPlanningResult(
                null,
                riskResult.Rejection,
                null);
        }

        var planned = riskResult.Order;
        var triggerRiskPerShare = Math.Abs(
            planned.EstimatedEntryPrice - planned.StopTriggerPrice);
        var takeProfitPrice = request.OrderPlan.ProfitTargetMode.Equals(
            "vwap",
            StringComparison.OrdinalIgnoreCase)
                ? request.OrderPlan.ProfitTargetReferencePrice ?? 0m
                : side == PlannedOrderSide.Short
                    ? planned.EstimatedEntryPrice -
                        (triggerRiskPerShare * request.OrderPlan.TargetRMultiple)
                    : planned.EstimatedEntryPrice +
                        (triggerRiskPerShare * request.OrderPlan.TargetRMultiple);
        var targetOnCorrectSide = side == PlannedOrderSide.Short
            ? takeProfitPrice < planned.EstimatedEntryPrice
            : takeProfitPrice > planned.EstimatedEntryPrice;
        if (takeProfitPrice <= 0m || !targetOnCorrectSide)
        {
            return new StrategyOrderPlanningResult(
                null,
                new OrderPlanRejection(
                    OrderPlanRejectionCode.InvalidStop,
                    "The configured target is non-positive or is on the wrong side of the planned entry."),
                null);
        }

        return new StrategyOrderPlanningResult(
            new FinalizedOrder(
                planned.Symbol,
                request.OrderPlan.StrategyName,
                planned.Quantity,
                Decimal.Round(planned.EstimatedEntryPrice, 4),
                Decimal.Round(planned.StopTriggerPrice, 4),
                Decimal.Round(takeProfitPrice, 4),
                request.ExecutionSignal.Timestamp),
            null,
            null);
    }
}
