using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services.Charts;

internal sealed record PaperChartEvent(string Id, string Kind, string Label, string ClientOrderId,
    DateTimeOffset? EventAtUtc = null, DateTimeOffset? EndAtUtc = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal? RawPrice = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal? Quantity = null,
    string? ExecutionId = null, string? Reason = null);

internal sealed record PaperChartEvidencePage(DateTimeOffset ReadAtUtc, IReadOnlyList<PaperChartEvent> Events,
    IReadOnlyList<PaperChartEvent> ActiveLevels, bool Incomplete, bool Truncated, string? Continuation, string? Detail);

/// <summary>Projects the proven journal observations. All prices stay raw and carry no chart coordinates.</summary>
internal static class PrivatePaperChartProjection
{
    private const int PageLimit = 100;
    private static readonly string[] EffectiveKinds = ["effective_entry", "effective_stop", "effective_target"];

    // Use UTC instants and stable IDs only after the complete candidate set has been bounded.
    public static PaperChartEvidencePage Build(PaperChartFacts facts, PaperChartWindow window, PaperChartCursor? cursor,
        PaperChartContext context, PrivatePaperChartTokens tokens, DateTimeOffset readAt)
    {
        if (facts.OverBudget) return new(readAt, [], [], true, false, null,
            "The selected journal exceeds the 200-record read limit. The evidence is incomplete.");

        var events = Project(facts);
        var active = events.Where(item => EffectiveKinds.Contains(item.Kind) && item.EventAtUtc < window.ToUtc &&
                (item.EndAtUtc is null || item.EndAtUtc >= window.ToUtc))
            .GroupBy(item => item.Kind).Select(group => group.OrderByDescending(OrderTime)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal).First()).ToArray();
        var visible = events.Where(item => Visible(item, window) && After(item, cursor)).OrderBy(OrderTime)
            .ThenBy(item => item.Id, StringComparer.Ordinal).Take(PageLimit + 1).ToArray();
        var truncated = visible.Length > PageLimit;
        var page = visible.Take(PageLimit).ToArray();
        var continuation = truncated
            ? tokens.CreatePage(new(context, window, OrderTime(page[^1]), page[^1].Id)) : null;
        var detail = facts.Binding.Intent.Kind == OrderIntentKind.ProtectiveStop && !facts.ProtectionGenerationProven
            ? "The retained journal does not establish this stop's position generation and replacement history."
            : facts.Binding.Scope.GenerationId.HasValue && facts.Positions.Count > 0 &&
                !active.Any(item => item.Kind == "effective_stop")
                ? "The retained journal does not establish a broker-effective stop at the window end." : null;
        return new(readAt, page, active, false, truncated, continuation, detail);
    }

    // Include overlapping spans as details and keep missing clocks outside the time-marker projection.
    private static bool Visible(PaperChartEvent item, PaperChartWindow window) => item.EventAtUtc is not { } at ||
        (at < window.ToUtc && (at >= window.FromUtc || item.EndAtUtc > window.FromUtc));

    // Continue after the exact final (UTC time, stable ID) pair, with undated rows ordered last.
    private static bool After(PaperChartEvent item, PaperChartCursor? cursor) => cursor is null ||
        OrderTime(item) > cursor.AtUtc ||
        (OrderTime(item) == cursor.AtUtc && StringComparer.Ordinal.Compare(item.Id, cursor.Id) > 0);

    // An unavailable clock sorts last without supplying a marker timestamp.
    private static DateTimeOffset OrderTime(PaperChartEvent item) => item.EventAtUtc ?? DateTimeOffset.MaxValue;

    // Link fills and protection to their exact durable owners before interpreting any private payload field.
    private static List<PaperChartEvent> Project(PaperChartFacts facts)
    {
        var scope = facts.Binding.Scope;
        var orders = facts.Orders.Where(order => order.IntentId == scope.IntentId || BelongsToGeneration(order, scope)).ToArray();
        var lifecycle = facts.Lifecycle.ToLookup(item => item.ClientOrderId, StringComparer.Ordinal);
        var result = new List<PaperChartEvent>();
        AddDecision(facts.Decision, facts.Binding.Intent, result);
        foreach (var order in orders)
        {
            using var plan = ReadObject(order.RequestJson);
            var rows = lifecycle[order.ClientOrderId].OrderBy(item => item.Id).ToArray();
            AddOrder(order, plan?.RootElement, rows, facts.Replacements, facts.ProtectionGenerationProven, result);
        }
        AddFills(facts.Positions, orders, scope, result);
        if (scope.GenerationId.HasValue)
            ResolvePlanEnds(facts.Positions, scope, result);
        return result;
    }

    // A linked broker level or a proven flat-close clock can end the originating generation's frozen stop or target plan.
    private static void ResolvePlanEnds(IReadOnlyList<PositionEventRecord> positions, PaperChartScope scope, List<PaperChartEvent> events)
    {
        var flatExecutions = positions.Where(row => row.QuantityAfter == 0m).Select(row => row.ExecutionId).ToHashSet(StringComparer.Ordinal);
        var closedAt = events.Where(item => item.Kind == "exit_fill" && item.EventAtUtc.HasValue &&
            item.ExecutionId is { } id && flatExecutions.Contains(id)).Select(item => item.EventAtUtc).Min();
        for (var index = 0; index < events.Count; index++)
        {
            var planned = events[index];
            if (planned.ClientOrderId != scope.ClientOrderId || planned.Kind is not ("planned_stop" or "planned_target") ||
                planned.EventAtUtc is not { } plannedAt) continue;
            var effectiveKind = planned.Kind == "planned_stop" ? "effective_stop" : "effective_target";
            var handoff = events.Where(item => item.Kind == effectiveKind && item.EventAtUtc >= plannedAt)
                .Select(item => item.EventAtUtc).Min();
            var end = handoff is null || closedAt < handoff ? closedAt : handoff;
            if (end >= plannedAt) events[index] = planned with { EndAtUtc = end };
        }
    }

    // A protection intent or exit from another run also needs the exact originating generation identity in its request.
    private static bool BelongsToGeneration(PaperChartIntent order, PaperChartScope scope)
    {
        if (scope.GenerationId is not { } generation || order.GenerationId != generation) return false;
        if (order.Kind == OrderIntentKind.ProtectiveStop)
            return IsProtectiveGenerationLink(order, generation, scope.ClientOrderId);
        using var json = ReadObject(order.RequestJson);
        if (json is null) return false;
        var root = json.RootElement;
        return order.Kind switch
        {
            OrderIntentKind.PositionExit => root.TryGetProperty("expectedPositionGenerationEventId", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number == generation &&
                Text(root, "expectedPositionGenerationClientOrderId") == scope.ClientOrderId,
            _ => false
        };
    }

    // A protective order names the exact opening ledger identity in its frozen request.
    internal static bool IsProtectiveGenerationLink(PaperChartIntent order, long generation, string openerClientOrderId)
    {
        using var json = ReadObject(order.RequestJson);
        return Text(json?.RootElement, "PositionGenerationIdentity") ==
            FormattableString.Invariant($"ledger:{generation}:{openerClientOrderId}");
    }

    // Prefer frozen admission and exact candidate clocks, with direct manual authorization as a separate recorded decision.
    private static void AddDecision(PaperChartDecision? decision, PaperChartIntent order, List<PaperChartEvent> events)
    {
        using var manual = order.Kind == OrderIntentKind.OperatorEntry ? ReadObject(order.RequestJson) : null;
        var at = Known(decision?.AtUtc) ?? Stamp(manual?.RootElement, "operatorOverrideIssuedAtUtc");
        var reason = decision?.Reason ?? Text(manual?.RootElement, "operatorOverrideReason");
        if (decision is not null || at.HasValue || reason is not null)
            events.Add(new("decision:" + order.IntentId, "decision", "Recorded decision", order.ClientOrderId,
                at, RawPrice: Positive(decision?.RawPrice), Reason: reason));
    }

    // Preserve order terms separately from the specific prices evidenced by broker acceptance.
    private static void AddOrder(PaperChartIntent order, JsonElement? plan, IReadOnlyList<PaperChartLifecycle> rows,
        IReadOnlyList<PaperChartReplacement> replacements, bool protectionGenerationProven, List<PaperChartEvent> events)
    {
        var entry = PrivatePaperChartAccess.IsEntry(order.Kind);
        var protective = order.Kind == OrderIntentKind.ProtectiveStop;
        var reason = Text(plan, "reason");
        var isTarget = order.Kind == OrderIntentKind.PositionExit && reason == "take_profit" && order.OrderType == "limit";
        var firstAck = rows.FirstOrDefault(row => row.State == "ACKED" && PrivatePaperChartJournal.BrokerLinked(row));
        var terminal = rows.FirstOrDefault(row => row.State is "FILLED" or "CANCELED" or "EXPIRED" or "REJECTED");
        var end = LifecycleClock(terminal);
        var start = LifecycleClock(firstAck);
        if (entry)
        {
            AddPlan(order, "entry", order.OrderType == "market" ? "Planned entry reference" : "Planned entry",
                order.LimitPrice, start ?? end, null, events);
            AddPlan(order, "stop", "Planned stop", order.StopPrice, null, null, events);
            AddPlan(order, "target", "Planned target", Number(plan, "takeProfitPrice"), null, null, events);
        }
        else if (protective)
            AddPlan(order, "stop", "Planned protective stop", order.StopPrice, start ?? end, reason, events);
        else
            AddPlan(order, isTarget ? "target" : "exit", isTarget ? "Planned target exit" : "Planned exit",
                order.LimitPrice ?? order.StopPrice, start ?? end, reason, events);

        AddStatuses(order, rows, events);
        if (firstAck is null || !entry && !protective && !isTarget) return;
        if (protective)
            AddProtection(order, firstAck, terminal, rows, replacements, protectionGenerationProven, events);
        else
            AddEffective(order, firstAck, terminal, entry ? "effective_entry" : "effective_target",
                entry ? "Broker-effective entry" : "Broker-effective target",
                order.OrderType == "limit" ? order.LimitPrice : null, reason, events);
    }

    // A frozen plan starts at intent creation and ends only on an observed handoff or terminal event.
    private static void AddPlan(PaperChartIntent order, string level, string label, decimal? price, DateTimeOffset? end,
        string? reason, List<PaperChartEvent> events)
    {
        if (Positive(price) is not { } amount) return;
        var start = Known(order.CreatedAtUtc);
        events.Add(new("plan:" + order.IntentId + ":" + level, "planned_" + level, label, order.ClientOrderId,
            start, end >= start ? end : null, amount, Reason: reason));
    }

    // Lifecycle state evidence has its own clock and never substitutes cumulative quantities for unit fills.
    private static void AddStatuses(PaperChartIntent order, IReadOnlyList<PaperChartLifecycle> rows, List<PaperChartEvent> events)
    {
        foreach (var row in rows.Where(item => item.State is "CANCEL_PENDING" or "CANCELED" or "EXPIRED" or "REJECTED"))
        {
            using var payload = ReadObject(row.Payload);
            var reason = Text(payload?.RootElement, "reason") ?? Text(payload?.RootElement, "Message");
            var label = row.State == "CANCEL_PENDING" ? "Cancel pending" :
                "Order " + row.State.ToLowerInvariant();
            events.Add(new("lifecycle:" + row.Id, "order_status", label, order.ClientOrderId, LifecycleClock(row), Reason: reason));
        }
    }

    // A known terminal state with a missing or contradictory clock is retained without claiming an active span.
    private static void AddEffective(PaperChartIntent order, PaperChartLifecycle ack, PaperChartLifecycle? terminal,
        string kind, string label, decimal? price, string? reason, List<PaperChartEvent> events)
    {
        var start = LifecycleClock(ack);
        var end = terminal?.BrokerOrderId == ack.BrokerOrderId ? LifecycleClock(terminal) : null;
        var unplacedTerminal = terminal is not null && (end is null || end < start);
        events.Add(new("effective:" + ack.Id, unplacedTerminal ? "unplaced_effective" : kind, label,
            order.ClientOrderId, start, unplacedTerminal ? null : end, UnchangedBrokerPrice(price),
            Reason: PriceProofReason(price, reason)));
    }

    // End protective spans only where the verified record and lifecycle prove the same owner/predecessor/successor chain.
    private static void AddProtection(PaperChartIntent order, PaperChartLifecycle ack, PaperChartLifecycle? terminal,
        IReadOnlyList<PaperChartLifecycle> lifecycle, IReadOnlyList<PaperChartReplacement> replacements,
        bool protectionGenerationProven, List<PaperChartEvent> events)
    {
        var root = ack.BrokerOrderId!;
        var broker = root;
        var current = new PaperChartEvent("effective:" + ack.Id, protectionGenerationProven ? "effective_stop" : "unplaced_effective",
            protectionGenerationProven ? "Broker-effective stop" : "Broker stop acknowledged",
            order.ClientOrderId, LifecycleClock(ack), RawPrice: UnchangedBrokerPrice(order.StopPrice),
            Reason: PriceProofReason(order.StopPrice, null));
        foreach (var replacement in replacements.Where(row => row.Owner == order.ClientOrderId && row.Root == root)
                     .OrderBy(row => row.Verified).ThenBy(row => row.Id))
        {
            if (replacement.Predecessor != broker || replacement.StopPrice <= 0m) continue;
            var successor = lifecycle.FirstOrDefault(row => ReplacementMatches(row, replacement));
            var verifiedAt = successor is null ? null : LifecycleClock(successor);
            if (verifiedAt is null || verifiedAt < current.EventAtUtc)
            {
                // In-place verification provides an observed price without the broker's effective transition time.
                if (replacement.Successor == broker && Known(replacement.Verified) is { } observed)
                {
                    events.Add(new("verified:" + replacement.Id, "stop_observation", "Replacement stop verified",
                        order.ClientOrderId, observed, RawPrice: replacement.StopPrice, Reason: replacement.Reason +
                            " · Effective transition time unavailable"));
                }
                current = current with { Kind = "unplaced_effective" };
                continue;
            }
            events.Add(current with { EndAtUtc = verifiedAt });
            current = new("replacement:" + replacement.Id, "effective_stop", "Broker-effective replacement stop",
                order.ClientOrderId, verifiedAt, RawPrice: replacement.StopPrice, Reason: replacement.Reason);
            broker = replacement.Successor;
        }
        var end = terminal?.BrokerOrderId == broker ? LifecycleClock(terminal) : null;
        if (terminal is not null && (end is null || end < current.EventAtUtc))
            current = current with { Kind = "unplaced_effective" };
        events.Add(current with { EndAtUtc = end >= current.EventAtUtc ? end : null });
    }

    // Broker replacement events contain the immutable command and owner linkage written alongside the verification.
    private static bool ReplacementMatches(PaperChartLifecycle row, PaperChartReplacement replacement)
    {
        if (!PrivatePaperChartJournal.BrokerLinked(row) || row.BrokerOrderId != replacement.Successor) return false;
        using var json = ReadObject(row.Payload);
        var root = json?.RootElement;
        return Text(root, "action") == "protective_stop_replaced" &&
            Guid.TryParse(Text(root, "commandId"), out var commandId) && commandId == replacement.Id &&
            Text(root, "predecessorBrokerOrderId") == replacement.Predecessor &&
            Text(root, "successorBrokerOrderId") == replacement.Successor &&
            Text(root, "replacementClientOrderId") == replacement.ReplacementClientOrderId &&
            Number(root, "StopPrice") == replacement.StopPrice;
    }

    // Reuse the comparison's execution ID, raw unit price, incremental quantity and independent execution clock.
    private static void AddFills(IReadOnlyList<PositionEventRecord> positions, IReadOnlyList<PaperChartIntent> orders,
        PaperChartScope scope, List<PaperChartEvent> events)
    {
        var byClient = orders.ToDictionary(order => order.ClientOrderId, StringComparer.Ordinal);
        var proven = positions.Where(row => byClient.TryGetValue(row.ClientOrderId, out var order) &&
            row.RunId == order.RunId && row.ConfigHash == order.ConfigHash && row.CodeVersion == order.CodeVersion &&
            row.AccountId == scope.AccountId && row.Symbol == scope.JournalSymbol &&
            (scope.GenerationId is null || row.PositionGenerationEventId == scope.GenerationId &&
                row.PositionGenerationClientOrderId == scope.ClientOrderId));
        foreach (var (client, fills) in PaperExecutionComparison.ReadUnitFills(proven))
        {
            var order = byClient[client];
            using var plan = ReadObject(order.RequestJson);
            var entry = PrivatePaperChartAccess.IsEntry(order.Kind);
            foreach (var fill in fills)
                events.Add(new("fill:" + fill.ExecutionId, entry ? "entry_fill" : "exit_fill",
                    entry ? "Entry execution" : "Exit execution", client, Known(fill.AtUtc), RawPrice: fill.Price,
                    Quantity: fill.Quantity, ExecutionId: fill.ExecutionId, Reason: Text(plan?.RootElement, "reason")));
        }
    }

    // Use provider time for broker events and local observation time for engine events.
    private static DateTimeOffset? LifecycleClock(PaperChartLifecycle? row) => row is null ? null :
        Known(row.Source is "broker_rest" or "broker_stream" ? row.BrokerAtUtc : row.LocalAtUtc);

    // Exclude uninitialised clocks and normalize every exposed observation to an exact UTC instant.
    private static DateTimeOffset? Known(DateTimeOffset? value) =>
        value is { } at && at != default ? at.ToUniversalTime() : null;

    // Omit zero or missing prices from details instead of inventing a numeric value.
    private static decimal? Positive(decimal? value) => value is > 0 ? value : null;

    // The broker rounds submitted US equity prices; an acknowledgement alone cannot prove a changed raw level.
    private static decimal? UnchangedBrokerPrice(decimal? value) => value is > 0m and var amount &&
        Decimal.Round(amount, amount >= 1m ? 2 : 4, MidpointRounding.ToZero) == amount ? amount : null;

    // Preserve an acknowledgement when its exact broker-effective price was not retained.
    private static string? PriceProofReason(decimal? price, string? reason) =>
        price is > 0m && UnchangedBrokerPrice(price) is null
            ? "Broker-effective price unavailable from the acknowledgement." : reason;

    // Payloads are size-capped at the SQL projection and are decoded only after candidate admission.
    internal static JsonDocument? ReadObject(string? value)
    {
        if (String.IsNullOrEmpty(value)) return null;
        try
        {
            var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
            json.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    // Expose only an expected string field of bounded length from the retained evidence.
    internal static string? Text(JsonElement? value, string field) =>
        value is { } root && root.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String &&
        item.GetString() is { Length: > 0 and <= 512 } text ? text : null;

    // Preserve the source's exact timestamp without falling back to the request or server clock.
    internal static DateTimeOffset? Stamp(JsonElement? value, string field) =>
        value is { } root && root.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String &&
        item.TryGetDateTimeOffset(out var stamp) ? Known(stamp) : null;

    // Retain numeric prices as persisted decimal evidence.
    private static decimal? Number(JsonElement? value, string field) =>
        value is { } root && root.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetDecimal(out var amount) ? amount : null;
}
