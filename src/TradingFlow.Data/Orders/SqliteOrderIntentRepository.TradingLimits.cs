using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteOrderIntentRepository
{
    private static readonly JsonSerializerOptions TradingLimitJson = new(JsonSerializerDefaults.Web);

    /// <summary>Reserve possible new symbols separately from actual entry fills, inside the existing account write transaction.</summary>
    private async Task<(OperationalTradingLimits Limits, IReadOnlySet<string> PendingOpeningSymbols)> ReserveOperationalCapacityAsync(TradingFlowDbContext context,
        ProductionRun run, OrderIntentReservation reservation, IReadOnlyList<PortfolioRiskReservationRecord> active,
        DateTimeOffset nowUtc, CancellationToken token)
    {
        var risk = reservation.PortfolioRisk!;
        var history = risk.FillHistory ?? throw RiskRejected(reservation.IntentId, "fill_history_missing",
            "Complete broker fill history is required before admitting a new stock; current holdings do not establish today's entries.");
        RequireFreshFillHistory(history, reservation.AccountId, risk.FillHistoryMaxAgeSeconds, nowUtc, reservation.IntentId);
        var limits = await SqliteOperationalTradingLimitStore.ReadAsync(context, run.Profile, tradingLimitDefaults, token);
        var roles = await ReadOwnedOrderRolesAsync(context, history, token);
        var entered = await ReadEnteredSymbolsAsync(context, history, roles, nowUtc, reservation.IntentId, token);
        var pending = ReadPendingOpeningOrders(history, roles, reservation.IntentId);
        var projected = entered.Concat(active.Where(row => row.PendingQuantity > 0 && row.CumulativeFilledQuantity == 0)
            .Select(row => row.Symbol)).Concat(pending.Where(order => order.FilledQuantity == 0).Select(order => order.Ticker))
            .ToHashSet(StringComparer.Ordinal);
        projected.Add(reservation.Symbol.Trim().ToUpperInvariant());
        if (projected.Count > limits.MaxNewStocksPerDay)
            throw RiskRejected(reservation.IntentId, "daily_new_stock_limit",
                $"Today's filled and pending new stocks would total {projected.Count}; the saved account limit is {limits.MaxNewStocksPerDay}.");

        var evidence = new EntryCapacityEvidence(limits.Mode, limits.Revision, limits.MaxNewStocksPerDay, limits.MaxOpenPositions, history.NewYorkDate,
            history.WindowEndUtc, risk.FillHistoryMaxAgeSeconds!.Value, entered.ToArray());
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = EntryCapacityStream(reservation.IntentId), EventType = "entry.capacity.reserved",
            OccurredAtUtc = nowUtc, PayloadJson = JsonSerializer.Serialize(evidence, TradingLimitJson)
        });
        return (limits, pending.Select(order => order.Ticker).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Reject partial, wrong-account, old-day or stale snapshots rather than treating missing fills as zero.</summary>
    private static void RequireFreshFillHistory(BrokerFillHistorySnapshot history, string accountId,
        int? maximumAgeSeconds, DateTimeOffset nowUtc, Guid intentId)
    {
        if (!history.Complete || history.PendingOrders is null || history.AccountId != accountId.Trim() || maximumAgeSeconds is null or <= 0 ||
            history.NewYorkDate != TradingLimitDay.At(nowUtc) || history.WindowStartUtc != TradingLimitDay.StartUtc(history.NewYorkDate) ||
            history.WindowEndUtc < history.WindowStartUtc || history.RequestedAtUtc > history.WindowEndUtc || history.WindowEndUtc > history.ObservedAtUtc ||
            history.ObservedAtUtc > nowUtc || nowUtc - history.WindowEndUtc > TimeSpan.FromSeconds(maximumAgeSeconds.Value) ||
            history.PageCount <= 0 || history.RawArchiveIds.Count < history.PageCount)
            throw RiskRejected(intentId, "fill_history_unavailable",
                "Broker fill history is incomplete, stale, for another account or from a previous New York day. Refresh account evidence before submitting.");
    }

    /// <summary>Resolve owned roles for both acknowledged fills and pending client IDs before considering external broker roles.</summary>
    private static async Task<IReadOnlyDictionary<string, OrderIntentKind>> ReadOwnedOrderRolesAsync(
        TradingFlowDbContext context, BrokerFillHistorySnapshot history, CancellationToken token)
    {
        var pending = history.PendingOrders ?? throw new InvalidDataException("Complete pending order evidence is required.");
        var ids = history.Activities.Select(fill => fill.BrokerOrderId).Concat(pending.Select(order => order.OrderId)).Distinct().ToArray();
        var clients = pending.Select(order => order.ClientOrderId).Distinct().ToArray();
        var localRoles = await (from orderEvent in context.OrderEvents.AsNoTracking()
                                join owner in context.OrderIntents.AsNoTracking() on orderEvent.ClientOrderId equals owner.ClientOrderId
                                where owner.AccountId == history.AccountId &&
                                    ((orderEvent.BrokerOrderId != null && ids.Contains(orderEvent.BrokerOrderId)) || clients.Contains(owner.ClientOrderId))
                                select new { orderEvent.BrokerOrderId, owner.ClientOrderId, owner.Kind, owner.Symbol, owner.Side }).Distinct().ToArrayAsync(token);
        var roles = localRoles.Where(row => row.BrokerOrderId != null).GroupBy(row => row.BrokerOrderId!).ToDictionary(group => group.Key,
            group => group.Select(row => row.Kind).Distinct().Single(), StringComparer.Ordinal);
        foreach (var order in pending)
        {
            var owned = localRoles.Where(row => row.ClientOrderId == order.ClientOrderId &&
                row.Symbol.Equals(order.Ticker, StringComparison.OrdinalIgnoreCase) &&
                row.Side.Equals(order.Side, StringComparison.OrdinalIgnoreCase)).Select(row => row.Kind).Distinct().ToArray();
            if (owned.Length == 1) roles[order.OrderId] = owned[0];
        }
        await ResolveOwnedClosingLegRolesAsync(context, history.AccountId, pending, roles, token);
        return roles;
    }

    /// <summary>Recognize a bracket child only through its exact filled entry parent, bound account/symbol and opposite closing side.</summary>
    private static async Task ResolveOwnedClosingLegRolesAsync(TradingFlowDbContext context, string accountId,
        IReadOnlyList<ActiveBrokerOrder> pending, IDictionary<string, OrderIntentKind> roles, CancellationToken token)
    {
        var parentIds = pending.Select(order => order.ParentClientOrderId).Where(id => !String.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (parentIds.Length == 0) return;
        var parents = await context.OrderIntents.AsNoTracking().Where(owner => owner.AccountId == accountId &&
            parentIds.Contains(owner.ClientOrderId) &&
            (owner.Kind == OrderIntentKind.StrategyEntry || owner.Kind == OrderIntentKind.OperatorEntry) &&
            context.OrderEvents.Any(fill => fill.ClientOrderId == owner.ClientOrderId && fill.FilledQuantity > 0))
            .ToArrayAsync(token);
        foreach (var order in pending.Where(order => !roles.ContainsKey(order.OrderId)))
        {
            var closingSide = order.Side == "sell" ? "buy" : order.Side == "buy" ? "sell" : null;
            if (closingSide is not null && parents.Any(parent => parent.ClientOrderId == order.ParentClientOrderId &&
                parent.Symbol.Equals(order.Ticker, StringComparison.OrdinalIgnoreCase) && parent.Side == closingSide))
                roles[order.OrderId] = OrderIntentKind.PositionExit;
        }
    }

    /// <summary>Classify pending whole-account orders explicitly; only opening orders reserve stock slots.</summary>
    private static IReadOnlyList<ActiveBrokerOrder> ReadPendingOpeningOrders(BrokerFillHistorySnapshot history,
        IReadOnlyDictionary<string, OrderIntentKind> roles, Guid intentId)
    {
        var opening = new List<ActiveBrokerOrder>();
        foreach (var order in history.PendingOrders!)
        {
            if (order.Qty is not { } quantity || quantity <= 0 || order.FilledQuantity < 0 || order.FilledQuantity > quantity ||
                String.IsNullOrWhiteSpace(order.Ticker) || String.IsNullOrWhiteSpace(order.OrderId))
                throw RiskRejected(intentId, "invalid_pending_broker_order", "A pending broker order lacks usable quantity, symbol or identity.");
            if (quantity == order.FilledQuantity) continue;
            if (IsOpeningOrder(order.OrderId, order.Ticker, order.Side, order.PositionIntent, roles, intentId))
            {
                if (order.FilledQuantity > 0 && order.UpdatedAt > history.WindowEndUtc)
                    throw RiskRejected(intentId, "fill_history_refresh_required",
                        "A pending opening order reports a fill after the history cutoff. Refresh complete account fills before admitting another stock.");
                opening.Add(order with { Ticker = order.Ticker.Trim().ToUpperInvariant() });
            }
        }
        return opening;
    }

    /// <summary>Count actual first entry fills; later partials and closing orders do not consume another daily stock.</summary>
    private static async Task<HashSet<string>> ReadEnteredSymbolsAsync(TradingFlowDbContext context,
        BrokerFillHistorySnapshot history, IReadOnlyDictionary<string, OrderIntentKind> roles,
        DateTimeOffset nowUtc, Guid intentId, CancellationToken token)
    {
        var entered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fill in history.Activities)
        {
            RequireValidFill(fill, history, intentId);
            if (fill.CumulativeQuantity == fill.Quantity && IsOpeningOrder(fill.BrokerOrderId, fill.Symbol, fill.Side, fill.PositionIntent, roles, intentId))
                entered.Add(fill.Symbol.Trim().ToUpperInvariant());
        }
        await RequireNoNewerOwnedEntryFillAsync(context, history, nowUtc, intentId, token);
        return entered;
    }

    /// <summary>Require usable first-fill evidence; later partial fills never create another daily stock.</summary>
    private static void RequireValidFill(BrokerFillActivity fill, BrokerFillHistorySnapshot history, Guid intentId)
    {
        if (fill.Quantity <= 0 || fill.CumulativeQuantity < fill.Quantity || fill.FilledAtUtc < history.WindowStartUtc ||
            fill.FilledAtUtc > history.WindowEndUtc || String.IsNullOrWhiteSpace(fill.Symbol) ||
            String.IsNullOrWhiteSpace(fill.ActivityId) || String.IsNullOrWhiteSpace(fill.BrokerOrderId) || fill.Side is not ("buy" or "sell"))
            throw RiskRejected(intentId, "invalid_broker_fill", "Broker fill evidence contains an invalid quantity, identity, side or timestamp.");
    }

    /// <summary>Use the application's order role first, or the broker's explicit opening/closing role for external orders.</summary>
    private static bool IsOpeningOrder(string orderId, string symbol, string side, string? positionIntent,
        IReadOnlyDictionary<string, OrderIntentKind> roles, Guid intentId)
    {
        if ((positionIntent is "buy_to_open" or "buy_to_close") && side != "buy" ||
            (positionIntent is "sell_to_open" or "sell_to_close") && side != "sell")
            throw RiskRejected(intentId, "conflicting_broker_fill_side", "Broker fill side conflicts with its opening/closing intent.");
        if (roles.TryGetValue(orderId, out var role))
        {
            var opening = role is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry;
            if ((opening && positionIntent is "buy_to_close" or "sell_to_close") ||
                (!opening && positionIntent is "buy_to_open" or "sell_to_open"))
                throw RiskRejected(intentId, "conflicting_broker_fill_role", "Broker opening/closing intent conflicts with the locally owned order role.");
            return opening;
        }
        if (positionIntent is "buy_to_open" or "sell_to_open") return true;
        if (positionIntent is "buy_to_close" or "sell_to_close") return false;
        throw RiskRejected(intentId, "unclassified_broker_fill",
            $"The broker order for {symbol} cannot be identified as an entry or exit. Its order needs an explicit position_intent before another stock can be admitted.");
    }

    /// <summary>Refresh broker evidence when a newer owned entry fill exists; cumulative REST repairs cannot establish its first-fill date.</summary>
    private static async Task RequireNoNewerOwnedEntryFillAsync(TradingFlowDbContext context,
        BrokerFillHistorySnapshot history, DateTimeOffset nowUtc, Guid intentId, CancellationToken token)
    {
        var cutoff = history.WindowEndUtc.ToString("O", CultureInfo.InvariantCulture);
        var end = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        var firstDate = history.WindowEndUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lastDate = nowUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var recent = context.PositionEvents.FromSqlInterpolated($"SELECT * FROM position_events WHERE account_id = {history.AccountId} AND substr(broker_timestamp_utc, 1, 10) >= {firstDate} AND substr(broker_timestamp_utc, 1, 10) <= {lastDate} AND julianday(broker_timestamp_utc) > julianday({cutoff}) AND julianday(broker_timestamp_utc) <= julianday({end})");
        var newerEntry = await (from fill in recent.AsNoTracking()
                                join owner in context.OrderIntents.AsNoTracking() on fill.ClientOrderId equals owner.ClientOrderId
                                where owner.Kind == OrderIntentKind.StrategyEntry || owner.Kind == OrderIntentKind.OperatorEntry
                                select fill.PositionEventId).AnyAsync(token);
        if (newerEntry)
            throw RiskRejected(intentId, "fill_history_refresh_required",
                "An owned entry filled after the broker-history cutoff. Refresh complete account fill history before admitting another stock.");
    }

    /// <summary>Before a first broker POST, reject stale/day-old evidence or changed saved limits; adoption is handled before this check.</summary>
    private async Task RequireCurrentEntryCapacityAsync(TradingFlowDbContext context, OrderIntentRecord intent,
        DateTimeOffset nowUtc, CancellationToken token)
    {
        var record = await context.ApplicationEvents.AsNoTracking().Where(row => row.StreamName == EntryCapacityStream(intent.IntentId))
            .OrderByDescending(row => row.EventSequence).FirstOrDefaultAsync(token);
        var evidence = record is null ? null : JsonSerializer.Deserialize<EntryCapacityEvidence>(record.PayloadJson, TradingLimitJson);
        if (evidence is null || evidence.NewYorkDate != TradingLimitDay.At(nowUtc) || evidence.FrozenThroughUtc > nowUtc ||
            nowUtc - evidence.FrozenThroughUtc > TimeSpan.FromSeconds(evidence.MaximumAgeSeconds))
            throw RiskRejected(intent.IntentId, "entry_capacity_evidence_expired",
                "The account fill evidence used for this entry expired or belongs to a previous New York day. Refresh and review the entry again.");
        var limits = await SqliteOperationalTradingLimitStore.ReadAsync(context, evidence.Mode, tradingLimitDefaults, token);
        if (limits.Revision != evidence.ProfileRevision || limits.MaxNewStocksPerDay != evidence.MaxNewStocksPerDay ||
            limits.MaxOpenPositions != evidence.MaxOpenPositions)
            throw RiskRejected(intent.IntentId, "trading_limits_changed", "Saved trading limits changed after this entry was reviewed. Review it again before dispatch.");
    }

    /// <summary>Bind one compact capacity assessment to its immutable entry intent without creating another persistence table.</summary>
    private static string EntryCapacityStream(Guid intentId) => "operational-entry-capacity:" + intentId.ToString("N");

    private sealed record EntryCapacityEvidence(string Mode, long ProfileRevision, int MaxNewStocksPerDay, int MaxOpenPositions, DateOnly NewYorkDate,
        DateTimeOffset FrozenThroughUtc, int MaximumAgeSeconds, IReadOnlyList<string> EnteredSymbols);
}
