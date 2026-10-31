using TradingFlow.Engine.Risk;
using System.Collections.Immutable;

namespace TradingFlow.Backtesting;

/// <summary>Frozen raw-price intent, produced by the shared strategy brain before execution.</summary>
public sealed record EventExecutionIntent(string CandidateId, string Symbol, int Quantity,
    long KnownAtNanoseconds, long EntryNotBeforeNanoseconds, decimal StopPriceRaw, decimal TargetPriceRaw,
    SimulatedTimeInForce EntryTimeInForce, long EntryRouteExpiresAtNanoseconds);
/// <summary>A daily decision is not actionable before its actual decision clock.</summary>
public sealed record EventExecutionLiquidation(long KnownAtNanoseconds, long ExecuteNotBeforeNanoseconds);
public sealed record EventExecutionReplayResult(string PolicyVersion, string CandidateId,
    ExecutionEventBookState State, IReadOnlyList<TimedSimulatedExecutionEvent> Events,
    IReadOnlyList<string> UnavailableReasons, bool Flat, bool EntrySubmitted);

/// <summary>
/// Additive single-opportunity research coordinator. No OHLC fallback and no bar-gap veto.
/// Portfolio admission/strategy decisions remain outside this executor, in the existing shared brain.
/// </summary>
public static class EventBacktestExecutionReplay
{
    public const string PolicyVersion = "daily-intent-sip-event-replay-v1";

    public static async Task<EventExecutionReplayResult> ReplayAsync(EventExecutionIntent intent,
        IAsyncEnumerable<ExecutionEventBatch> timeline, ExecutionEventSimulationPolicy policy,
        IReadOnlyList<EventExecutionLiquidation> liquidations, long controllerLatencyNanoseconds,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(intent.CandidateId) || String.IsNullOrWhiteSpace(intent.Symbol) ||
            intent.Quantity <= 0 || intent.KnownAtNanoseconds < 0 ||
            intent.EntryNotBeforeNanoseconds < intent.KnownAtNanoseconds || intent.StopPriceRaw <= 0 ||
            intent.EntryRouteExpiresAtNanoseconds <= intent.EntryNotBeforeNanoseconds ||
            intent.TargetPriceRaw <= intent.StopPriceRaw || controllerLatencyNanoseconds < 0 ||
            liquidations.Any(x => x.KnownAtNanoseconds < intent.KnownAtNanoseconds ||
                x.ExecuteNotBeforeNanoseconds < x.KnownAtNanoseconds))
            throw new InvalidDataException("Invalid frozen event intent or liquidation decision clock.");
        var state = ExecutionEventBookState.Empty;
        var simulator = new DeterministicExecutionSimulator();
        var events = new List<TimedSimulatedExecutionEvent>();
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var entrySubmitted = false;
        var protectionSubmitted = false;
        var liquidationSubmitted = false;
        var observationsSeen = 0;
        long sequence = 0;
        var entryId = intent.CandidateId + ":entry";
        var stopId = intent.CandidateId + ":stop";
        var targetId = intent.CandidateId + ":target";
        var exitId = intent.CandidateId + ":liquidation";
        // A coordinator may not silently choose a per-fill broker workflow.
        if (policy.ProtectionActivation != ExecutionProtectionActivation.NativeParentFullyFilled)
            throw new InvalidDataException("This coordinator requires explicit native full-parent protection; custom per-fill needs its own coordinator.");

