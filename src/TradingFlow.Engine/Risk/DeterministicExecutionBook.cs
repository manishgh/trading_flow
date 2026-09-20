using System.Collections.Frozen;

namespace TradingFlow.Engine.Risk;

public interface IDeterministicExecutionSimulator
{
    ExecutionStepResult Advance(ExecutionStepRequest request);
}

public enum ExecutionCommandKind
{
    Submit,
    Cancel,
    Replace
}

public enum SimulatedTimeInForce
{
    Day,
    GoodTillCanceled,
    ImmediateOrCancel
}

public sealed record ExecutionMarketSlice(
    string Symbol,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset AvailableAtUtc,
    long SourceSequence,
    bool IsCompleted,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal? Bid = null,
    decimal? Ask = null,
    DateTimeOffset? QuoteObservedAtUtc = null);

public sealed record SimulatedOrderCommand(
    ExecutionCommandKind Kind,
    string ClientOrderId,
    string Symbol,
    SimulatedOrderSide Side,
    SimulatedOrderType OrderType,
    int Quantity,
    DateTimeOffset KnownAtUtc,
    long Sequence,
    bool IsRiskReducing,
    SimulatedTimeInForce TimeInForce = SimulatedTimeInForce.Day,
    decimal? LimitPrice = null,
    decimal? StopPrice = null,
    string? TargetClientOrderId = null,
    string? FixedFeeGroupId = null);

public sealed record SimulatedWorkingOrder(
    SimulatedOrderCommand Command,
    int FilledQuantity,
    decimal FilledNotional,
    bool FixedFeeCharged,
    decimal FinraTafCharged,
    bool StopTriggered)
{
    public int RemainingQuantity => Command.Quantity - FilledQuantity;
}

public sealed record SimulatedPosition(
    string Symbol,
    int SignedQuantity,
    decimal AveragePrice,
    decimal RealizedProfit);

public sealed record SimulatedExecutionEvent(
    string ClientOrderId,
    string Symbol,
    string State,
    int LastFillQuantity,
    int CumulativeFillQuantity,
    int RemainingQuantity,
    decimal? FillPrice,
    decimal Fees,
    decimal SpreadBps,
    decimal SlippageBps,
    decimal ImpactBps,
    int ResultingPositionQuantity,
    decimal RealizedProfit,
    DateTimeOffset OccurredAtUtc,
    string Reason,
    SimulatedOrderSide Side,
    bool IsRiskReducing);

public sealed record ExecutionBookState(
    IReadOnlyDictionary<string, SimulatedWorkingOrder> WorkingOrders,
    IReadOnlyDictionary<string, SimulatedPosition> Positions,
    IReadOnlyDictionary<string, SimulatedOrderCommand> KnownOrderContracts,
    IReadOnlyDictionary<string, long> LastProcessedSliceSequence,
    decimal AccumulatedFees,
    IReadOnlySet<string> ChargedFixedFeeGroupIds)
{
    public static ExecutionBookState Empty { get; } = new(
        Array.Empty<KeyValuePair<string, SimulatedWorkingOrder>>().ToFrozenDictionary(StringComparer.Ordinal),
        Array.Empty<KeyValuePair<string, SimulatedPosition>>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        Array.Empty<KeyValuePair<string, SimulatedOrderCommand>>().ToFrozenDictionary(StringComparer.Ordinal),
        Array.Empty<KeyValuePair<string, long>>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
        0m,
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal));
}

public sealed record ExecutionStepRequest(
    ExecutionBookState PriorState,
    ExecutionMarketSlice Market,
    IReadOnlyList<SimulatedOrderCommand> Commands,
    ExecutionSimulationPolicy Policy);

public sealed record ExecutionStepResult(
    ExecutionBookState State,
    IReadOnlyList<SimulatedExecutionEvent> Events);

