using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Risk;

public sealed record StrategyOrderPlanningRequest(
    StrategyDefinition Strategy,
    TradeSignal StrategySignal,
    TradeSignal ExecutionSignal,
    PlannedOrderSide Side,
    decimal AccountEquity,
    OrderPlanningRiskLimits RiskLimits,
    decimal FixedEntryFee,
    decimal FixedExitFee,
    int StopContextIndex,
    IReadOnlyList<OhlcvBar> ExecutionBars,
    IReadOnlyList<IndicatorSnapshot> ExecutionSnapshots,
    decimal? RequestedNotional = null);

public sealed record StrategyOrderPlanningResult(
    FinalizedOrder? Order,
    OrderPlanRejection? RiskRejection,
    string? StopRejectionReason)
{
    public bool IsAccepted => Order is not null;
}

/// <summary>
/// Produces the same initial stop, risk sizing, and bracket prices for paper/live
/// callers. Backtests use the same stop resolver and order-risk planner around
/// their simulated fill price.
/// </summary>
public sealed class StrategyOrderPlanner
{
    private readonly StrategyInitialStopResolver stopResolver = new();
    private readonly SharedOrderRiskPlanner riskPlanner = new();

    public StrategyOrderPlanningResult Plan(StrategyOrderPlanningRequest request)
    {
        var stopSignal = request.StrategySignal with
        {
            CurrentAtr = request.ExecutionSignal.CurrentAtr
        };
        var stopResult = stopResolver.Resolve(
            new StrategyInitialStopRequest(
                request.Strategy,
                stopSignal,
                request.Side,
                request.ExecutionSignal.CurrentPrice,
                request.StopContextIndex,
                request.ExecutionBars,
                request.ExecutionSnapshots));
        if (!stopResult.IsResolved || stopResult.Stop is null)
        {
            return new StrategyOrderPlanningResult(
                null,
                null,
                stopResult.RejectionReason ?? "initial_stop_unresolved");
        }

        var riskResult = riskPlanner.Plan(
            new OrderPlanningRequest(
                request.ExecutionSignal.Ticker,
                request.Side,
                request.AccountEquity,
                request.ExecutionSignal.CurrentPrice,
                stopResult.Stop,
                request.RiskLimits,
                new EstimatedOrderExecutionCosts(
                    request.Strategy.Execution.SlippageBps,
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
        var takeProfitPrice = request.Side == PlannedOrderSide.Short
            ? planned.EstimatedEntryPrice -
                (triggerRiskPerShare * request.Strategy.ExitRules.TargetRMultiple)
            : planned.EstimatedEntryPrice +
                (triggerRiskPerShare * request.Strategy.ExitRules.TargetRMultiple);
        if (takeProfitPrice <= 0m)
        {
            return new StrategyOrderPlanningResult(
                null,
                new OrderPlanRejection(
                    OrderPlanRejectionCode.InvalidStop,
                    "The configured target produces a non-positive take-profit price."),
                null);
        }

        return new StrategyOrderPlanningResult(
            new FinalizedOrder(
                planned.Symbol,
                request.Strategy.StrategyName,
                planned.Quantity,
                Decimal.Round(planned.EstimatedEntryPrice, 4),
                Decimal.Round(planned.StopTriggerPrice, 4),
                Decimal.Round(takeProfitPrice, 4),
                request.ExecutionSignal.Timestamp),
            null,
            null);
    }
}
