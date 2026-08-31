using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Execution;

public sealed record ProtectiveOrderOptions
{
    public ProtectiveOrderOptions(decimal backstopAtrMultiple = 1.5m)
    {
        if (backstopAtrMultiple is < 1m or > 3m)
        {
            throw new ArgumentOutOfRangeException(nameof(backstopAtrMultiple));
        }

        BackstopAtrMultiple = backstopAtrMultiple;
    }

    public decimal BackstopAtrMultiple { get; }
}

public sealed record ProtectiveOrderRepair(
    string Symbol,
    bool Succeeded,
    string Detail,
    string? BrokerOrderId = null);

public interface IProtectiveOrderInvariantService
{
    Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
        IBrokerClient broker,
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
    ILogger<ProtectiveOrderInvariantService> logger) : IProtectiveOrderInvariantService
{
    private const int AtrWarmupCalendarDays = 120;
    private static readonly string ExchangeTimezone = "America/New_York";

    public async Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
        IBrokerClient broker,
        IReadOnlyList<BrokerPosition> brokerPositions,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(brokerPositions);
        ArgumentNullException.ThrowIfNull(openOrders);
        var repairs = new List<ProtectiveOrderRepair>();
        foreach (var position in brokerPositions.Where(item => item.Qty != 0m))
        {
            var symbol = NormalizeSymbol(position.Ticker);
            var effectiveOrders = openOrders.ToList();
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

                    var canceled = await broker.CancelOrderAsync(redundant.OrderId, cancellationToken);
                    if (!canceled)
                    {
                        repairs.Add(new ProtectiveOrderRepair(
                            symbol,
                            false,
                            $"Could not cancel redundant backstop {redundant.OrderId}."));
                        break;
                    }

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

                var localPosition = await positions.GetCurrentAsync(symbol, cancellationToken);
                var positionGenerationIdentity = localPosition is not null
                    ? $"ledger:{localPosition.PositionEventId}:{localPosition.LatestClientOrderId}"
                    : FormattableString.Invariant(
                        $"broker:{position.Side}:{position.EntryPrice:G29}:{position.Qty:G29}");
                var now = timeProvider.GetUtcNow();
                var run = runContext.Run;
                var protectiveSide = OppositeOrderSide(position);
                var owner = await ResolveProtectionOwnerAsync(
                    broker,
                    position,
                    symbol,
                    protectiveSide,
                    positionGenerationIdentity,
                    effectiveOrders,
                    cancellationToken);
                var intentId = owner.IntentId;
                var existingIntent = owner.Intent;
                if (existingIntent is not null &&
                    (existingIntent.Kind != OrderIntentKind.ProtectiveStop ||
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

    internal static bool HasProtectiveCoverage(
        BrokerPosition position,
        IReadOnlyList<ActiveBrokerOrder> openOrders) =>
        ProtectiveCoverage(position, openOrders) >= Math.Abs(position.Qty);

    internal static decimal ProtectiveCoverage(
        BrokerPosition position,
        IReadOnlyList<ActiveBrokerOrder> openOrders) =>
        openOrders
            .Where(order => IsProtectiveFor(position, order))
            .Sum(RemainingQuantity);

    private async Task<ProtectionOwner> ResolveProtectionOwnerAsync(
        IBrokerClient broker,
        BrokerPosition expectedPosition,
        string symbol,
        string side,
        string positionGenerationIdentity,
        IReadOnlyList<ActiveBrokerOrder> openOrders,
        CancellationToken cancellationToken)
    {
        for (var revision = 0; revision < 10_000; revision++)
        {
            var intentId = ProtectiveOrderIntentIdFactory.Create(
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
            !String.IsNullOrWhiteSpace(localPosition.LatestClientOrderId))
        {
            var intent = await intents.GetByClientOrderIdAsync(
                localPosition.LatestClientOrderId,
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
