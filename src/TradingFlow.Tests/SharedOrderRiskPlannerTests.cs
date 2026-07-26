using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class SharedOrderRiskPlannerTests
{
    private readonly SharedOrderRiskPlanner _planner = new();

    [Fact]
    public void Plan_LongStructuralStop_PreservesStopAndUsesPositionNotionalCap()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Long,
            stop: PlannedStopRequest.FromStructuralPrice(99m)));

        Assert.True(result.IsAccepted);
        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.Equal(200, order.Quantity);
        Assert.Equal(20_000m, order.DeployedNotional);
        Assert.Equal(200m, order.PlannedLoss);
        Assert.Equal(1_000m, order.AccountRiskBudget);
        Assert.Equal(99m, order.StopTriggerPrice);
    }

    [Fact]
    public void Plan_WideStructuralStop_IsPreservedAndQuantityAbsorbsRisk()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Long,
            stop: PlannedStopRequest.FromStructuralPrice(90m),
            maxPositionNotionalPct: 100m));

        Assert.True(result.IsAccepted);
        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.Equal(100, order.Quantity);
        Assert.Equal(10_000m, order.DeployedNotional);
        Assert.Equal(1_000m, order.PlannedLoss);
        Assert.Equal(90m, order.StopTriggerPrice);
    }

    [Fact]
    public void Plan_LongAtrStop_SizesByAccountRiskBudget()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Long,
            stop: PlannedStopRequest.FromAtr(atr: 1m, atrMultiple: 2m),
            accountRiskBudgetPct: 0.25m,
            maxPositionNotionalPct: 100m));

        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.True(result.IsAccepted);
        Assert.Equal(125, order.Quantity);
        Assert.Equal(12_500m, order.DeployedNotional);
        Assert.Equal(250m, order.AccountRiskBudget);
        Assert.Equal(250m, order.PlannedLoss);
        Assert.Equal(98m, order.StopTriggerPrice);
    }

    [Fact]
    public void Plan_ShortStructuralStop_UsesAdversePriceDirection()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Short,
            stop: PlannedStopRequest.FromStructuralPrice(101m)));

        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.True(result.IsAccepted);
        Assert.Equal(PlannedOrderSide.Short, order.Side);
        Assert.Equal(200, order.Quantity);
        Assert.Equal(20_000m, order.DeployedNotional);
        Assert.Equal(1m, order.EstimatedPriceLossPerShare);
        Assert.Equal(200m, order.PlannedLoss);
    }

    [Fact]
    public void Plan_ShortAtrStop_ResolvesStopAboveEntry()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Short,
            stop: PlannedStopRequest.FromAtr(atr: 0.5m, atrMultiple: 2m)));

        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.True(result.IsAccepted);
        Assert.Equal(101m, order.StopTriggerPrice);
        Assert.Equal(101m, order.EstimatedStopFillPrice);
        Assert.Equal(200m, order.PlannedLoss);
    }

    [Fact]
    public void Plan_WithSlippageAndFixedFees_IncludesBothInAccountRisk()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Long,
            stop: PlannedStopRequest.FromStructuralPrice(99.5m),
            executionCosts: new EstimatedOrderExecutionCosts(
                SlippageBps: 5m,
                FixedEntryFee: 2m,
                FixedExitFee: 2m)));

        var order = Assert.IsType<PlannedOrder>(result.Order);
        Assert.True(result.IsAccepted);
        Assert.Equal(199, order.Quantity);
        Assert.Equal(100.05m, order.EstimatedEntryPrice);
        Assert.Equal(99.45025m, order.EstimatedStopFillPrice);
        Assert.Equal(4m, order.EstimatedFixedFees);
        Assert.Equal(123.35025m, order.PlannedLoss);
        Assert.True(order.PlannedLoss <= order.AccountRiskBudget);
    }

    [Fact]
    public void Plan_WrongSideStructuralStop_ReturnsInvalidStopReason()
    {
        var result = _planner.Plan(CreateRequest(
            side: PlannedOrderSide.Short,
            stop: PlannedStopRequest.FromStructuralPrice(99m)));

        Assert.False(result.IsAccepted);
        Assert.Equal(OrderPlanRejectionCode.InvalidStop, result.Rejection?.Code);
        Assert.Equal(
            "A short stop must be above the estimated entry price.",
            result.Rejection?.Reason);
    }

    private static OrderPlanningRequest CreateRequest(
        PlannedOrderSide side,
        PlannedStopRequest stop,
        decimal accountRiskBudgetPct = 1m,
        decimal maxPositionNotionalPct = 20m,
        EstimatedOrderExecutionCosts? executionCosts = null)
    {
        return new OrderPlanningRequest(
            Symbol: "test",
            Side: side,
            AccountEquity: 100_000m,
            ReferenceEntryPrice: 100m,
            Stop: stop,
            RiskLimits: new OrderPlanningRiskLimits(
                AccountRiskBudgetPct: accountRiskBudgetPct,
                MaxPositionNotionalPct: maxPositionNotionalPct),
            ExecutionCosts: executionCosts ?? new EstimatedOrderExecutionCosts());
    }
}
