using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class FixedTargetCostPlannerTests
{
    // The fixed target uses the same adverse-price precision and exact side fees as actual shared simulation, on both sides.
    [Theory]
    [InlineData(true, 98.85, 103.31, 36.828438)]
    [InlineData(false, 101.15, 96.68, 36.912004)]
    public void Fallback_IsNearestLegalTickAtFullCostNetTwoRisk(bool isLong, double stop, double target, double loss)
    {
        var request = Request(isLong, (decimal)stop);
        var result = FixedTargetCostPlanner.Plan(request);
        Assert.True(result.Accepted);
        Assert.Equal((decimal)target, result.TargetPrice);
        Assert.Equal((decimal)loss, result.NetStopRisk);
        Assert.True(result.NetReward >= 2m * result.NetStopRisk);
        var closer = FixedTargetCostPlanner.Plan(request with { KnownZoneTarget = result.TargetPrice + (isLong ? -.01m : .01m) });
        Assert.True(closer.NetReward < 2m * result.NetStopRisk);
        var entry = Fill(request, 100m, isLong ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell, SimulatedOrderType.Market);
        var stopFill = Fill(request, request.StopPrice, isLong ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy, SimulatedOrderType.Market);
        var targetFill = Fill(request, result.TargetPrice!.Value, isLong ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy, SimulatedOrderType.Limit);
        Assert.Equal(entry.AverageFillPrice, result.EntryFillPrice);
        Assert.Equal(stopFill.AverageFillPrice, result.StopFillPrice);
        Assert.Equal(entry.Fees, result.EntryFees);
        Assert.Equal(stopFill.Fees, result.StopExitFees);
        Assert.Equal(targetFill.Fees, result.TargetExitFees);
        Assert.Equal(result.TargetPrice, targetFill.AverageFillPrice);
    }

    // A known positive target needs no 1.5R floor, while a non-covering known target cannot be replaced by a risk fallback.
    [Theory]
    [InlineData(102.5, true)]
    [InlineData(100.06, false)]
    public void KnownZone_PreservesTargetAndUsesOnlyPositiveNetReward(double target, bool accepted)
    {
        var result = FixedTargetCostPlanner.Plan(Request(true, 98.85m) with { KnownZoneTarget = (decimal)target });
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal((decimal)target, result.TargetPrice);
        Assert.Equal(accepted ? "known_zone_target_covers_costs" : "target_does_not_cover_costs", result.Reason);
        if (accepted) Assert.True(result.NetRewardRisk is > 0m and < 1.5m);
    }

    // Fixed fees can reject two shares at a nearby box even though the same legal target covers costs at a larger whole-share quantity.
    [Theory]
    [InlineData(2, false)]
    [InlineData(24, true)]
    public void KnownZone_CostFloorUsesActualWholeShares(int quantity, bool expected)
    {
        var result = FixedTargetCostPlanner.Plan(Request(true, 98.85m) with { Quantity = quantity, KnownZoneTarget = 100.5m });
        Assert.Equal(expected, result.Accepted);
    }

    // A short fallback with risk too wide to pay two net risks cannot manufacture a negative target.
    [Fact]
    public void ShortFallback_RejectsImpossiblePositivePrice()
    {
        var result = FixedTargetCostPlanner.Plan(Request(false, 160m));
        Assert.False(result.Accepted);
        Assert.Null(result.TargetPrice);
        Assert.Equal("risk_target_no_finite_positive_legal_price", result.Reason);
    }

    // A one-tick positive short target is legal even when the entry is less than two ticks above zero.
    [Fact]
    public void ShortFallback_PreservesOneTickBoundary()
    {
        var result = FixedTargetCostPlanner.Plan(new(false, 1, 1.9m, 2m, 1m, 0m, 0m, 0m, 0m, 0m, 0m, 2m));
        Assert.True(result.Accepted);
        Assert.Equal(1m, result.TargetPrice);
    }

    // Legal subpenny targets use the same monotone solver and capped regulatory fees without a floating-point approximation.
    [Fact]
    public void Fallback_RoundsToNearestSubpennyTickWithCappedTaf()
    {
        var request = Request(true, .9m) with { Midpoint = 1m, TickSize = .0001m, FinraTafPerShare = .1m, FinraTafCap = .2m };
        var result = FixedTargetCostPlanner.Plan(request);
        Assert.True(result.Accepted);
        Assert.Equal(0m, result.TargetPrice % request.TickSize);
        Assert.True(result.NetReward >= 2m * result.NetStopRisk);
        var closer = FixedTargetCostPlanner.Plan(request with { KnownZoneTarget = result.TargetPrice - request.TickSize });
        Assert.True(closer.NetReward < 2m * result.NetStopRisk);
    }

    // Freeze the ordinary side-neutral cost settings independently of any future bar's liquidity.
    private static FixedTargetCostRequest Request(bool isLong, decimal stop) => new(isLong, 24, 100m, stop, .01m,
        15m, 1m, 1m, .0000278m, .000166m, 8.3m, 2m);

    // Compare estimates with the existing matcher at an observed opening or at a no-improvement target limit.
    private static ExecutionSimulationResult Fill(FixedTargetCostRequest r, decimal price, SimulatedOrderSide side, SimulatedOrderType type) =>
        new DeterministicExecutionSimulator().Simulate(new(side, type, r.Quantity,
            new(DateTimeOffset.UnixEpoch, price, price, price, price, 100000m),
            new(100m, 10m, 10m, 0m, r.FixedBuyFee, r.FixedSellFee, r.SecFeeRate, r.FinraTafPerShare, r.FinraTafCap),
            type == SimulatedOrderType.Limit ? price : null));
}
