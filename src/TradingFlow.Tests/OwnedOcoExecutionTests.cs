using TradingFlow.Engine.Risk;
namespace TradingFlow.Tests;
public sealed class OwnedOcoExecutionTests
{
    [Fact]
    public void TargetCancelsItsAlreadyVisitedStop_WithoutCancellingAnotherOwnersProtection()
    {
        var t=DateTimeOffset.Parse("2026-06-01T13:30:00Z");var sim=new DeterministicExecutionSimulator();
        var policy=new ExecutionSimulationPolicy(100,0,0,0,0,0,0,0,0,InputPriceIncludesExecutionFriction:true);
        var first=sim.Advance(new(ExecutionBookState.Empty,new("X",t,t.AddMinutes(5),t.AddMinutes(5),1,true,10,10,10,10,1000),
            [new(ExecutionCommandKind.Submit,"entry1","X",SimulatedOrderSide.Buy,SimulatedOrderType.Market,10,t,1,false),
             new(ExecutionCommandKind.Submit,"entry2","X",SimulatedOrderSide.Buy,SimulatedOrderType.Market,10,t,2,false)],policy));
        var now=t.AddMinutes(5);
        var second=sim.Advance(new(first.State,new("X",now,now.AddMinutes(5),now.AddMinutes(5),2,true,10,13,9.5m,12,1000),
            [new(ExecutionCommandKind.Submit,"stop1","X",SimulatedOrderSide.Sell,SimulatedOrderType.Stop,10,now,3,true,TimeInForce:SimulatedTimeInForce.GoodTillCanceled,StopPrice:9,OcoGroupId:"owner1"),
             new(ExecutionCommandKind.Submit,"target1","X",SimulatedOrderSide.Sell,SimulatedOrderType.Limit,10,now,4,true,TimeInForce:SimulatedTimeInForce.GoodTillCanceled,LimitPrice:12,OcoGroupId:"owner1"),
             new(ExecutionCommandKind.Submit,"stop2","X",SimulatedOrderSide.Sell,SimulatedOrderType.Stop,10,now,5,true,TimeInForce:SimulatedTimeInForce.GoodTillCanceled,StopPrice:7,OcoGroupId:"owner2")],policy));
        Assert.False(second.State.WorkingOrders.ContainsKey("stop1"));Assert.True(second.State.WorkingOrders.ContainsKey("stop2"));
        Assert.Equal(10,second.State.Positions["X"].SignedQuantity);
        Assert.Contains(second.Events,e=>e.ClientOrderId=="stop1"&&e.State=="canceled"&&e.Reason=="oco_sibling_filled");
    }
    [Theory]
    [InlineData(null)]
    [InlineData("different-owner")]
    public void ReplacementCannotDetachStopFromItsOwnedOcoGroup(string? replacementGroup)
    {
        var t = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var simulator = new DeterministicExecutionSimulator();
        var policy = new ExecutionSimulationPolicy(100, 0, 0, 0, 0, 0, 0, 0, 0,
            InputPriceIncludesExecutionFriction: true);
        var entered = simulator.Advance(new(ExecutionBookState.Empty,
            new("X", t, t.AddMinutes(5), t.AddMinutes(5), 1, true, 10, 10, 10, 10, 1000),
            [new(ExecutionCommandKind.Submit, "entry", "X", SimulatedOrderSide.Buy,
                SimulatedOrderType.Market, 10, t, 1, false)], policy));
        var next = t.AddMinutes(5);
        var protectedState = simulator.Advance(new(entered.State,
            new("X", next, next.AddMinutes(5), next.AddMinutes(5), 2, true, 10, 11, 10, 10, 1000),
            [new(ExecutionCommandKind.Submit, "stop", "X", SimulatedOrderSide.Sell,
                SimulatedOrderType.Stop, 10, next, 2, true,
                TimeInForce: SimulatedTimeInForce.GoodTillCanceled, StopPrice: 9, OcoGroupId: "owner")], policy));
        next = next.AddMinutes(5);
        var error = Assert.Throws<InvalidOperationException>(() => simulator.Advance(new(protectedState.State,
            new("X", next, next.AddMinutes(5), next.AddMinutes(5), 3, true, 10, 11, 10, 10, 1000),
            [new(ExecutionCommandKind.Replace, "replacement", "X", SimulatedOrderSide.Sell,
                SimulatedOrderType.Stop, 10, next, 3, true,
                TimeInForce: SimulatedTimeInForce.GoodTillCanceled, StopPrice: 9.5m,
                TargetClientOrderId: "stop", OcoGroupId: replacementGroup)], policy)));
        Assert.Contains("OCO group", error.Message);
        Assert.Equal("owner", protectedState.State.WorkingOrders["stop"].Command.OcoGroupId);
    }

}