public sealed partial class DeterministicExecutionSimulator
{
    /// <summary>
    /// Advances an immutable execution book by one completed market slice. Commands
    /// known after the slice starts are retained but cannot consume that slice, and a
    /// repeated source sequence is an idempotent no-op.
    /// </summary>
    public ExecutionStepResult Advance(ExecutionStepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSlice(request.Market);
        var symbol = NormalizeSymbol(request.Market.Symbol);
        var lastSequence = request.PriorState.LastProcessedSliceSequence.TryGetValue(symbol, out var prior)
            ? prior
            : Int64.MinValue;
        if (request.Market.SourceSequence == lastSequence)
        {
            if (request.Commands.Count > 0)
            {
                throw new InvalidOperationException(
                    "Commands cannot be attached to an already processed market slice.");
            }

            return new ExecutionStepResult(request.PriorState, Array.Empty<SimulatedExecutionEvent>());
        }

        if (request.Market.SourceSequence < lastSequence)
        {
            throw new InvalidOperationException(
                $"Market slice sequence {request.Market.SourceSequence} precedes processed sequence {lastSequence} for {symbol}.");
        }

        var working = request.PriorState.WorkingOrders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var positions = request.PriorState.Positions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var knownOrderContracts = request.PriorState.KnownOrderContracts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        var lastSequences = request.PriorState.LastProcessedSliceSequence.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var chargedFixedFeeGroups = new HashSet<string>(
            request.PriorState.ChargedFixedFeeGroupIds,
            StringComparer.Ordinal);
        var events = new List<SimulatedExecutionEvent>();

        var orderedCommands = request.Commands
            .OrderBy(item => item.KnownAtUtc)
            .ThenBy(item => item.Sequence)
            .ThenBy(item => item.ClientOrderId, StringComparer.Ordinal)
            .ToArray();
        if (orderedCommands.Any(command => command.KnownAtUtc > request.Market.AvailableAtUtc))
        {
            throw new InvalidOperationException("A command cannot be processed before it is known.");
        }

        var newContractCount = orderedCommands
            .Where(command => command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace)
            .Select(command => command.ClientOrderId)
            .Distinct(StringComparer.Ordinal)
            .Count(clientOrderId => !knownOrderContracts.ContainsKey(clientOrderId));
        if (request.Policy.MaximumTrackedOrderContracts <= 0 ||
            (long)knownOrderContracts.Count + newContractCount > request.Policy.MaximumTrackedOrderContracts)
        {
            throw new InvalidOperationException(
                $"Execution history exceeds the configured order-contract limit of {request.Policy.MaximumTrackedOrderContracts:N0}.");
        }

        foreach (var command in orderedCommands.Where(item => item.KnownAtUtc <= request.Market.StartUtc))
        {
            ApplyCommand(command, working, knownOrderContracts, positions, request.Market, events);
        }

        var capacity = request.Policy.MaxParticipationPct > 0m
            ? CalculateFillableQuantity(
                Int32.MaxValue,
                request.Market.Volume,
                request.Policy.MaxParticipationPct,
                missingVolumeFailsClosed: true)
            : Int32.MaxValue;
        var remainingCapacity = capacity;

        foreach (var order in working.Values
                     .Where(item => NormalizeSymbol(item.Command.Symbol) == symbol)
                     .Where(item => item.Command.KnownAtUtc <= request.Market.StartUtc)
                     .OrderByDescending(item => item.Command.IsRiskReducing)
                     .ThenBy(item => ResolveRiskReducingPriority(item, positions))
                     .ThenBy(item => item.Command.KnownAtUtc)
                     .ThenBy(item => item.Command.Sequence)
                     .ThenBy(item => item.Command.ClientOrderId, StringComparer.Ordinal)
                     .ToArray())
        {
            var maximumOrderFill = remainingCapacity;
            if (order.Command.IsRiskReducing)
            {
                positions.TryGetValue(symbol, out var currentPosition);
                var reducesLong = currentPosition?.SignedQuantity > 0 && order.Command.Side == SimulatedOrderSide.Sell;
                var reducesShort = currentPosition?.SignedQuantity < 0 && order.Command.Side == SimulatedOrderSide.Buy;
                if (!reducesLong && !reducesShort)
                {
                    working.Remove(order.Command.ClientOrderId);
                    events.Add(CreateStateEvent(
                        order,
                        "canceled",
                        positions,
                        request.Market.EndUtc,
                        "position_quantity_unavailable"));
                    continue;
                }

                maximumOrderFill = Math.Min(maximumOrderFill, Math.Abs(currentPosition!.SignedQuantity));
            }

            var quoteIsKnownBeforeSlice = request.Market.QuoteObservedAtUtc is { } quoteTime &&
                quoteTime <= request.Market.StartUtc;
            var market = new ExecutionMarketSnapshot(
                request.Market.EndUtc,
                request.Market.Open,
                request.Market.High,
                request.Market.Low,
                request.Market.Close,
                request.Market.Volume,
                quoteIsKnownBeforeSlice ? request.Market.Bid : null,
                quoteIsKnownBeforeSlice ? request.Market.Ask : null);
            var remainingTafCap = request.Policy.FinraTafCap > 0m
                ? Math.Max(request.Policy.FinraTafCap - order.FinraTafCharged, 0m)
                : 0m;
            var policy = request.Policy with
            {
                FixedBuyFee = order.FixedFeeCharged ||
                    chargedFixedFeeGroups.Contains(ResolveFixedFeeGroupId(order.Command))
                    ? 0m
                    : request.Policy.FixedBuyFee,
                FixedSellFee = order.FixedFeeCharged ||
                    chargedFixedFeeGroups.Contains(ResolveFixedFeeGroupId(order.Command))
                    ? 0m
                    : request.Policy.FixedSellFee,
                FinraTafPerShare = request.Policy.FinraTafCap > 0m && remainingTafCap == 0m
                    ? 0m
                    : request.Policy.FinraTafPerShare,
                FinraTafCap = remainingTafCap
            };
            var effectiveOrderType = order.StopTriggered
                ? SimulatedOrderType.Limit
                : order.Command.OrderType;
            var fill = SimulateCore(
                new ExecutionSimulationRequest(
                    order.Command.Side,
                    effectiveOrderType,
                    order.RemainingQuantity,
                    market,
                    policy,
                    order.Command.LimitPrice,
                    order.Command.StopPrice),
                maximumOrderFill);
            var lateMutation = orderedCommands.FirstOrDefault(command =>
                (command.Kind is ExecutionCommandKind.Cancel or ExecutionCommandKind.Replace) &&
                command.KnownAtUtc > request.Market.StartUtc &&
                String.Equals(command.TargetClientOrderId, order.Command.ClientOrderId, StringComparison.Ordinal));
            if (lateMutation is not null &&
                effectiveOrderType != SimulatedOrderType.Market &&
                (fill.Status != SimulatedFillStatus.NoFill ||
                 fill.Reason == "ambiguous_intrabar_stop_limit_sequence"))
            {
                throw new InvalidOperationException(
                    $"Order '{order.Command.ClientOrderId}' and its {lateMutation.Kind} command overlap one market slice. " +
                    "Split the slice at the command timestamp; intrabar ordering cannot be inferred.");
            }

            if (fill.Status == SimulatedFillStatus.NoFill)
            {
                if (!order.StopTriggered &&
                    order.Command.OrderType == SimulatedOrderType.StopLimit &&
                    fill.Reason == "ambiguous_intrabar_stop_limit_sequence")
                {
                    var triggered = order with { StopTriggered = true };
                    working[order.Command.ClientOrderId] = triggered;
                    events.Add(CreateStateEvent(
                        triggered,
                        "triggered",
                        positions,
                        request.Market.EndUtc,
                        "stop_limit_activated"));
                    if (order.Command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                    {
                        working.Remove(order.Command.ClientOrderId);
                        events.Add(CreateStateEvent(
                            triggered,
                            "canceled",
                            positions,
                            request.Market.EndUtc,
                            "immediate_or_cancel_unfilled"));
                    }

                    continue;
                }

                if (order.Command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
                {
                    working.Remove(order.Command.ClientOrderId);
                    events.Add(CreateStateEvent(order, "canceled", positions, request.Market.EndUtc, fill.Reason));
                }

                continue;
            }

            remainingCapacity -= fill.FilledQuantity;
            var cumulativeQuantity = order.FilledQuantity + fill.FilledQuantity;
            var cumulativeNotional = order.FilledNotional +
                (fill.FilledQuantity * fill.AverageFillPrice!.Value);
            var updated = order with
            {
                FilledQuantity = cumulativeQuantity,
                FilledNotional = cumulativeNotional,
                FixedFeeCharged = true,
                FinraTafCharged = order.FinraTafCharged + CalculateFinraTafFee(
                    fill.FilledQuantity,
                    order.Command.Side == SimulatedOrderSide.Sell ? policy.FinraTafPerShare : 0m,
                    policy.FinraTafCap)
            };
            chargedFixedFeeGroups.Add(ResolveFixedFeeGroupId(order.Command));
            var position = ApplyFill(positions, symbol, order.Command.Side, fill.FilledQuantity, fill.AverageFillPrice.Value);
            var remainingQuantity = order.Command.Quantity - cumulativeQuantity;
            var state = remainingQuantity == 0 ? "filled" : "partially_filled";
            if (remainingQuantity == 0 || order.Command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel)
            {
                working.Remove(order.Command.ClientOrderId);
            }
            else
            {
                working[order.Command.ClientOrderId] = updated;
            }

            events.Add(new SimulatedExecutionEvent(
                order.Command.ClientOrderId,
                symbol,
                state,
                fill.FilledQuantity,
                cumulativeQuantity,
                remainingQuantity,
                fill.AverageFillPrice,
                fill.Fees,
                fill.SpreadBps,
                fill.SlippageBps,
                fill.ImpactBps,
                position.SignedQuantity,
                position.RealizedProfit,
                request.Market.EndUtc,
                fill.Reason,
                order.Command.Side,
                order.Command.IsRiskReducing));
            if (order.Command.IsRiskReducing && position.SignedQuantity == 0 && remainingQuantity > 0)
            {
                working.Remove(order.Command.ClientOrderId);
                events.Add(CreateStateEvent(
                    updated,
                    "canceled",
                    positions,
                    request.Market.EndUtc,
                    "position_quantity_exhausted"));
            }
            if (remainingQuantity > 0 &&
                order.Command.TimeInForce == SimulatedTimeInForce.ImmediateOrCancel &&
                !(order.Command.IsRiskReducing && position.SignedQuantity == 0))
            {
                events.Add(CreateStateEvent(
                    updated,
                    "canceled",
                    positions,
                    request.Market.EndUtc,
                    "immediate_or_cancel_remainder"));
            }
        }

        // Commands learned while the slice was forming cannot change fills from that
        // slice. They are committed afterward and become eligible on the next slice.
        foreach (var command in orderedCommands.Where(item => item.KnownAtUtc > request.Market.StartUtc))
        {
            ApplyCommand(command, working, knownOrderContracts, positions, request.Market, events);
        }

        lastSequences[symbol] = request.Market.SourceSequence;
        var stateResult = new ExecutionBookState(
            working.ToFrozenDictionary(StringComparer.Ordinal),
            positions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            knownOrderContracts.ToFrozenDictionary(StringComparer.Ordinal),
            lastSequences.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            request.PriorState.AccumulatedFees + events.Sum(item => item.Fees),
            chargedFixedFeeGroups.ToFrozenSet(StringComparer.Ordinal));
        return new ExecutionStepResult(stateResult, events);
    }

    private static void ApplyCommand(
        SimulatedOrderCommand command,
        IDictionary<string, SimulatedWorkingOrder> working,
        IDictionary<string, SimulatedOrderCommand> knownOrderContracts,
        IReadOnlyDictionary<string, SimulatedPosition> positions,
        ExecutionMarketSlice market,
        ICollection<SimulatedExecutionEvent> events)
    {
        ValidateCommand(command);
        if (command.Kind == ExecutionCommandKind.Submit)
        {
            if (knownOrderContracts.TryGetValue(command.ClientOrderId, out var priorContract))
            {
                if (!HasSameOrderContract(priorContract, command))
                {
                    throw new InvalidOperationException(
                        $"Client order ID '{command.ClientOrderId}' was reused with a different order contract.");
                }

                return;
            }

            ValidateRiskReduction(command, positions);
            knownOrderContracts[command.ClientOrderId] = command;
            working[command.ClientOrderId] = new SimulatedWorkingOrder(command, 0, 0m, false, 0m, false);
            return;
        }

        if (command.Kind == ExecutionCommandKind.Replace &&
            knownOrderContracts.TryGetValue(command.ClientOrderId, out var replayedReplacement))
        {
            if (!HasSameOrderContract(replayedReplacement, command))
            {
                throw new InvalidOperationException(
                    $"Replacement client order ID '{command.ClientOrderId}' was reused with a different order contract.");
            }

            return;
        }

        var targetId = String.IsNullOrWhiteSpace(command.TargetClientOrderId)
            ? throw new InvalidOperationException($"{command.Kind} requires a target client order ID.")
            : command.TargetClientOrderId;
        if (!working.TryGetValue(targetId, out var target))
        {
            return;
        }

        if (command.Kind == ExecutionCommandKind.Replace)
        {
            if (knownOrderContracts.ContainsKey(command.ClientOrderId))
            {
                throw new InvalidOperationException(
                    $"Replacement client order ID '{command.ClientOrderId}' has already been used.");
            }

            if (!NormalizeSymbol(command.Symbol).Equals(NormalizeSymbol(target.Command.Symbol), StringComparison.Ordinal) ||
                command.Side != target.Command.Side ||
                command.IsRiskReducing != target.Command.IsRiskReducing ||
                command.Quantity < target.FilledQuantity)
            {
                throw new InvalidOperationException(
                    "A replacement must preserve symbol, side, and risk classification and cannot reduce total quantity below prior fills.");
            }

            ValidateRiskReduction(command, positions, command.Quantity - target.FilledQuantity);
        }

        working.Remove(targetId);

        events.Add(CreateStateEvent(target, "canceled", positions, command.KnownAtUtc,
            command.Kind == ExecutionCommandKind.Replace ? "replaced" : "operator_cancel"));
        if (command.Kind == ExecutionCommandKind.Replace)
        {
            knownOrderContracts[command.ClientOrderId] = command;
            working[command.ClientOrderId] = new SimulatedWorkingOrder(
                command,
                target.FilledQuantity,
                target.FilledNotional,
                target.FixedFeeCharged,
                target.FinraTafCharged,
                target.StopTriggered && command.OrderType == SimulatedOrderType.StopLimit);
        }
    }

    private static SimulatedPosition ApplyFill(
        IDictionary<string, SimulatedPosition> positions,
        string symbol,
        SimulatedOrderSide side,
        int quantity,
        decimal price)
    {
        positions.TryGetValue(symbol, out var existing);
        existing ??= new SimulatedPosition(symbol, 0, 0m, 0m);
        var signedFill = side == SimulatedOrderSide.Buy ? quantity : -quantity;
        var current = existing.SignedQuantity;
        var resulting = current + signedFill;
        var realized = existing.RealizedProfit;
        decimal averagePrice;

        if (current == 0 || Math.Sign(current) == Math.Sign(signedFill))
        {
            averagePrice = resulting == 0
                ? 0m
                : ((Math.Abs(current) * existing.AveragePrice) + (quantity * price)) / Math.Abs(resulting);
        }
        else
        {
            var closingQuantity = Math.Min(Math.Abs(current), quantity);
            realized += current > 0
                ? (price - existing.AveragePrice) * closingQuantity
                : (existing.AveragePrice - price) * closingQuantity;
            averagePrice = resulting == 0
                ? 0m
                : Math.Sign(resulting) == Math.Sign(current) ? existing.AveragePrice : price;
        }

        var updated = new SimulatedPosition(symbol, resulting, averagePrice, Decimal.Round(realized, 6));
        positions[symbol] = updated;
        return updated;
    }

    private static SimulatedExecutionEvent CreateStateEvent(
        SimulatedWorkingOrder order,
        string state,
        IReadOnlyDictionary<string, SimulatedPosition> positions,
        DateTimeOffset occurredAtUtc,
        string reason)
    {
        positions.TryGetValue(NormalizeSymbol(order.Command.Symbol), out var position);
        return new SimulatedExecutionEvent(
            order.Command.ClientOrderId,
            NormalizeSymbol(order.Command.Symbol),
            state,
            0,
            order.FilledQuantity,
            order.RemainingQuantity,
            null,
            0m,
            0m,
            0m,
            0m,
            position?.SignedQuantity ?? 0,
            position?.RealizedProfit ?? 0m,
            occurredAtUtc,
            reason,
            order.Command.Side,
            order.Command.IsRiskReducing);
    }

    private static void ValidateSlice(ExecutionMarketSlice market)
    {
        if (String.IsNullOrWhiteSpace(market.Symbol) || !market.IsCompleted)
        {
            throw new ArgumentException("Execution requires a completed market slice with a symbol.", nameof(market));
        }

        var hasBid = market.Bid is not null;
        var hasAsk = market.Ask is not null;
        if (market.StartUtc.Offset != TimeSpan.Zero || market.EndUtc.Offset != TimeSpan.Zero ||
            market.AvailableAtUtc.Offset != TimeSpan.Zero || market.StartUtc >= market.EndUtc ||
            market.AvailableAtUtc < market.EndUtc || market.SourceSequence < 0 ||
            hasBid != hasAsk || (hasBid && market.QuoteObservedAtUtc is null) ||
            (market.QuoteObservedAtUtc is { } quoteTime &&
             (quoteTime.Offset != TimeSpan.Zero || quoteTime > market.AvailableAtUtc || !hasBid)))
        {
            throw new ArgumentException("Market slice timing and sequence evidence is invalid.", nameof(market));
        }

        _ = new DeterministicExecutionSimulator().Simulate(new ExecutionSimulationRequest(
            SimulatedOrderSide.Buy,
            SimulatedOrderType.Market,
            1,
            new ExecutionMarketSnapshot(
                market.EndUtc,
                market.Open,
                market.High,
                market.Low,
                market.Close,
                market.Volume,
                market.Bid,
                market.Ask),
            new ExecutionSimulationPolicy(
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                InputPriceIncludesExecutionFriction: true)));
    }

    private static void ValidateCommand(SimulatedOrderCommand command)
    {
        if (String.IsNullOrWhiteSpace(command.ClientOrderId) || String.IsNullOrWhiteSpace(command.Symbol) ||
            command.KnownAtUtc == default || command.KnownAtUtc.Offset != TimeSpan.Zero || command.Sequence < 0)
        {
            throw new ArgumentException("Execution command identity, symbol, UTC time, and sequence are required.", nameof(command));
        }

        if (command.Kind is ExecutionCommandKind.Submit or ExecutionCommandKind.Replace && command.Quantity <= 0)
        {
            throw new ArgumentException("Submit and replace commands require a positive quantity.", nameof(command));
        }
    }

    private static void ValidateRiskReduction(
        SimulatedOrderCommand command,
        IReadOnlyDictionary<string, SimulatedPosition> positions,
        int? effectiveQuantity = null)
    {
        if (!command.IsRiskReducing)
        {
            return;
        }

        positions.TryGetValue(NormalizeSymbol(command.Symbol), out var position);
        var reducesLong = position?.SignedQuantity > 0 && command.Side == SimulatedOrderSide.Sell;
        var reducesShort = position?.SignedQuantity < 0 && command.Side == SimulatedOrderSide.Buy;
        var quantity = effectiveQuantity ?? command.Quantity;
        if ((!reducesLong && !reducesShort) || quantity > Math.Abs(position!.SignedQuantity))
        {
            throw new InvalidOperationException(
                $"Order '{command.ClientOrderId}' is marked risk-reducing but does not reduce the current position.");
        }
    }

    private static int ResolveRiskReducingPriority(
        SimulatedWorkingOrder order,
        IReadOnlyDictionary<string, SimulatedPosition> positions)
    {
        if (!order.Command.IsRiskReducing ||
            !positions.TryGetValue(NormalizeSymbol(order.Command.Symbol), out var position))
        {
            return 2;
        }

        // If one completed bar touches both a protective stop and a profit limit,
        // the intrabar path is unknown. Resolve conservatively by processing the
        // adverse stop first, then cap/cancel siblings against the remaining lot.
        var isProtectiveStop = position.SignedQuantity > 0
            ? order.Command.Side == SimulatedOrderSide.Sell &&
              order.Command.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit
            : order.Command.Side == SimulatedOrderSide.Buy &&
              order.Command.OrderType is SimulatedOrderType.Stop or SimulatedOrderType.StopLimit;
        return isProtectiveStop ? 0 : 1;
    }

    private static decimal CalculateFinraTafFee(
        int quantity,
        decimal perShareRate,
        decimal remainingCap)
    {
        if (quantity <= 0 || perShareRate <= 0m)
        {
            return 0m;
        }

        var fee = quantity * perShareRate;
        return remainingCap > 0m ? Math.Min(fee, remainingCap) : fee;
    }

    private static bool HasSameOrderContract(
        SimulatedOrderCommand left,
        SimulatedOrderCommand right) =>
        left.Kind == right.Kind &&
        NormalizeSymbol(left.Symbol).Equals(NormalizeSymbol(right.Symbol), StringComparison.Ordinal) &&
        left.Side == right.Side &&
        left.OrderType == right.OrderType &&
        left.Quantity == right.Quantity &&
        left.IsRiskReducing == right.IsRiskReducing &&
        left.TimeInForce == right.TimeInForce &&
        left.LimitPrice == right.LimitPrice &&
        left.StopPrice == right.StopPrice &&
        String.Equals(left.FixedFeeGroupId, right.FixedFeeGroupId, StringComparison.Ordinal) &&
        String.Equals(left.TargetClientOrderId, right.TargetClientOrderId, StringComparison.Ordinal);

    private static string ResolveFixedFeeGroupId(SimulatedOrderCommand command) =>
        String.IsNullOrWhiteSpace(command.FixedFeeGroupId)
            ? command.ClientOrderId
            : command.FixedFeeGroupId;

    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();
}
