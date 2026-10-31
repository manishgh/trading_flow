using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Engine.Risk;

public sealed partial class DeterministicExecutionSimulator
{
    /// <summary>
    /// A distinct event policy, using observed trades and strictly prior known NBBO.
    /// This never constructs an OHLC bar or reprocesses an event after a fill.
    /// </summary>
    public ExecutionEventStepResult AdvanceEvent(ExecutionEventStepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEventPolicy(request.Policy);
        var batch = request.Market;
        var prior = request.PriorState;
        var symbol = NormalizeSymbol(batch.Symbol);
        if (String.IsNullOrWhiteSpace(batch.Symbol) || batch.EventTimeNanoseconds < 0 ||
            batch.KnownAtNanoseconds < batch.EventTimeNanoseconds || batch.Sequence < 0 ||
            batch.Quotes is null || batch.Trades is null)
            throw new InvalidDataException("Invalid execution event batch clock or symbol.");
        var policyFingerprint = EventHash(request.Policy);
        if (prior.PolicyFingerprint is { } frozenPolicy && frozenPolicy != policyFingerprint)
            throw new InvalidOperationException("An event book cannot change its frozen policy or evidence version.");
        var fingerprint = EventHash(batch);
        if (prior.LastSequences.TryGetValue(symbol, out var lastSequence))
        {
            if (batch.Sequence == lastSequence)
            {
                if (request.Commands.Count != 0 || prior.LastFingerprints[symbol] != fingerprint)
                    throw new InvalidDataException("A replayed event cannot change evidence or attach commands.");
                return new(prior, [], []);
            }
            if (batch.Sequence < lastSequence || batch.EventTimeNanoseconds <= prior.LastEventTimes[symbol] ||
                batch.KnownAtNanoseconds < prior.LastKnownTimes[symbol])
                throw new InvalidDataException("Execution events must advance; equal nanoseconds belong in one batch.");
        }
        if (!batch.CoverageComplete)
            return new(prior, [], ["execution_interval_coverage_unavailable"]);
        if (batch.Quotes.Any(q => String.IsNullOrWhiteSpace(q.EventId) ||
                q.EventTimeNanoseconds != batch.EventTimeNanoseconds) ||
            batch.Trades.Any(t => String.IsNullOrWhiteSpace(t.EventId) ||
                t.EventTimeNanoseconds != batch.EventTimeNanoseconds || t.Price <= 0 || t.SizeShares <= 0) ||
            batch.Trades.Select(t => t.EventId).Distinct(StringComparer.Ordinal).Count() != batch.Trades.Count)
            return new(prior, [], ["invalid_execution_event_identity_or_trade"]);
        if (request.Commands.Any(c => c.KnownAtNanoseconds < 0 || c.KnownAtNanoseconds > batch.KnownAtNanoseconds ||
                c.Command.KnownAtUtc != ExecutionEventTime.ToUtc(c.KnownAtNanoseconds) ||
                NormalizeSymbol(c.Command.Symbol) != symbol || c.ExpiresAtNanoseconds is { } expiry &&
                (expiry <= c.KnownAtNanoseconds || c.Command.Kind is not (ExecutionCommandKind.Submit or ExecutionCommandKind.Replace))))
            throw new InvalidDataException("Event commands need matching UTC/nanosecond clocks and the batch symbol.");

        var working = prior.Book.WorkingOrders.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var positions = prior.Book.Positions.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var contracts = prior.Book.KnownOrderContracts.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var feeGroups = new HashSet<string>(prior.Book.ChargedFixedFeeGroupIds, StringComparer.Ordinal);
        var quotes = prior.Quotes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var activations = prior.OrderActivationTimes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var expiries = (prior.OrderExpiries ?? FrozenDictionary<string, long>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var metadata = (prior.OrderMetadataFingerprints ?? FrozenDictionary<string, string>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var nativeChildren = (prior.NativeChildCommitments ?? FrozenDictionary<string, ExecutionNativeChildCommitment>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var filledQuantities = prior.FilledQuantities.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var fillKnown = prior.FillKnownTimes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var receipts = (prior.FillReceipts ?? FrozenDictionary<string, ImmutableArray<ExecutionEventFillReceipt>>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var entryParents = (prior.OrderEntryParents ?? FrozenDictionary<string, string>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var parentEntries = (prior.ParentEntryReceipts ?? FrozenDictionary<string, ImmutableArray<ExecutionEventFillReceipt>>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var parentReductions = (prior.ParentReductionReceipts ?? FrozenDictionary<string, ImmutableArray<ExecutionEventFillReceipt>>.Empty)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var emitted = new List<TimedSimulatedExecutionEvent>();
        var unavailable = new List<string>();
        var commands = request.Commands.OrderBy(c => c.KnownAtNanoseconds)
            .ThenBy(c => c.Command.Sequence).ThenBy(c => c.Command.ClientOrderId, StringComparer.Ordinal).ToArray();
        if ((long)contracts.Count + commands.Where(c => c.Command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace)
                .Select(c => c.Command.ClientOrderId).Distinct(StringComparer.Ordinal).Count(id => !contracts.ContainsKey(id))
            > request.Policy.Costs.MaximumTrackedOrderContracts)
            throw new InvalidOperationException("Event order contracts exceed the frozen history limit.");

        // Bind each instruction to its immutable parent, receipt and activation contracts before routing it.
        void ApplyEventCommand(ExecutionEventOrderCommand envelope)
        {
            var command = envelope.Command;
            var effectiveExpiry = envelope.ExpiresAtNanoseconds;
            var replacementTarget = command.Kind == ExecutionCommandKind.Replace ? command.TargetClientOrderId : null;
            if (envelope.NativeParentCommitted && (command.Kind is not (ExecutionCommandKind.Submit or ExecutionCommandKind.Replace) ||
                !envelope.IsProtection || envelope.IsRepair || !command.IsRiskReducing ||
                request.Policy.ProtectionActivation != ExecutionProtectionActivation.NativeParentFullyFilled ||
                command.OrderType is not (SimulatedOrderType.Stop or SimulatedOrderType.StopLimit or SimulatedOrderType.Limit)))
                throw new InvalidDataException("Precommitted native children require native protection submit/replace, not repair or market reduction.");
            if (command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace)
            {
                var commandFingerprint = EventHash(envelope);
                if (metadata.TryGetValue(command.ClientOrderId, out var original))
                {
                    if (original != commandFingerprint)
                        throw new InvalidDataException("An event order ID changed its nanosecond or parent contract.");
                    if (contracts.ContainsKey(command.ClientOrderId)) return;
                }
                if (command.Kind == ExecutionCommandKind.Replace && command.TargetClientOrderId is { } expiryTarget &&
                    working.ContainsKey(expiryTarget) && expiries.TryGetValue(expiryTarget, out var targetExpiry))
                {
                    if (effectiveExpiry is { } replacementExpiry && replacementExpiry != targetExpiry)
                        throw new InvalidDataException("A replacement cannot change its route's bound expiry.");
                    effectiveExpiry = targetExpiry;
                }
                var protectionType = command.IsRiskReducing &&
                    command.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit or SimulatedOrderType.Limit;
                if (protectionType && !envelope.IsProtection)
                    throw new InvalidDataException("Protective orders require an explicit parent/workflow contract.");
                if (envelope.IsProtection && !command.IsRiskReducing)
                    throw new InvalidDataException("Protection must reduce its entry parent's exposure.");
                if (!String.IsNullOrWhiteSpace(command.OcoGroupId))
                {
                    var parentId = command.IsRiskReducing ? envelope.ParentOrderId ?? "" : replacementTarget ?? command.ClientOrderId;
                    var proposedParent = entryParents.GetValueOrDefault(parentId, parentId);
                    if (nativeChildren.Any(pair => working.ContainsKey(pair.Key) &&
                        contracts[pair.Key].OcoGroupId == command.OcoGroupId && pair.Value.EntryParentId != proposedParent))
                        throw new InvalidDataException("An OCO group bound to native children cannot cancel another entry parent's protection.");
                }
                if (replacementTarget is not null && nativeChildren.ContainsKey(replacementTarget) && !envelope.NativeParentCommitted)
                    throw new InvalidDataException("A replacement cannot discard its precommitted native workflow.");
                if (!command.IsRiskReducing && replacementTarget is not null &&
                    contracts.TryGetValue(replacementTarget, out var replacedParent))
                {
                    var parentRoot = entryParents.GetValueOrDefault(replacementTarget, replacementTarget);
                    if (nativeChildren.Values.Any(child => child.EntryParentId == parentRoot) && command.Quantity != replacedParent.Quantity)
                        throw new InvalidDataException("An entry replacement cannot change quantity bound to precommitted native children.");
                }
                if (envelope.NativeParentCommitted)
                {
                    ValidateCommand(command);
                    if (envelope.ParentOrderId is not { } parent ||
                        !contracts.TryGetValue(parent, out var parentContract) || parentContract.IsRiskReducing ||
                        !working.ContainsKey(parent) || NormalizeSymbol(parentContract.Symbol) != symbol ||
                        parentContract.Side == command.Side || command.Quantity != parentContract.Quantity ||
                        activations.GetValueOrDefault(parent, Int64.MaxValue) > envelope.KnownAtNanoseconds ||
                        String.IsNullOrWhiteSpace(command.OcoGroupId) || parentContract.OcoGroupId == command.OcoGroupId ||
                        (command.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit) && command.StopPrice is not > 0 ||
                        (command.OrderType is SimulatedOrderType.Limit or SimulatedOrderType.StopLimit) && command.LimitPrice is not > 0)
                        throw new InvalidDataException("A native child requires its live earlier-committed same-symbol entry parent, opposite side, exact quantity and OCO group.");
                    var entryParent = entryParents.GetValueOrDefault(parent, parent);
                    if (parentEntries.GetValueOrDefault(entryParent, []).Sum(receipt => receipt.Quantity) != 0)
                        throw new InvalidOperationException("Native child terms must be committed before the first physical parent fill.");
                    if (nativeChildren.Any(pair => working.ContainsKey(pair.Key) && pair.Key != replacementTarget &&
                        (pair.Value.EntryParentId == entryParent && contracts[pair.Key].OcoGroupId != command.OcoGroupId ||
                         pair.Value.EntryParentId != entryParent && contracts[pair.Key].OcoGroupId == command.OcoGroupId)))
                        throw new InvalidDataException("Native sibling OCO groups must be bound exclusively to one entry parent.");
                    if (working.Values.Any(order => order.Command.ClientOrderId != replacementTarget &&
                        order.Command.OcoGroupId == command.OcoGroupId &&
                        entryParents.GetValueOrDefault(order.Command.ClientOrderId, order.Command.ClientOrderId) != entryParent))
                        throw new InvalidDataException("A native OCO group cannot include another parent's existing order.");
                    if (replacementTarget is not null)
                    {
                        if (!nativeChildren.TryGetValue(replacementTarget, out var previousChild) ||
                            previousChild.EntryParentId != entryParent || previousChild.ParentContractId != parent ||
                            previousChild.ParentQuantity != command.Quantity)
                            throw new InvalidDataException("A native child replacement must preserve its committed parent and quantity.");
                        if (!working.TryGetValue(replacementTarget, out var target)) return;
                        if (command.Side != target.Command.Side || command.OrderType != target.Command.OrderType ||
                            command.OcoGroupId != target.Command.OcoGroupId || target.FilledQuantity != 0)
                            throw new InvalidDataException("A dormant native child replacement must preserve side, type and OCO group.");
                        working.Remove(replacementTarget);
                        emitted.Add(new(CreateStateEvent(target, "canceled", positions, command.KnownAtUtc, "replaced"),
                            envelope.KnownAtNanoseconds, envelope.KnownAtNanoseconds));
                    }
                    // These are broker-held dormant instructions. Registering them must not
                    // fabricate a position to bypass shared client reduction validation.
                    contracts[command.ClientOrderId] = command;
                    metadata[command.ClientOrderId] = commandFingerprint;
                    working[command.ClientOrderId] = new SimulatedWorkingOrder(command, 0, 0m, false, 0m, false);
                    entryParents[command.ClientOrderId] = entryParent;
                    nativeChildren[command.ClientOrderId] = new(entryParent, parent, parentContract.Quantity, envelope.KnownAtNanoseconds);
                    activations[command.ClientOrderId] = Int64.MaxValue;
                    if (effectiveExpiry is { } nativeExpiry) expiries[command.ClientOrderId] = nativeExpiry;
                    return;
                }
                // Scheduled market exits and repairs require the same receipt causality as protection.
                if (command.IsRiskReducing)
                {
                    if (envelope.ParentOrderId is not { } parent ||
                        !contracts.TryGetValue(parent, out var parentContract) || parentContract.IsRiskReducing ||
                        NormalizeSymbol(parentContract.Symbol) != symbol || parentContract.Side == command.Side)
                        throw new InvalidDataException("A reduction requires its same-symbol, opposite-side entry parent.");
                    var entryParent = entryParents.GetValueOrDefault(parent, parent);
                    var knownQuantity = parentEntries.GetValueOrDefault(entryParent, [])
                        .Where(r => r.KnownAtNanoseconds <= envelope.KnownAtNanoseconds).Sum(r => r.Quantity);
                    var reducedKnown = parentReductions.GetValueOrDefault(entryParent, [])
                        .Where(r => r.KnownAtNanoseconds <= envelope.KnownAtNanoseconds).Sum(r => r.Quantity);
                    if (knownQuantity - reducedKnown <= 0)
                        throw new InvalidOperationException("A reduction cannot precede a known parent fill.");
                    if (envelope.IsProtection &&
                        request.Policy.ProtectionActivation == ExecutionProtectionActivation.NativeParentFullyFilled &&
                        !envelope.IsRepair && knownQuantity < parentContract.Quantity)
                        throw new InvalidOperationException("Native bracket children require the fully filled parent.");
                    var priorFilled = command.Kind == ExecutionCommandKind.Replace && command.TargetClientOrderId is { } target &&
                        working.TryGetValue(target, out var replaced) ? replaced.FilledQuantity : 0;
                    if (command.Kind == ExecutionCommandKind.Replace && command.TargetClientOrderId is { } priorId &&
                        entryParents.TryGetValue(priorId, out var oldParent) && oldParent != entryParent)
                        throw new InvalidDataException("A replacement cannot change its exposure parent.");
                    if (command.Quantity - priorFilled > knownQuantity - reducedKnown)
                        throw new InvalidOperationException("A reduction exceeds the parent's known filled quantity.");
                    var physicalOwned = parentEntries.GetValueOrDefault(entryParent, []).Sum(r => r.Quantity) -
                        parentReductions.GetValueOrDefault(entryParent, []).Sum(r => r.Quantity);
                    var physicalPosition = positions.GetValueOrDefault(symbol)?.SignedQuantity ?? 0;
                    var reduciblePosition = command.Side == SimulatedOrderSide.Sell
                        ? Math.Max(physicalPosition, 0) : Math.Max(-physicalPosition, 0);
                    if (command.Quantity - priorFilled > Math.Min(physicalOwned, reduciblePosition))
                    {
                        // The controller used valid reported exposure; the broker may already have reduced it.
                        // Reject the route without resizing the controller's instruction from unreported fills.
                        metadata[command.ClientOrderId] = commandFingerprint;
                        contracts[command.ClientOrderId] = command;
                        entryParents[command.ClientOrderId] = entryParent;
                        emitted.Add(new(new SimulatedExecutionEvent(command.ClientOrderId, symbol,
                            "rejected", 0, 0, command.Quantity, null, 0m, 0m, 0m, 0m,
                            physicalPosition, positions.GetValueOrDefault(symbol)?.RealizedProfit ?? 0m,
                            ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds),
                            "physical_parent_exposure_changed_before_route", command.Side, true),
                            batch.EventTimeNanoseconds,
                            checked(batch.KnownAtNanoseconds + request.Policy.FillReportLatencyNanoseconds)));
                        return;
                    }
                }
                metadata[command.ClientOrderId] = commandFingerprint;
            }
            var cancellationTarget = command.Kind == ExecutionCommandKind.Cancel ? command.TargetClientOrderId : null;
            var canceledParent = cancellationTarget is not null && working.TryGetValue(cancellationTarget, out var canceledOrder) &&
                !canceledOrder.Command.IsRiskReducing ? canceledOrder : null;
            var isNewContract = !contracts.ContainsKey(command.ClientOrderId);
            var changes = new List<SimulatedExecutionEvent>();
            ApplyCommand(command, working, contracts, positions, changes);
            if ((command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace) &&
                contracts.ContainsKey(command.ClientOrderId))
            {
                var parentId = command.IsRiskReducing ? envelope.ParentOrderId! :
                    replacementTarget ?? command.ClientOrderId;
                entryParents[command.ClientOrderId] = entryParents.GetValueOrDefault(parentId, parentId);
            }
            if (isNewContract && replacementTarget is not null && working.ContainsKey(command.ClientOrderId))
            {
                if (receipts.TryGetValue(replacementTarget, out var priorReceipts))
                    receipts[command.ClientOrderId] = priorReceipts;
                if (filledQuantities.TryGetValue(replacementTarget, out var priorQuantity))
                    filledQuantities[command.ClientOrderId] = priorQuantity;
                if (fillKnown.TryGetValue(replacementTarget, out var priorKnown))
                    fillKnown[command.ClientOrderId] = priorKnown;
            }
            if ((command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace) &&
                !activations.ContainsKey(command.ClientOrderId))
                activations[command.ClientOrderId] = envelope.KnownAtNanoseconds;
            if ((command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace) &&
                working.ContainsKey(command.ClientOrderId) && effectiveExpiry is { } expires)
                expiries[command.ClientOrderId] = expires;
            emitted.AddRange(changes.Select(e => new TimedSimulatedExecutionEvent(e,
                envelope.KnownAtNanoseconds, envelope.KnownAtNanoseconds)));
            if (canceledParent is not null && !working.ContainsKey(canceledParent.Command.ClientOrderId))
                CancelDormantNativeChildren(canceledParent.Command.ClientOrderId, envelope.KnownAtNanoseconds,
                    envelope.KnownAtNanoseconds, "canceled");
        }

        // A terminated parent cancels dormant children; a partial parent remains explicitly unprotected.
        void CancelDormantNativeChildren(string parentId, long occurredAt, long knownAt, string reason)
        {
            var root = entryParents.GetValueOrDefault(parentId, parentId);
            var dormant = nativeChildren.Where(pair => pair.Value.EntryParentId == root && working.ContainsKey(pair.Key) &&
                activations.GetValueOrDefault(pair.Key, Int64.MaxValue) == Int64.MaxValue).ToArray();
            foreach (var child in dormant)
            {
                var order = working[child.Key];
                working.Remove(child.Key);
                emitted.Add(new(CreateStateEvent(order, "canceled", positions, ExecutionEventTime.ToUtc(occurredAt),
                    "native_parent_terminated_before_full_fill"), occurredAt, knownAt));
            }
            var commitments = nativeChildren.Values.Where(child => child.EntryParentId == root).ToArray();
            var physicalFilled = parentEntries.GetValueOrDefault(root, []).Sum(receipt => receipt.Quantity);
            if (commitments.Length > 0 && physicalFilled > 0 && physicalFilled < commitments[0].ParentQuantity)
                unavailable.Add("native_parent_" + reason + "_partially_filled_unprotected");
        }

        // Apply precommitted exclusive deadlines without requiring a fill or inventing market prices.
        void ExpireWorkingOrders(long throughNanoseconds)
        {
            // This is a broker timer fixed in the original route contract, not a
            // controller cancellation learned from a later print. Expiry is exclusive.
            foreach (var order in working.Values.Where(o => NormalizeSymbol(o.Command.Symbol) == symbol &&
                expiries.TryGetValue(o.Command.ClientOrderId, out var expiry) && expiry <= throughNanoseconds)
                .OrderBy(o => expiries[o.Command.ClientOrderId])
                .ThenBy(o => o.Command.ClientOrderId, StringComparer.Ordinal).ToArray())
            {
                if (!working.ContainsKey(order.Command.ClientOrderId)) continue;
                var expires = expiries[order.Command.ClientOrderId];
                working.Remove(order.Command.ClientOrderId);
                var cancellation = CreateStateEvent(order, "canceled", positions,
                    ExecutionEventTime.ToUtc(expires), "route_expired");
                emitted.Add(new(cancellation, expires,
                    checked(expires + request.Policy.FillReportLatencyNanoseconds)));
                if (!order.Command.IsRiskReducing)
                    CancelDormantNativeChildren(order.Command.ClientOrderId, expires,
                        checked(expires + request.Policy.FillReportLatencyNanoseconds), "expired");
            }
        }

        // Commands at this exact event time are committed afterward, never matched retrospectively.
        foreach (var command in commands.Where(c => c.KnownAtNanoseconds < batch.EventTimeNanoseconds))
        {
            // A replacement issued after expiry cannot revive its expired target.
            ExpireWorkingOrders(command.KnownAtNanoseconds);
            ApplyEventCommand(command);
        }
        ExpireWorkingOrders(batch.EventTimeNanoseconds);

        quotes.TryGetValue(symbol, out var quoteState);
        var active = working.Values.Where(o => NormalizeSymbol(o.Command.Symbol) == symbol &&
                activations.GetValueOrDefault(o.Command.ClientOrderId, Int64.MaxValue) < batch.EventTimeNanoseconds)
            .OrderByDescending(o => o.Command.IsRiskReducing)
            .ThenBy(o => ResolveRiskReducingPriority(o, positions))
            .ThenBy(o => o.Command.KnownAtUtc).ThenBy(o => o.Command.Sequence)
            .ThenBy(o => o.Command.ClientOrderId, StringComparer.Ordinal).ToArray();
        var eligibleTrades = batch.Trades.Where(t => t.IsEligible).ToArray();
        var ambiguous = batch.Quotes.Count > 0 && batch.Trades.Count > 0 ||
            eligibleTrades.Select(t => t.Price).Distinct().Count() > 1;
        if (active.Length > 0 && ambiguous)
            unavailable.Add("ambiguous_same_nanosecond_market_order");
        var freshQuote = quoteState is not null && quoteState.KnownAtNanoseconds <= batch.EventTimeNanoseconds &&
            quoteState.Quote.EventTimeNanoseconds < batch.EventTimeNanoseconds &&
            batch.EventTimeNanoseconds - quoteState.Quote.EventTimeNanoseconds <= request.Policy.MaximumQuoteAgeNanoseconds;
        if (active.Length > 0 && eligibleTrades.Length > 0 && !freshQuote)
            unavailable.Add("prior_fresh_nbbo_unavailable");
        if (!ambiguous && freshQuote && eligibleTrades.Length > 0)
        {
            var availableQuote = quoteState!;
            var quote = availableQuote.Quote;
            // Trade condition eligibility is externally versioned. There is no implicit odd-lot veto.
            var inside = eligibleTrades.Where(t => t.Price >= quote.Bid && t.Price <= quote.Ask).ToArray();
            var volume = inside.Sum(t => (decimal)t.SizeShares);
            var capacity = CalculateFillableQuantity(Int32.MaxValue, volume,
                request.Policy.Costs.MaxParticipationPct, missingVolumeFailsClosed: true);
            foreach (var originalOrder in active)
            {
                if (!working.TryGetValue(originalOrder.Command.ClientOrderId, out var order)) continue;
                var command = order.Command;
                if (!command.IsRiskReducing && request.Policy.MaximumEntrySpreadBps is { } maximumSpread &&
                    (quote.Ask - quote.Bid) / ((quote.Ask + quote.Bid) / 2m) * 10_000m > maximumSpread)
                    continue;
                var reducingPosition = positions.GetValueOrDefault(symbol)?.SignedQuantity ?? 0;
                if (command.IsRiskReducing &&
                    !(reducingPosition > 0 && command.Side == SimulatedOrderSide.Sell ||
                      reducingPosition < 0 && command.Side == SimulatedOrderSide.Buy))
                {
                    working.Remove(command.ClientOrderId);
                    var canceled = CreateStateEvent(order, "canceled", positions, ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds),
                        "position_quantity_unavailable");
                    emitted.Add(new(canceled, batch.EventTimeNanoseconds, batch.KnownAtNanoseconds));
                    continue;
                }
                if (inside.Length == 0) continue;
                if (!order.StopTriggered && (command.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit))
                {
                    var crossed = command.Side == SimulatedOrderSide.Sell
                        ? inside.Any(t => t.Price <= command.StopPrice) : inside.Any(t => t.Price >= command.StopPrice);
                    if (crossed)
                    {
                        var triggered = order with { StopTriggered = true };
                        working[command.ClientOrderId] = triggered;
                        activations[command.ClientOrderId] = batch.KnownAtNanoseconds;
                        var activation = CreateStateEvent(triggered, "triggered", positions,
                            ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds), "eligible_trade_stop_activated");
                        emitted.Add(new(activation, batch.EventTimeNanoseconds, batch.KnownAtNanoseconds));
                    }
                    continue; // Election creates a route instruction; the producing trade cannot fill it.
                }
                var reference = command.Side == SimulatedOrderSide.Buy ? quote.Ask : quote.Bid;
                var isLimit = command.OrderType is SimulatedOrderType.Limit or SimulatedOrderType.StopLimit;
                var canMatch = !isLimit || (command.Side == SimulatedOrderSide.Buy
                    ? reference <= command.LimitPrice : reference >= command.LimitPrice);
                var displayed = command.Side == SimulatedOrderSide.Buy ? availableQuote.RemainingAskShares : availableQuote.RemainingBidShares;
                var quantity = (int)Math.Min(Math.Min(order.RemainingQuantity, capacity), Math.Min(displayed, Int32.MaxValue));
                var entryParent = entryParents.GetValueOrDefault(command.ClientOrderId, command.ClientOrderId);
                var ownedRemaining = parentEntries.GetValueOrDefault(entryParent, []).Sum(r => r.Quantity) -
                    parentReductions.GetValueOrDefault(entryParent, []).Sum(r => r.Quantity);
                if (command.IsRiskReducing)
                    quantity = Math.Min(quantity, Math.Min(Math.Abs(reducingPosition), ownedRemaining));
                if (!canMatch || quantity <= 0)
                {
                    if (command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                        EventCancel(order, "immediate_or_cancel_unfilled");
                    continue;
                }
                var costs = request.Policy.Costs;
                var impact = CalculateImpactBps(quantity, volume, costs.MaxParticipationPct, costs.ImpactBpsAtMaxParticipation);
                var slippage = costs.BaseSlippageBps + impact; // Real side already includes spread.
                var price = Decimal.Round(ApplyAdversePrice(reference, command.Side, slippage), 6);
                if (price <= 0 || isLimit && (command.Side == SimulatedOrderSide.Buy
                        ? price > command.LimitPrice : price < command.LimitPrice))
                {
                    if (command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                        EventCancel(order, "limit_execution_friction_not_executable");
                    continue;
                }
                var remainingTaf = costs.FinraTafCap > 0 ? Math.Max(costs.FinraTafCap - order.FinraTafCharged, 0) : 0;
                var tafRate = costs.FinraTafCap > 0 && remainingTaf == 0 ? 0 : costs.FinraTafPerShare;
                var fixedFee = order.FixedFeeCharged || feeGroups.Contains(ResolveFixedFeeGroupId(command)) ? 0 :
                    command.Side == SimulatedOrderSide.Buy ? costs.FixedBuyFee : costs.FixedSellFee;
                var fees = Decimal.Round(fixedFee + CalculateRegulatoryFees(
                    command.Side == SimulatedOrderSide.Sell ? quantity * price : 0,
                    command.Side == SimulatedOrderSide.Sell ? quantity : 0, costs.SecFeeRate, tafRate, remainingTaf), 6);
                var updated = order with { FilledQuantity = order.FilledQuantity + quantity,
                    FilledNotional = order.FilledNotional + quantity * price, FixedFeeCharged = true,
                    FinraTafCharged = order.FinraTafCharged + CalculateFinraTafFee(quantity,
                        command.Side == SimulatedOrderSide.Sell ? tafRate : 0, remainingTaf) };
                var position = ApplyFill(positions, symbol, command.Side, quantity, price);
                capacity -= quantity;
                availableQuote = command.Side == SimulatedOrderSide.Buy
                    ? availableQuote with { RemainingAskShares = availableQuote.RemainingAskShares - quantity }
                    : availableQuote with { RemainingBidShares = availableQuote.RemainingBidShares - quantity };
                quotes[symbol] = availableQuote;
                feeGroups.Add(ResolveFixedFeeGroupId(command));
                var known = checked(batch.KnownAtNanoseconds + request.Policy.FillReportLatencyNanoseconds);
                filledQuantities[command.ClientOrderId] = updated.FilledQuantity;
                fillKnown[command.ClientOrderId] = known;
                receipts[command.ClientOrderId] = receipts.GetValueOrDefault(command.ClientOrderId, [])
                    .Add(new(quantity, known));
                var parentReceipts = command.IsRiskReducing ? parentReductions : parentEntries;
                parentReceipts[entryParent] = parentReceipts.GetValueOrDefault(entryParent, [])
                    .Add(new(quantity, known));
                if (updated.RemainingQuantity == 0 || command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                    working.Remove(command.ClientOrderId);
                else working[command.ClientOrderId] = updated;
                var spread = (quote.Ask - quote.Bid) / ((quote.Ask + quote.Bid) / 2m) * BasisPoints;
                emitted.Add(new(new SimulatedExecutionEvent(command.ClientOrderId, symbol,
                    updated.RemainingQuantity == 0 ? "filled" : "partially_filled", quantity, updated.FilledQuantity,
                    updated.RemainingQuantity, price, fees, Decimal.Round(spread, 6), Decimal.Round(slippage, 6),
                    Decimal.Round(impact, 6), position.SignedQuantity, position.RealizedProfit,
                    ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds), "trade_gated_prior_nbbo_fill",
                    command.Side, command.IsRiskReducing), batch.EventTimeNanoseconds, known));
                if (!command.IsRiskReducing)
                {
                    var physicalParentFilled = parentEntries.GetValueOrDefault(entryParent, []).Sum(receipt => receipt.Quantity);
                    foreach (var child in nativeChildren.Where(pair => pair.Value.EntryParentId == entryParent &&
                        pair.Value.ParentQuantity == physicalParentFilled && working.ContainsKey(pair.Key) &&
                        activations.GetValueOrDefault(pair.Key, Int64.MaxValue) == Int64.MaxValue).ToArray())
                    {
                        activations[child.Key] = batch.EventTimeNanoseconds;
                        emitted.Add(new(CreateStateEvent(working[child.Key], "activated", positions,
                            ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds), "native_parent_physically_fully_filled"),
                            batch.EventTimeNanoseconds, known));
                    }
                }
                if (updated.RemainingQuantity == 0)
                {
                    var cancellations = new List<SimulatedExecutionEvent>();
                    CancelOcoSiblings(updated, working, positions, cancellations, ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds));
                    emitted.AddRange(cancellations.Select(e => new TimedSimulatedExecutionEvent(e, batch.EventTimeNanoseconds, known)));
                }
                if (command.IsRiskReducing && quantity == ownedRemaining)
                {
                    foreach (var sibling in working.Values.Where(o => o.Command.IsRiskReducing &&
                        entryParents.GetValueOrDefault(o.Command.ClientOrderId) == entryParent).ToArray())
                        EventCancel(sibling, "parent_exposure_closed", known);
                }
                if (updated.RemainingQuantity > 0 && command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                    EventCancel(updated, "immediate_or_cancel_remainder");

                void EventCancel(SimulatedWorkingOrder value, string reason, long? knownAt = null)
                {
                    working.Remove(value.Command.ClientOrderId);
                    var cancellation = CreateStateEvent(value, "canceled", positions,
                        ExecutionEventTime.ToUtc(batch.EventTimeNanoseconds), reason);
                    emitted.Add(new(cancellation, batch.EventTimeNanoseconds, knownAt ?? batch.KnownAtNanoseconds));
                    if (!value.Command.IsRiskReducing)
                        CancelDormantNativeChildren(value.Command.ClientOrderId, batch.EventTimeNanoseconds,
                            knownAt ?? batch.KnownAtNanoseconds, "terminated");
                }
            }
            quoteState = availableQuote;
        }
        foreach (var command in commands.Where(c => c.KnownAtNanoseconds >= batch.EventTimeNanoseconds))
            ApplyEventCommand(command);

        // An equal-time quote is never retroactively attached to any trade in this batch.
        if (batch.Quotes.Count > 0)
        {
            if (ExecutionEventQuotes.UniqueEligible(batch.Quotes) is { } next)
            {
                if (quoteState?.Quote.EventId == next.EventId)
                    return new(prior, [], ["reused_quote_event_identity"]);
                quotes[symbol] = new(next, next.BidSizeShares, next.AskSizeShares, batch.KnownAtNanoseconds);
            }
            else quotes.Remove(symbol);
        }
        var eventTimes = prior.LastEventTimes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var knownTimes = prior.LastKnownTimes.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var sequences = prior.LastSequences.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var fingerprints = prior.LastFingerprints.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        eventTimes[symbol] = batch.EventTimeNanoseconds; knownTimes[symbol] = batch.KnownAtNanoseconds;
        sequences[symbol] = batch.Sequence; fingerprints[symbol] = fingerprint;
        var book = new ExecutionBookState(working.ToFrozenDictionary(StringComparer.Ordinal),
            positions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), contracts.ToFrozenDictionary(StringComparer.Ordinal),
            prior.Book.LastProcessedSliceSequence, prior.Book.AccumulatedFees + emitted.Sum(e => e.Event.Fees),
            feeGroups.ToFrozenSet(StringComparer.Ordinal));
        return new(new(book, quotes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            eventTimes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), knownTimes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            sequences.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), fingerprints.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            activations.ToFrozenDictionary(StringComparer.Ordinal), filledQuantities.ToFrozenDictionary(StringComparer.Ordinal),
            fillKnown.ToFrozenDictionary(StringComparer.Ordinal), policyFingerprint, metadata.ToFrozenDictionary(StringComparer.Ordinal),
            receipts.ToFrozenDictionary(StringComparer.Ordinal), entryParents.ToFrozenDictionary(StringComparer.Ordinal),
            parentEntries.ToFrozenDictionary(StringComparer.Ordinal), parentReductions.ToFrozenDictionary(StringComparer.Ordinal),
            expiries.ToFrozenDictionary(StringComparer.Ordinal), nativeChildren.ToFrozenDictionary(StringComparer.Ordinal)),
            emitted.ToImmutableArray(), unavailable.ToImmutableArray());
    }

    // Bind replay inputs and immutable policy contracts to their serialized bytes.
    private static string EventHash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    // Refuse implicit workflows or unproved execution units before matching any event.
    private static void ValidateEventPolicy(ExecutionEventSimulationPolicy policy)
    {
        if (policy.ProtectionActivation is not (ExecutionProtectionActivation.NativeParentFullyFilled or
                ExecutionProtectionActivation.CustomPerFill) || policy.MaximumQuoteAgeNanoseconds <= 0 ||
            policy.FillReportLatencyNanoseconds < 0 || policy.MaximumEntrySpreadBps is <= 0 || !policy.RawPriceAndQuantityBasisVerified ||
            policy.PriceFactorToRaw != 1 || policy.QuantityFactorToRaw != 1 || String.IsNullOrWhiteSpace(policy.EvidenceVersion))
            throw new InvalidDataException("Event replay requires a declared protection workflow, fresh-quote clock and verified raw units.");
        var costs = policy.Costs;
        if (costs.MaxParticipationPct is < 0 or > 100 || costs.BaseSlippageBps < 0 || costs.ImpactBpsAtMaxParticipation < 0 ||
            costs.FixedBuyFee < 0 || costs.FixedSellFee < 0 || costs.SecFeeRate < 0 || costs.FinraTafPerShare < 0 ||
            costs.FinraTafCap < 0 || costs.MaximumTrackedOrderContracts <= 0 || costs.SyntheticSpreadBps != 0 ||
            costs.InputPriceIncludesExecutionFriction)
            throw new InvalidDataException("Event costs must use raw quote-side prices, explicit friction and valid shared limits.");
    }
}
