using System.Collections.Immutable;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class MixedTimeframeDecisionTimingTests
{
    [Fact]
    public void DailyDecisionAtAvailability_PreservesDailyContextAndTrigger()
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var source = request.BarsByTimeframe["1d"][^1].Timestamp;
        request = request with { AsOfUtc = source.AddDays(1) };
        var result = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(result.IsTriggered, result.NoEntryReason);
        Assert.Equal(source.AddDays(1), result.OrderPlan!.TriggeredAtUtc);
        Assert.Equal(source, result.OrderPlan.StopContextTimestampUtc);
        Assert.Equal(source, StrategyDecisionRequestAssembler.CreateExecutionSignal(result.Signal!, result.OrderPlan,
            request.SnapshotsByTimeframe["1d"]).Timestamp);
    }

    [Theory]
    [InlineData("2026-06-10T04:00:00Z", "2026-06-10T19:50:00Z", "2026-06-11T04:00:00Z")]
    [InlineData("2026-07-02T04:00:00Z", "2026-07-02T16:50:00Z", "2026-07-06T13:30:00Z")]
    public void PriorSessionContext_CannotBackdateAnActionableDecision(string setup, string context, string asOf)
    {
        var request = MixedRequest(DateTimeOffset.Parse(setup), DateTimeOffset.Parse(context), DateTimeOffset.Parse(asOf));
        var result = new StrategyDecisionKernel().Evaluate(request);

        Assert.True(result.IsTriggered, result.NoEntryReason);
        var plan = Assert.IsType<StrategyOrderPlan>(result.OrderPlan);
        Assert.Equal(request.AsOfUtc, plan.TriggeredAtUtc);
        Assert.True(plan.TriggeredAtUtc >= plan.SetupAvailableAtUtc);
        Assert.Equal(1, plan.StopContextIndex);
        var executionSignal = StrategyDecisionRequestAssembler.CreateExecutionSignal(
            result.Signal!, plan, request.SnapshotsByTimeframe["5m"]);
        Assert.Equal(request.SnapshotsByTimeframe["5m"][1].Timestamp, executionSignal.Timestamp);
        Assert.Equal(plan.TriggerReferencePrice, executionSignal.CurrentPrice);
        var planning = new StrategyOrderPlanningRequest(plan, executionSignal, 10_000m,
            new OrderPlanningRiskLimits(1m, 25m), 1m, 1m);
        var finalized = new StrategyOrderPlanner().Plan(planning);
        var priorTiming = new StrategyOrderPlanner().Plan(planning with
        {
            OrderPlan = plan with { TriggeredAtUtc = plan.StopContextTimestampUtc.AddMinutes(5) }
        });
        Assert.True(finalized.IsAccepted);
        Assert.Equal(plan.TriggeredAtUtc, finalized.Order!.ExecutionTimestamp);
        Assert.Equal(priorTiming.Order!.ShareQuantity, finalized.Order.ShareQuantity);
        Assert.Equal(priorTiming.Order.StopLossPrice, finalized.Order.StopLossPrice);
        Assert.Equal(priorTiming.Order.TakeProfitPrice, finalized.Order.TakeProfitPrice);
    }

    [Fact]
    public void DelayedReceipt_CannotCreateAnOrderBeforeEvidenceWasKnown()
    {
        var known = DateTimeOffset.Parse("2026-06-11T13:32:00Z");
        var request = MixedRequest(DateTimeOffset.Parse("2026-06-10T04:00:00Z"),
            DateTimeOffset.Parse("2026-06-10T19:50:00Z"), known);
        request = request with { BarsByTimeframe = request.BarsByTimeframe.SetItem("5m",
            request.BarsByTimeframe["5m"].Select(b => b with { KnownAtUtc = known }).ToImmutableArray()) };
        var result = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(result.IsTriggered, result.NoEntryReason);
        Assert.Equal(known, result.OrderPlan!.TriggeredAtUtc);
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(
            request with { AsOfUtc = known.AddSeconds(-1) }));
    }

    [Fact]
    public void ExecutionSignal_RejectsMissingOrFutureContext()
    {
        var request = MixedRequest(DateTimeOffset.Parse("2026-06-10T04:00:00Z"),
            DateTimeOffset.Parse("2026-06-10T19:50:00Z"), DateTimeOffset.Parse("2026-06-11T04:00:00Z"));
        var result = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(result.IsTriggered, result.NoEntryReason);
        var plan = result.OrderPlan!;
        Assert.Throws<InvalidOperationException>(() => StrategyDecisionRequestAssembler.CreateExecutionSignal(
            result.Signal!, plan with { StopContextIndex = 99 }, request.SnapshotsByTimeframe["5m"]));
        var future = request.SnapshotsByTimeframe["5m"].Select(s => s with { Timestamp = request.AsOfUtc }).ToArray();
        Assert.Throws<InvalidOperationException>(() => StrategyDecisionRequestAssembler.CreateExecutionSignal(result.Signal!, plan, future));
        var reorderedContext = request.SnapshotsByTimeframe["5m"].Select(s => s with { Timestamp = s.Timestamp.AddDays(-1) }).ToArray();
        Assert.Throws<InvalidOperationException>(() => StrategyDecisionRequestAssembler.CreateExecutionSignal(result.Signal!, plan, reorderedContext));
    }

    private static StrategyDecisionRequest MixedRequest(DateTimeOffset setup, DateTimeOffset context, DateTimeOffset asOf)
    {
        var original = StrategyDecisionKernelTests.CreateRequest();
        var strategy = original.Strategy with { Execution = original.Strategy.Execution with { Timeframe = "5m" } };
        var identity = new StrategyArtifactIdentity(original.StrategyIdentity.StrategyId, original.StrategyIdentity.SemanticVersion, EvidenceCanonicalJson.ComputeSha256(
            new CanonicalStrategyDocument(original.StrategyIdentity.StrategyId, original.StrategyIdentity.SemanticVersion,
                strategy, original.AdmissionProfile)));
        var daily = original.BarsByTimeframe["1d"].Select((b,i) => b with { Timestamp = setup.AddDays(i-2) }).ToImmutableArray();
        var snapshots = original.SnapshotsByTimeframe["1d"].Select((s,i) => s with { Timestamp = daily[i].Timestamp }).ToImmutableArray();
        var fine = daily.TakeLast(2).Select((b,i) => b with { Timeframe = "5m", Timestamp = context.AddMinutes(i*5) }).ToImmutableArray();
        var fineSnapshots = snapshots.TakeLast(2).Select((s,i) => s with { Timeframe = "5m", Timestamp = fine[i].Timestamp }).ToImmutableArray();
        return original with
        {
            Strategy = strategy, StrategyIdentity = identity, AsOfUtc = asOf,
            BarsByTimeframe = original.BarsByTimeframe.SetItem("1d",daily).Add("5m",fine),
            SnapshotsByTimeframe = original.SnapshotsByTimeframe.SetItem("1d",snapshots).Add("5m",fineSnapshots),
            Candidate = original.Candidate with
            {
                Identity = original.Candidate.Identity with { StrategyContentSha256 = identity.ContentSha256,
                    DiscoveryWindowStartUtc = asOf.AddMinutes(-10), DiscoveryWindowEndUtc = asOf.AddMinutes(5) },
                Discovery = original.Candidate.Discovery with { ObservedAtUtc = asOf.AddMinutes(-10), ExpiresAtUtc = asOf.AddMinutes(5) },
                MarketState = original.Candidate.MarketState with
                {
                    CompletedBarsByTimeframe = original.Candidate.MarketState.CompletedBarsByTimeframe.Add("5m",2),
                    RequiredWarmupBarsByTimeframe = original.Candidate.MarketState.RequiredWarmupBarsByTimeframe.Add("5m",2)
                }
            }
        };
    }
}
