using TradingFlow.Backtesting;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class EventBacktestExecutionReplayTests
{
    private const long Epoch = 1_800_000_000_000_000_000L;
    private static ExecutionEventSimulationPolicy Policy(long latency = 0) =>
        new(new(100,0,0,0,0,0,0,0,0), ExecutionProtectionActivation.NativeParentFullyFilled,
            1000,latency,true,1,1,"raw-fixture-v1");
    private static EventExecutionIntent Intent => new("candidate","X",10,Epoch+1,Epoch+1,9.5m,11,
        SimulatedTimeInForce.ImmediateOrCancel, Epoch+100);
    private static async IAsyncEnumerable<ExecutionEventBatch> Timeline(params (long Ns, decimal? Price)[] observations)
    {
        foreach(var (ns,price) in observations)
        {
            yield return new("X",Epoch+ns,Epoch+ns,ns,
                ns==0 ? [new("q0",Epoch,9,10,100,100,true)] : [],
                price is {} p ? [new("t"+ns,Epoch+ns,p,100,true)] : [],true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EntryAndLaterProtectionExecuteWithoutAnyCandles()
    {
        var result = await EventBacktestExecutionReplay.ReplayAsync(Intent,
            Timeline((0,null),(1,null),(2,9.75m),(3,9.1m),(4,9.1m)),Policy(),[],0);
        Assert.True(result.Flat);
        Assert.Empty(result.UnavailableReasons);
        var fills=result.Events.Where(e=>e.Event.LastFillQuantity>0).ToArray();
        Assert.Equal(new[]{Epoch+2,Epoch+4},fills.Select(e=>e.OccurredAtNanoseconds));
        Assert.Equal(new[]{10m,9m},fills.Select(e=>e.Event.FillPrice!.Value));
        Assert.Empty(result.State.Book.WorkingOrders);
    }

    [Fact]
    public async Task ScheduledExitWaitsForReportedEntryInsteadOfUsingActualPositionEarly()
    {
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent,
            Timeline((0,null),(1,null),(2,9.75m),(3,9.75m),(12,9.75m),(13,9.75m)),
            Policy(10),[new(Epoch+3,Epoch+3)],0);
        var reducing=result.Events.Where(e=>e.Event.IsRiskReducing&&e.Event.LastFillQuantity>0).ToArray();
        Assert.Equal(Epoch+13,Assert.Single(reducing).OccurredAtNanoseconds);
        Assert.True(result.Flat);
    }

    [Fact]
    public async Task ScheduledExitQuantityCannotBackdateThePartialProtectionReceiptItUses()
    {
        static async IAsyncEnumerable<ExecutionEventBatch> PartialStopTimeline()
        {
            foreach (var ns in new long[] { 0, 1, 2, 13, 14, 24, 25 })
            {
                decimal? price = ns switch { 2 or 25 => 9.75m, 13 or 14 => 9.1m, _ => null };
                yield return new("X", Epoch + ns, Epoch + ns, ns,
                    ns == 0 ? [new("q0", Epoch, 9, 10, 100, 100, true)] : [],
                    price is { } p ? [new("t" + ns, Epoch + ns, p, ns == 14 ? 3 : 100, true)] : [], true);
                await Task.CompletedTask;
            }
        }
        var result = await EventBacktestExecutionReplay.ReplayAsync(Intent,
            PartialStopTimeline(), Policy(10), [new(Epoch + 15, Epoch + 15)], 0);
        Assert.Equal(Epoch + 24, result.State.OrderActivationTimes["candidate:liquidation"]);
        Assert.Equal(Epoch + 25, Assert.Single(result.Events,
            e => e.Event.ClientOrderId == "candidate:liquidation" && e.Event.LastFillQuantity > 0).OccurredAtNanoseconds);
        Assert.Empty(result.UnavailableReasons);
        Assert.True(result.Flat);
    }

    [Fact]
    public async Task CoverageFailureIsNotReplacedWithAnOhlcNoTouchAssumption()
    {
        static async IAsyncEnumerable<ExecutionEventBatch> Missing()
        {
            yield return new("X",Epoch,Epoch,0,[],[],false);
            await Task.CompletedTask;
        }
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent,Missing(),Policy(),[],0);
        Assert.Contains("execution_interval_coverage_unavailable",result.UnavailableReasons);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task EmptyOrTooShortTimelineIsExplicitlyUnavailable()
    {
        var empty=await EventBacktestExecutionReplay.ReplayAsync(Intent,Timeline(),Policy(),[],0);
        Assert.Contains("event_evidence_empty",empty.UnavailableReasons);
        Assert.Contains("event_evidence_ends_before_entry_ready",empty.UnavailableReasons);
        var early=await EventBacktestExecutionReplay.ReplayAsync(Intent,Timeline((0,null)),Policy(),[],0);
        Assert.Contains("event_evidence_ends_before_entry_ready",early.UnavailableReasons);
    }

    [Fact]
    public async Task WaitingControllerLatencyDoesNotDiscardMarketObservations()
    {
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent,
            Timeline((0,null),(3,null),(4,9.75m),(5,9.75m),(14,9.75m),(15,9.75m),(16,9.75m),(17,9.75m)),
            Policy(10),[new(Epoch+5,Epoch+5)],2);
        Assert.Equal(Epoch+17,result.State.LastEventTimes["X"]);
        Assert.True(result.Flat);
        Assert.DoesNotContain(result.Events,e=>e.Event.IsRiskReducing&&e.Event.LastFillQuantity>0&&e.OccurredAtNanoseconds<Epoch+17);
    }

    [Fact]
    public async Task ExitRacingAnUnreportedStopFillRecordsRejectionWithoutControllerForesight()
    {
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent,
            Timeline((0,null),(1,null),(2,9.75m),(12,9.75m),(13,9.1m),(14,9.1m),(15,9.75m),(16,9.75m)),
            Policy(10),[new(Epoch+15,Epoch+15)],0);
        var rejection=Assert.Single(result.Events,e=>e.Event.State=="rejected");
        Assert.Equal(Epoch+15,rejection.OccurredAtNanoseconds);
        Assert.Equal(Epoch+25,rejection.KnownAtNanoseconds);
        Assert.Contains("broker_route_rejected:physical_parent_exposure_changed_before_route",result.UnavailableReasons);
        Assert.True(result.Flat);
    }

    [Theory]
    [InlineData(SimulatedTimeInForce.Day)]
    [InlineData(SimulatedTimeInForce.ImmediateOrCancel)]
    public async Task EntryCannotRemainPendingBeyondItsDeclaredRouteExpiry(SimulatedTimeInForce tif)
    {
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent with {EntryTimeInForce=tif,EntryRouteExpiresAtNanoseconds=Epoch+5},
            Timeline((0,null),(1,null),(4,null),(6,9.75m)),Policy(),[],0);
        Assert.DoesNotContain(result.Events,e=>e.Event.LastFillQuantity>0);
        Assert.Contains(result.Events,e=>e.Event.State=="canceled"&&e.Event.ClientOrderId=="candidate:entry");
        Assert.Empty(result.State.Book.WorkingOrders);
    }

    [Theory]
    [InlineData(SimulatedTimeInForce.Day)]
    [InlineData(SimulatedTimeInForce.ImmediateOrCancel)]
    public async Task ATradeExactlyAtExclusiveExpiryCannotFillTheEntry(SimulatedTimeInForce tif)
    {
        var result=await EventBacktestExecutionReplay.ReplayAsync(Intent with {EntryTimeInForce=tif,EntryRouteExpiresAtNanoseconds=Epoch+5},
            Timeline((0,null),(1,null),(4,null),(5,9.75m)),Policy(),[],0);
        Assert.DoesNotContain(result.Events,e=>e.Event.LastFillQuantity>0);
        Assert.Contains(result.Events,e=>e.Event.Reason=="route_expired"&&e.OccurredAtNanoseconds==Epoch+5);
    }
}
