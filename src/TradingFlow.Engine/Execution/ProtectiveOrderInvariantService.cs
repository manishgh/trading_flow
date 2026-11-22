using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Execution;

public sealed record ProtectiveOrderOptions
{
    public ProtectiveOrderOptions(
        decimal backstopAtrMultiple = 1.5m,
        int restProjectionLagToleranceSeconds = 30)
    {
        if (backstopAtrMultiple is < 1m or > 3m)
        {
            throw new ArgumentOutOfRangeException(nameof(backstopAtrMultiple));
        }

        BackstopAtrMultiple = backstopAtrMultiple;
        if (restProjectionLagToleranceSeconds is < 5 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(restProjectionLagToleranceSeconds));
        }

        RestProjectionLagTolerance = TimeSpan.FromSeconds(restProjectionLagToleranceSeconds);
    }

    public decimal BackstopAtrMultiple { get; }
    public TimeSpan RestProjectionLagTolerance { get; }
}

public sealed record ProtectiveOrderRepair(
    string Symbol,
    bool Succeeded,
    string Detail,
    string? BrokerOrderId = null);

public interface IProtectiveOrderInvariantService
{
    // A read-only release proof must be explicitly implemented; repair success alone is insufficient.
    Task<bool> HasVerifiedCoverageAsync(IBrokerClient broker, string accountId, BrokerPosition position,
        IReadOnlyList<ActiveBrokerOrder> openOrders, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
        IBrokerClient broker,
        string accountId,
        IReadOnlyList<BrokerPosition> brokerPositions,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Enforces broker-side position protection independently of strategy logic. A missing stop is priced from the
/// original structural stop when available, otherwise from point-in-time daily ATR.
/// </summary>
public sealed class ProtectiveOrderInvariantService(
    IOrderIntentRepository intents,
    IOrderEventRepository orderEvents,
    IPositionLedgerRepository positions,
    IOrderSubmissionService submissions,
    Lazy<IMarketDataProvider> marketData,
    IndicatorEngine indicators,
    ProtectiveOrderOptions options,
    ReconciliationRunContext runContext,
    TimeProvider timeProvider,
    ILogger<ProtectiveOrderInvariantService> logger,
    IProtectiveStopReplacementRepository? stopReplacements = null) : IProtectiveOrderInvariantService
{
    private const int AtrWarmupCalendarDays = 120;
    private static readonly string ExchangeTimezone = "America/New_York";

    public async Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
        IBrokerClient broker,
        string accountId,
        IReadOnlyList<BrokerPosition> brokerPositions,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (String.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("Broker account ID is required.", nameof(accountId));
        }
        ArgumentNullException.ThrowIfNull(brokerPositions);
        ArgumentNullException.ThrowIfNull(openOrders);
        var repairs = new List<ProtectiveOrderRepair>();
        var effectivePositions = brokerPositions
            .Where(item => item.Qty != 0m)
            .ToDictionary(item => NormalizeSymbol(item.Ticker), StringComparer.Ordinal);

        // The local ledger is advanced atomically with every accepted stream/REST fill.
        // Alpaca's position REST projection may lag that fill, so protection must use
        // the locally committed quantity as the minimum exposure, never as a reason to wait.
        var localPositions = await positions.ListCurrentAsync(accountId, cancellationToken) ?? [];
        foreach (var local in localPositions.Where(item => item.Quantity != 0m))
        {
            var symbol = NormalizeSymbol(local.Symbol);
            var hasBrokerPosition = effectivePositions.TryGetValue(symbol, out var brokerPosition);
            if (hasBrokerPosition && Math.Abs(brokerPosition!.Qty) >= Math.Abs(local.Quantity))
            {
                continue;
            }

            var latestFillIncreasedExposure =
                local.Quantity > 0m && local.LatestFillSide.Equals("buy", StringComparison.OrdinalIgnoreCase) ||
                local.Quantity < 0m && local.LatestFillSide.Equals("sell", StringComparison.OrdinalIgnoreCase);
            var fillIsInsideRestLagWindow =
                timeProvider.GetUtcNow() - local.LocalTimestampUtc.ToUniversalTime() <= options.RestProjectionLagTolerance;
            if (!latestFillIncreasedExposure || !fillIsInsideRestLagWindow)
            {
                // A smaller broker position paired with an old or reducing local
                // event means the broker quantity is the safe upper bound. Using
                // stale local size could create a reversing stop order.
                continue;
            }

            var side = local.Quantity > 0m ? "long" : "short";
            effectivePositions[symbol] = new BrokerPosition(
                symbol,
                side,
                Math.Abs(local.Quantity),
                local.LatestFillPrice,
                local.LatestFillPrice,
                0m);
        }

        foreach (var position in effectivePositions.Values.OrderBy(item => item.Ticker, StringComparer.Ordinal))
        {
            var symbol = NormalizeSymbol(position.Ticker);
            var localPosition = await positions.GetCurrentAsync(
                accountId,
                symbol,
                cancellationToken);
            if (localPosition is not null &&
                await intents.HasActivePositionExitAsync(
                    accountId,
                    symbol,
                    localPosition.PositionGenerationEventId,
                    cancellationToken))
            {
                repairs.Add(new ProtectiveOrderRepair(
                    symbol,
                    true,
                    $"Protection reconciliation deferred while a durable exit owns position generation {localPosition.PositionGenerationEventId}."));
                continue;
            }

            var positionGenerationIdentity = localPosition is not null
                ? $"ledger:{localPosition.PositionGenerationEventId}:{localPosition.PositionGenerationClientOrderId}"
                : FormattableString.Invariant(
                    $"broker:{position.Side}:{position.EntryPrice:G29}:{position.Qty:G29}");
            if (openOrders.GroupBy(order => order.OrderId, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
                openOrders.GroupBy(order => order.ClientOrderId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                repairs.Add(new ProtectiveOrderRepair(symbol, false, "Duplicate broker order identities prevent protection verification."));
                continue;
            }
            var effectiveOrders = await ListOwnedProtectiveOrdersAsync(
                broker,
                accountId,
                position,
                positionGenerationIdentity,
                openOrders,
                cancellationToken);
            // An uncertain native stop may still execute. Never add a competing backstop
            // merely because its parent proof is incomplete or the stop is being replaced.
            if (openOrders.Any(order => IsPotentialClosingStop(position, order) &&
                    !String.IsNullOrWhiteSpace(order.ParentClientOrderId) &&
                    !effectiveOrders.Any(owned => owned.OrderId == order.OrderId)))
            {
                repairs.Add(new ProtectiveOrderRepair(symbol, false,
                    "Native linked stop ownership or executable coverage is unresolved; group preserved for review."));
                continue;
            }
            var coverage = ProtectiveCoverage(position, effectiveOrders);
            if (coverage > Math.Abs(position.Qty))
            {
                foreach (var redundant in effectiveOrders
                             .Where(order => IsProtectiveFor(position, order) && IsBackstop(order))
                             .OrderByDescending(order => order.CreatedAt))
                {
                    var remaining = coverage - RemainingQuantity(redundant);
                    if (remaining < Math.Abs(position.Qty))
                    {
                        continue;
                    }

                    await submissions.RequestCancelAsync(
                        new OrderCancellationSubmission(
                            redundant.ParentClientOrderId ?? redundant.ClientOrderId,
                            redundant.OrderId,
                            "redundant_protective_backstop",
                            timeProvider.GetUtcNow().ToUniversalTime()),
                        broker,
                        cancellationToken);

                    coverage = remaining;
                    effectiveOrders.Remove(redundant);
                    repairs.Add(new ProtectiveOrderRepair(
                        symbol,
                        true,
                        $"Canceled redundant backstop {redundant.OrderId}; remaining stop coverage={coverage}."));
                }
            }

            if (coverage >= Math.Abs(position.Qty))
            {
                continue;
            }

            try
            {
                var currentCoverageDeficit = Math.Abs(position.Qty) - coverage;
                if (currentCoverageDeficit != Decimal.Truncate(currentCoverageDeficit))
                {
                    throw new InvalidOperationException(
                        "Alpaca does not support fractional-quantity GTC stop orders.");
                }

                var now = timeProvider.GetUtcNow();
                var run = runContext.Run;
                var protectiveSide = OppositeOrderSide(position);
                var owner = await ResolveProtectionOwnerAsync(
                    broker,
                    position,
                    accountId,
                    symbol,
                    protectiveSide,
                    positionGenerationIdentity,
                    effectiveOrders,
                    cancellationToken);
                var intentId = owner.IntentId;
                var existingIntent = owner.Intent;
                if (existingIntent is not null &&
                    (existingIntent.Kind != OrderIntentKind.ProtectiveStop ||
                     !existingIntent.AccountId.Equals(accountId, StringComparison.Ordinal) ||
                     !existingIntent.Symbol.Equals(symbol, StringComparison.Ordinal) ||
                     !existingIntent.Side.Equals(protectiveSide, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Protective owner {intentId:N} does not match position generation '{positionGenerationIdentity}'.");
                }

                // Once a position generation owns a protective intent, every retry
                // reuses its immutable coverage request. Broker/order reconciliation
                // must resolve that owner before later market snapshots may resize it.
                var quantity = existingIntent?.RequestedQuantity ?? currentCoverageDeficit;
                var stopPrice = existingIntent?.StopPrice ??
                    await ResolveStopPriceAsync(position, localPosition, cancellationToken);
                if (quantity != Decimal.Truncate(quantity))
                {
                    throw new InvalidOperationException(
                        "Alpaca does not support fractional-quantity GTC stop orders.");
                }

                var result = await submissions.SubmitProtectiveStopAsync(
                    new ProtectiveStopSubmission(
                        intentId,
                        new ExecutionRunContext(
                            run.RunId,
                            run.Profile,
                            run.ConfigHash,
                            run.CodeVersion,
                            run.StartedAtUtc),
                        accountId,
                        symbol,
                        protectiveSide,
                        quantity,
                        stopPrice,
                        existingIntent?.SessionDate ??
                            ExecutionRunContextFactory.ResolveSessionDate(
                                localPosition?.BrokerTimestampUtc ?? now,
                                ExchangeTimezone),
                        existingIntent?.CreatedAtUtc ?? now,
                        positionGenerationIdentity,
                        owner.Revision),
                    broker,
                    cancellationToken);
                repairs.Add(new ProtectiveOrderRepair(
                    symbol,
                    true,
                    $"Placed GTC backstop at {stopPrice} for {quantity} share(s).",
                    result.BrokerOrderId));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogCritical(
                    exception,
                    "Broker-side protection could not be restored for {Symbol}.",
                    symbol);
                repairs.Add(new ProtectiveOrderRepair(symbol, false, exception.Message));
            }
        }

        return repairs;
    }

    // Resolve standalone protection and native bracket legs against their immutable position owner.
    private async Task<List<ActiveBrokerOrder>> ListOwnedProtectiveOrdersAsync(
        IBrokerClient broker,
        string accountId,
        BrokerPosition position,
        string positionGenerationIdentity,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken)
    {
        var owned = new List<ActiveBrokerOrder>();
        foreach (var order in openOrders.Where(order => IsProtectiveFor(position, order)))
        {
            var ownerClientOrderId = order.ParentClientOrderId ?? order.ClientOrderId;
            var intent = await intents.GetByClientOrderIdAsync(ownerClientOrderId, cancellationToken);
            if (intent is null && stopReplacements is not null)
            {
                var replacement = await stopReplacements.GetVerifiedByReplacementClientOrderIdAsync(
                    order.ClientOrderId,
                    cancellationToken);
                if (replacement is not null)
                {
                    ownerClientOrderId = replacement.OwnerClientOrderId;
                    intent = await intents.GetByClientOrderIdAsync(
                        ownerClientOrderId,
                        cancellationToken);
                }
            }

            if (intent is null)
            {
                continue;
            }

            if (intent.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry)
            {
                if (order.Status.Trim().ToLowerInvariant() is "new" or "partially_filled" or "accepted_for_bidding" &&
                    await IsOwnedNativeStopAsync(broker, accountId, position, positionGenerationIdentity,
                        order, intent, cancellationToken))
                {
                    owned.Add(order);
                }
                continue;
            }

            if (intent.Kind != OrderIntentKind.ProtectiveStop ||
                !intent.AccountId.Equals(accountId, StringComparison.Ordinal) ||
                !intent.Symbol.Equals(NormalizeSymbol(position.Ticker), StringComparison.Ordinal) ||
                !intent.Side.Equals(OppositeOrderSide(position), StringComparison.OrdinalIgnoreCase) ||
                intent.RequestedQuantity != order.Qty)
            {
                continue;
            }

            var state = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken);
            if (state is null || state.BrokerOrderId != order.OrderId || OrderStateMachine.IsTerminal(state.State) ||
                !IntentOwnsPositionGeneration(intent, positionGenerationIdentity))
            {
                continue;
            }

            owned.Add(order);
        }

        return owned;
    }

    // Prove exact resting coverage without placing or canceling orders. The caller obtains the
    // surrounding fresh snapshots; account and ledger generation are checked again here.
    public async Task<bool> HasVerifiedCoverageAsync(IBrokerClient broker, string accountId,
        BrokerPosition position, IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default)
    {
        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        if (!String.Equals(account.AccountId, accountId, StringComparison.Ordinal) || position.Qty == 0m ||
            openOrders.GroupBy(order => order.OrderId, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            openOrders.GroupBy(order => order.ClientOrderId, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            return false;
        }
        var local = await positions.GetCurrentAsync(accountId, NormalizeSymbol(position.Ticker), cancellationToken);
        var signedQuantity = position.Side.Equals("long", StringComparison.OrdinalIgnoreCase)
            ? Math.Abs(position.Qty) : position.Side.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? -Math.Abs(position.Qty) : 0m;
        if (local is null || signedQuantity == 0m || local.Quantity != signedQuantity ||
            await intents.HasActivePositionExitAsync(accountId, local.Symbol, local.PositionGenerationEventId, cancellationToken))
        {
            return false;
        }
        var generation = $"ledger:{local.PositionGenerationEventId}:{local.PositionGenerationClientOrderId}";
        var owned = await ListOwnedProtectiveOrdersAsync(broker, accountId, position, generation, openOrders, cancellationToken);
        foreach (var stop in owned.Where(order => String.IsNullOrWhiteSpace(order.ParentClientOrderId)))
        {
            var ownerId = stop.ClientOrderId;
            if (stopReplacements is not null)
                ownerId = (await stopReplacements.GetVerifiedByReplacementClientOrderIdAsync(ownerId, cancellationToken))?.OwnerClientOrderId ?? ownerId;
            var state = await orderEvents.GetCurrentAsync(ownerId, cancellationToken);
            if (state?.State is not (OrderState.Acked or OrderState.PartiallyFilled)) return false;
        }
        var stops = owned.Where(order => order.Status.Trim().ToLowerInvariant() is
                "new" or "partially_filled" or "accepted_for_bidding")
            .Where(order => String.IsNullOrWhiteSpace(order.ReplacedByOrderId) &&
                order.Qty is > 0m && order.FilledQuantity >= 0m && order.FilledQuantity < order.Qty).ToArray();
        return !openOrders.Any(order => IsPotentialClosingStop(position, order) &&
                   !stops.Any(owned => owned.OrderId == order.OrderId)) &&
               ProtectiveCoverage(position, stops) == Math.Abs(position.Qty);
    }

    // Held children are inactive; every other nonfinal closing stop can still affect exposure.
    private static bool IsPotentialClosingStop(BrokerPosition position, ActiveBrokerOrder order) =>
        NormalizeSymbol(order.Ticker) == NormalizeSymbol(position.Ticker) &&
        order.Side.Equals(OppositeOrderSide(position), StringComparison.OrdinalIgnoreCase) &&
        order.OrderType.Trim().ToLowerInvariant() is "stop" or "stop_limit" or "trailing_stop" &&
        order.Status.Trim().ToLowerInvariant() is not ("held" or "filled" or "canceled" or "expired" or "rejected");



    // A live nested stop inherits ownership only from the exact fully filled entry that opened
    // this ledger generation. Held children, unrelated groups and replaced parents cannot qualify.
    private async Task<bool> IsOwnedNativeStopAsync(IBrokerClient broker, string accountId,
        BrokerPosition position, string generation, ActiveBrokerOrder stop, OrderIntentRecord intent,
        CancellationToken cancellationToken)
    {
        var local = await positions.GetCurrentAsync(accountId, NormalizeSymbol(position.Ticker), cancellationToken);
        if (local is null || generation != $"ledger:{local.PositionGenerationEventId}:{local.PositionGenerationClientOrderId}" ||
            local.PositionGenerationClientOrderId != intent.ClientOrderId || stop.ParentClientOrderId != intent.ClientOrderId ||
            intent.AccountId != accountId || intent.Symbol != NormalizeSymbol(position.Ticker) ||
            !intent.Side.Equals(position.Side.Equals("long", StringComparison.OrdinalIgnoreCase) ? "buy" : "sell", StringComparison.OrdinalIgnoreCase) ||
            !String.IsNullOrWhiteSpace(stop.ReplacedByOrderId) || stop.OrderClass?.ToLowerInvariant() is not ("bracket" or "oto") ||
            stop.OrderType.Trim().ToLowerInvariant() is not ("stop" or "stop_limit") ||
            stop.Qty is not > 0m || stop.FilledQuantity < 0m || stop.FilledQuantity >= stop.Qty ||
            stop.GroupRole is not null && stop.GroupRole != "stop_loss" ||
            stop.PositionIntent is not null && !stop.PositionIntent.Equals(
                position.Side.Equals("long", StringComparison.OrdinalIgnoreCase) ? "sell_to_close" : "buy_to_close", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var parent = await broker.GetOrderByClientOrderIdAsync(intent.ClientOrderId, cancellationToken);
        var journal = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken);
        return parent is not null && journal is not null && journal.State == OrderState.Filled &&
            parent.ClientOrderId == intent.ClientOrderId && parent.OrderId == journal.BrokerOrderId &&
            stop.ParentOrderId == parent.OrderId && parent.NestedLegOrderIds?.Count(id => id == stop.OrderId) == 1 &&
            (parent.GroupRole is null || parent.GroupRole == "entry") &&
            (stop.GroupId is null && parent.GroupId is null || stop.GroupId is not null && stop.GroupId == parent.GroupId) &&
            parent.Ticker.Equals(intent.Symbol, StringComparison.OrdinalIgnoreCase) &&
            parent.Side.Equals(intent.Side, StringComparison.OrdinalIgnoreCase) &&
            parent.Status.Equals("filled", StringComparison.OrdinalIgnoreCase) &&
            parent.OrderClass?.ToLowerInvariant() is "bracket" or "oto" &&
            String.IsNullOrWhiteSpace(parent.ParentClientOrderId) && String.IsNullOrWhiteSpace(parent.ReplacedByOrderId) &&
            parent.Qty == intent.RequestedQuantity && parent.FilledQuantity == intent.RequestedQuantity &&
            journal.FilledQuantity == parent.FilledQuantity && RemainingQuantity(stop) <= parent.FilledQuantity;
    }

    private static bool IntentOwnsPositionGeneration(
        OrderIntentRecord intent,
        string expectedPositionGenerationIdentity)
    {
        using var document = System.Text.Json.JsonDocument.Parse(intent.RequestJson);
        return document.RootElement.TryGetProperty("PositionGenerationIdentity", out var identity) &&
               identity.ValueKind == System.Text.Json.JsonValueKind.String &&
               String.Equals(
                   identity.GetString(),
                   expectedPositionGenerationIdentity,
                   StringComparison.Ordinal);
    }

    internal static decimal ProtectiveCoverage(
        BrokerPosition position,
        IReadOnlyList<ActiveBrokerOrder> openOrders) =>
        openOrders
            .Where(order => IsProtectiveFor(position, order))
            .Sum(RemainingQuantity);

    private async Task<ProtectionOwner> ResolveProtectionOwnerAsync(
        IBrokerClient broker,
        BrokerPosition expectedPosition,
        string accountId,
        string symbol,
        string side,
        string positionGenerationIdentity,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken)
    {
        for (var revision = 0; revision < 10_000; revision++)
        {
            var intentId = ProtectiveOrderIntentIdFactory.Create(
                accountId,
                symbol,
                side,
                positionGenerationIdentity,
                revision);
            var intent = await intents.GetByIntentIdAsync(intentId, cancellationToken);
            if (intent is null)
            {
                return new ProtectionOwner(intentId, revision, null);
            }

            if (intent.Kind != OrderIntentKind.ProtectiveStop ||
                !intent.AccountId.Equals(accountId, StringComparison.Ordinal) ||
                !intent.Symbol.Equals(symbol, StringComparison.Ordinal) ||
                !intent.Side.Equals(side, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Protective owner {intentId:N} does not match position generation '{positionGenerationIdentity}'.");
            }

            var state = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Protective owner {intent.ClientOrderId} has no lifecycle state.");
            if (state.State == OrderState.Filled)
            {
                var brokerOrder = await broker.GetOrderByClientOrderIdAsync(
                    intent.ClientOrderId,
                    cancellationToken);
                var brokerConfirmsFill = brokerOrder is not null &&
                    brokerOrder.Status.Equals("filled", StringComparison.OrdinalIgnoreCase);
                var refreshedPositions = await broker.GetOpenPositionsAsync(cancellationToken) ?? [];
                var refreshedPosition = refreshedPositions
                    .SingleOrDefault(position =>
                        NormalizeSymbol(position.Ticker) == symbol &&
                        position.Side.Equals(expectedPosition.Side, StringComparison.OrdinalIgnoreCase));
                var brokerConfirmsRemainingPosition = refreshedPosition is not null &&
                    Math.Abs(refreshedPosition.Qty) == Math.Abs(expectedPosition.Qty);
                if (!brokerConfirmsFill || !brokerConfirmsRemainingPosition)
                {
                    throw new InvalidOperationException(
                        $"Filled protective owner {intent.ClientOrderId} requires broker-confirmed fill and exact remaining position before replacement.");
                }
            }

            var visibleAtBroker = openOrders.Any(order =>
                order.ClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) &&
                NormalizeSymbol(order.Ticker) == symbol &&
                order.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                IsRestingStop(order));
            if (!OrderStateMachine.IsTerminal(state.State) && !visibleAtBroker)
            {
                return new ProtectionOwner(intentId, revision, intent);
            }
        }

        throw new InvalidOperationException(
            $"Protective replacement history exceeded the supported bound for {symbol}.");
    }

    private async Task<decimal> ResolveStopPriceAsync(
        BrokerPosition brokerPosition,
        PositionLedgerSnapshot? localPosition,
        CancellationToken cancellationToken)
    {
        var symbol = NormalizeSymbol(brokerPosition.Ticker);
        if (localPosition is not null &&
            !String.IsNullOrWhiteSpace(localPosition.PositionGenerationClientOrderId))
        {
            var intent = await intents.GetByClientOrderIdAsync(
                localPosition.PositionGenerationClientOrderId,
                cancellationToken);
            if (intent?.StopPrice is > 0m && IsValidStopSide(brokerPosition, intent.StopPrice.Value))
            {
                return NormalizeStopPrice(intent.StopPrice.Value, brokerPosition.Side);
            }
        }

        var now = timeProvider.GetUtcNow();
        var bars = new List<OhlcvBar>();
        await foreach (var bar in marketData.Value.GetBarsAsync(
            [symbol],
            ["1d"],
            now.AddDays(-AtrWarmupCalendarDays),
            now,
            cancellationToken))
        {
            if (bar.Timestamp < now)
            {
                bars.Add(bar);
            }
        }

        var atr = indicators.Compute(bars.OrderBy(bar => bar.Timestamp).ToArray()).LastOrDefault()?.Atr;
        if (atr is null or <= 0m)
        {
            throw new InvalidOperationException(
                $"A structural stop and a warmed ATR are both unavailable for {symbol}.");
        }

        var isLong = brokerPosition.Side.Equals("long", StringComparison.OrdinalIgnoreCase);
        var anchor = isLong
            ? Math.Min(brokerPosition.EntryPrice, brokerPosition.CurrentPrice)
            : Math.Max(brokerPosition.EntryPrice, brokerPosition.CurrentPrice);
        var calculated = isLong
            ? anchor - (options.BackstopAtrMultiple * atr.Value)
            : anchor + (options.BackstopAtrMultiple * atr.Value);
        if (calculated <= 0m)
        {
            throw new InvalidOperationException(
                $"ATR backstop calculation produced a non-positive price for {symbol}.");
        }

        return NormalizeStopPrice(calculated, brokerPosition.Side);
    }

    private static bool IsRestingStop(ActiveBrokerOrder order) =>
        order.OrderType.Trim().ToLowerInvariant() is "stop" or "stop_limit" or "trailing_stop" &&
        order.StopPrice is > 0m &&
        order.Status.Trim().ToLowerInvariant() is
            "new" or "partially_filled" or "accepted_for_bidding" or "pending_replace";

    private static bool IsProtectiveFor(BrokerPosition position, ActiveBrokerOrder order) =>
        NormalizeSymbol(order.Ticker) == NormalizeSymbol(position.Ticker) &&
        order.Side.Equals(OppositeOrderSide(position), StringComparison.OrdinalIgnoreCase) &&
        IsRestingStop(order);

    private static decimal RemainingQuantity(ActiveBrokerOrder order) =>
        Math.Max(0m, (order.Qty ?? 0m) - order.FilledQuantity);

    private static bool IsBackstop(ActiveBrokerOrder order) =>
        String.IsNullOrWhiteSpace(order.ParentClientOrderId) && String.IsNullOrWhiteSpace(order.ParentOrderId) &&
        order.ClientOrderId.StartsWith("BACKSTOP-", StringComparison.Ordinal);

    private static bool IsValidStopSide(BrokerPosition position, decimal stopPrice) =>
        position.Side.Trim().ToLowerInvariant() switch
        {
            "long" => stopPrice < position.CurrentPrice,
            "short" => stopPrice > position.CurrentPrice,
            _ => false
        };

    private static string OppositeOrderSide(BrokerPosition position) =>
        position.Side.Trim().ToLowerInvariant() switch
        {
            "long" => "sell",
            "short" => "buy",
            _ => throw new InvalidOperationException(
                $"Broker position {position.Ticker} has unsupported side '{position.Side}'.")
        };

    private static decimal NormalizeStopPrice(decimal price, string positionSide)
    {
        var scale = price >= 1m ? 100m : 10_000m;
        return positionSide.Equals("long", StringComparison.OrdinalIgnoreCase)
            ? Decimal.Floor(price * scale) / scale
            : Decimal.Ceiling(price * scale) / scale;
    }

    private static string NormalizeSymbol(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new InvalidOperationException("Position symbol is required.");

    private sealed record ProtectionOwner(
        Guid IntentId,
        int Revision,
        OrderIntentRecord? Intent);
}
