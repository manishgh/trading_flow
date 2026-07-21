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
/// Enforces EXE-09 independently of strategy logic. A missing stop is priced from the
/// original structural stop when available, otherwise from point-in-time daily ATR.
/// </summary>
public sealed class ProtectiveOrderInvariantService(
    IOrderIntentRepository intents,
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
                var quantity = Math.Abs(position.Qty) - coverage;
                if (quantity != Decimal.Truncate(quantity))
                {
                    throw new InvalidOperationException(
                        "Alpaca does not support fractional-quantity GTC stop orders.");
                }

                var stopPrice = await ResolveStopPriceAsync(position, cancellationToken);
                var now = timeProvider.GetUtcNow();
                var run = runContext.Run;
                var result = await submissions.SubmitProtectiveStopAsync(
                    new ProtectiveStopSubmission(
                        Guid.NewGuid(),
                        new ExecutionRunContext(
                            run.RunId,
                            run.Profile,
                            run.ConfigHash,
                            run.CodeVersion,
                            run.StartedAtUtc),
                        symbol,
                        OppositeOrderSide(position),
                        quantity,
                        stopPrice,
                        ExecutionRunContextFactory.ResolveSessionDate(now, ExchangeTimezone),
                        now),
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
                    "EXE-09 protection could not be restored for {Symbol}.",
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

    private async Task<decimal> ResolveStopPriceAsync(
        BrokerPosition brokerPosition,
        CancellationToken cancellationToken)
    {
        var symbol = NormalizeSymbol(brokerPosition.Ticker);
        var localPosition = await positions.GetCurrentAsync(symbol, cancellationToken);
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
}