        await foreach (var batch in timeline.WithCancellation(cancellationToken))
        {
            observationsSeen++;
            if (!String.Equals(batch.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A single-opportunity replay received a different symbol.");
            var commands = new List<ExecutionEventOrderCommand>();
            var clock = batch.EventTimeNanoseconds;
            var ready = checked(intent.EntryNotBeforeNanoseconds + controllerLatencyNanoseconds);
            if (!entrySubmitted && clock >= ready && clock < intent.EntryRouteExpiresAtNanoseconds)
            {
                commands.Add(new(new(ExecutionCommandKind.Submit, entryId, intent.Symbol,
                    SimulatedOrderSide.Buy, SimulatedOrderType.Market, intent.Quantity,
                    ExecutionEventTime.ToUtc(ready), sequence++, false, intent.EntryTimeInForce,
                    FixedFeeGroupId: intent.CandidateId + ":entry-fee"), ready,
                    ExpiresAtNanoseconds: intent.EntryRouteExpiresAtNanoseconds));
                entrySubmitted = true;
            }
            var receipts = state.FillReceipts is { } allReceipts && allReceipts.TryGetValue(entryId, out var priorReceipts)
                ? priorReceipts : ImmutableArray<ExecutionEventFillReceipt>.Empty;
            var knownQuantity = receipts.Where(x => x.KnownAtNanoseconds <= clock).Sum(x => x.Quantity);
            var fullFillKnown = knownQuantity == intent.Quantity;
            var fullReady = fullFillKnown ? checked(receipts.Max(x => x.KnownAtNanoseconds) + controllerLatencyNanoseconds) : Int64.MaxValue;
            var dueExit = liquidations.Where(x => checked(x.ExecuteNotBeforeNanoseconds + controllerLatencyNanoseconds) <= clock)
                .OrderBy(x => x.ExecuteNotBeforeNanoseconds).FirstOrDefault();
            if (dueExit is not null && !liquidationSubmitted)
            {
                var exitKnown = Math.Max(checked(dueExit.ExecuteNotBeforeNanoseconds + controllerLatencyNanoseconds),
                    receipts.Where(x => x.KnownAtNanoseconds <= clock).Select(x => checked(x.KnownAtNanoseconds + controllerLatencyNanoseconds)).DefaultIfEmpty(0).Max());
                exitKnown = Math.Max(exitKnown, events.Where(e => e.Event.IsRiskReducing &&
                    e.Event.LastFillQuantity > 0 && e.KnownAtNanoseconds <= clock)
                    .Select(e => checked(e.KnownAtNanoseconds + controllerLatencyNanoseconds)).DefaultIfEmpty(0).Max());
                if (exitKnown <= clock)
                {
                // Cancellation does not inspect unreported position. The reducing command uses reported fills only.
                foreach (var id in new[] { entryId, stopId, targetId })
                    if (state.Book.WorkingOrders.TryGetValue(id, out var order))
                        commands.Add(new(new(ExecutionCommandKind.Cancel, id + ":cancel", intent.Symbol,
                            order.Command.Side, order.Command.OrderType, 0, ExecutionEventTime.ToUtc(exitKnown),
                            sequence++, true, TargetClientOrderId: id), exitKnown));
                var exitedKnown = events.Where(e => e.Event.IsRiskReducing && e.KnownAtNanoseconds <= clock)
                    .Sum(e => e.Event.LastFillQuantity);
                var reportedOpen = knownQuantity - exitedKnown;
                if (reportedOpen > 0)
                    commands.Add(new(new(ExecutionCommandKind.Submit, exitId, intent.Symbol, SimulatedOrderSide.Sell,
                        SimulatedOrderType.Market, reportedOpen, ExecutionEventTime.ToUtc(exitKnown), sequence++, true,
                        SimulatedTimeInForce.GoodTillCanceled, FixedFeeGroupId: intent.CandidateId + ":exit-fee"),
                        exitKnown, entryId));
                liquidationSubmitted = reportedOpen > 0 || fullFillKnown;
                }
            }
            else if (!liquidationSubmitted && !protectionSubmitted && fullFillKnown && fullReady <= clock)
            {
                foreach (var (id, type, price) in new[] { (stopId, SimulatedOrderType.Stop, intent.StopPriceRaw),
                             (targetId, SimulatedOrderType.Limit, intent.TargetPriceRaw) })
                    commands.Add(new(new(ExecutionCommandKind.Submit, id, intent.Symbol, SimulatedOrderSide.Sell,
                        type, intent.Quantity, ExecutionEventTime.ToUtc(fullReady), sequence++, true,
                        SimulatedTimeInForce.GoodTillCanceled,
                        LimitPrice: type == SimulatedOrderType.Limit ? price : null,
                        StopPrice: type == SimulatedOrderType.Stop ? price : null,
                        FixedFeeGroupId: intent.CandidateId + ":exit-fee", OcoGroupId: intent.CandidateId),
                        fullReady, entryId, IsProtection: true));
                protectionSubmitted = true;
            }
            var result = simulator.AdvanceEvent(new(state, batch, commands, policy));
            state = result.State;
            events.AddRange(result.Events);
            foreach (var reason in result.UnavailableReasons) unavailable.Add(reason);
            foreach (var rejection in result.Events.Where(e => e.Event.State == "rejected"))
                unavailable.Add("broker_route_rejected:" + rejection.Event.Reason);
            if (result.UnavailableReasons.Count > 0) break; // A missing observation is never a no-touch conclusion.
        }
        var actualOpen = state.Book.Positions.GetValueOrDefault(intent.Symbol)?.SignedQuantity ?? 0;
        if (observationsSeen == 0) unavailable.Add("event_evidence_empty");
        if (!entrySubmitted) unavailable.Add("event_evidence_ends_before_entry_ready");
        if (state.Book.WorkingOrders.Count > 0) unavailable.Add("working_orders_unresolved_at_end_of_event_evidence");
        if (entrySubmitted && !events.Any(e => e.Event.ClientOrderId == entryId &&
            (e.Event.LastFillQuantity > 0 || e.Event.State == "canceled")))
            unavailable.Add("entry_has_no_observed_fill_or_nonfill_disposition");
        if (actualOpen != 0) unavailable.Add("position_open_at_end_of_event_evidence");
        var entryFilled = state.FilledQuantities.GetValueOrDefault(entryId);
        if (entryFilled is > 0 && entryFilled < intent.Quantity && !protectionSubmitted)
            unavailable.Add("native_parent_partial_fill_has_no_active_children_no_repair_assumed");
        return new(PolicyVersion, intent.CandidateId, state, events, unavailable.Order().ToArray(), actualOpen == 0, entrySubmitted);
    }
}
