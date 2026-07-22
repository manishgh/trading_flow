using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Execution;

public sealed class PseudoBroker : IBrokerClient
{
    private readonly ConcurrentDictionary<string, BrokerEntryOrder> _activeOrders = new();
    private readonly ConcurrentDictionary<string, ProtectiveStopOrder> _protectiveOrders = new();

    public Task<BrokerOrderReceipt> SubmitOrderAsync(BrokerEntryOrder order, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid().ToString("N");
        _activeOrders.TryAdd(orderId, order);

        // Simulate network latency
        return Task.FromResult(new BrokerOrderReceipt(orderId, DateTimeOffset.UtcNow));
    }

    public Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(
        ProtectiveStopOrder order,
        CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid().ToString("N");
        _protectiveOrders.TryAdd(orderId, order);
        return Task.FromResult(new BrokerOrderReceipt(orderId, DateTimeOffset.UtcNow));
    }

    public Task<AssetTradingEligibility?> GetEligibilityAsync(
        string symbol,
        CancellationToken cancellationToken) =>
        Task.FromResult<AssetTradingEligibility?>(
            new AssetTradingEligibility(
                symbol.Trim().ToUpperInvariant(),
                true,
                true,
                true,
                DateTimeOffset.UtcNow));

    public Task<TradingSessionSnapshot> GetSessionAsync(
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            new TradingSessionSnapshot(
                DateOnly.FromDateTime(timestampUtc.UtcDateTime),
                EquityTradingSession.Regular,
                DateTimeOffset.UtcNow,
                null,
                null));

    public Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var orders = _protectiveOrders.Select(item => new ActiveBrokerOrder(
            item.Key,
            item.Value.Ticker,
            item.Value.Side,
            "new",
            "stop",
            null,
            item.Value.StopPrice,
            item.Value.Quantity,
            now,
            item.Value.ClientOrderId,
            0m,
            null,
            now)).ToArray();
        return Task.FromResult<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>>(orders);
    }

    public Task<TradingFlow.Domain.Orders.ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        var order = _activeOrders
            .Select(item => (item.Key, item.Value))
            .SingleOrDefault(item => String.Equals(item.Value.Order.ClientOrderId, clientOrderId, StringComparison.Ordinal));
        if (order.Value is null)
        {
            var protective = _protectiveOrders
                .Select(item => (item.Key, item.Value))
                .SingleOrDefault(item => String.Equals(item.Value.ClientOrderId, clientOrderId, StringComparison.Ordinal));
            if (protective.Value is null)
            {
                return Task.FromResult<TradingFlow.Domain.Orders.ActiveBrokerOrder?>(null);
            }

            var protectiveNow = DateTimeOffset.UtcNow;
            return Task.FromResult<ActiveBrokerOrder?>(new ActiveBrokerOrder(
                protective.Key,
                protective.Value.Ticker,
                protective.Value.Side,
                "new",
                "stop",
                null,
                protective.Value.StopPrice,
                protective.Value.Quantity,
                protectiveNow,
                protective.Value.ClientOrderId,
                0m,
                null,
                protectiveNow));
        }

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<TradingFlow.Domain.Orders.ActiveBrokerOrder?>(new TradingFlow.Domain.Orders.ActiveBrokerOrder(
            order.Key,
            order.Value.Order.Ticker,
            order.Value.Side,
            "new",
            order.Value.OrderType,
            order.Value.Order.LimitPrice,
            order.Value.Order.StopLossPrice,
            order.Value.Order.ShareQuantity,
            order.Value.Order.ExecutionTimestamp,
            order.Value.Order.ClientOrderId,
            0m,
            null,
            now));
    }

    public Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.BrokerPosition>>(Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>());
    }

    public Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken)
    {
        return Task.FromResult(new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") });
    }

    public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            _activeOrders.TryRemove(orderId, out _) ||
            _protectiveOrders.TryRemove(orderId, out _));
    }

    public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken)
    {
        _activeOrders.Clear();
        _protectiveOrders.Clear();
        return Task.FromResult(true);
    }

    public Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken)
    {
        // PseudoBroker doesn't currently model positions natively in memory, so just return true
        return Task.FromResult(true);
    }

    public Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken)
    {
        // PseudoBroker doesn't currently model positions natively in memory, so just return true
        return Task.FromResult(quantity > 0);
    }

    public Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken)
    {
        if (_activeOrders.TryGetValue(orderId, out var existingOrder))
        {
            var updatedOrder = existingOrder.Order with
            {
                StopLossPrice = newStopLoss,
                TakeProfitPrice = newTakeProfit
            };
            _activeOrders[orderId] = existingOrder with { Order = updatedOrder };
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    // Helper for local simulation: evaluate tick data against active orders to simulate bracket fills mid-bar
    public void EvaluateTick(string ticker, decimal price, DateTimeOffset timestamp)
    {
        foreach (var kvp in _activeOrders)
        {
            var order = kvp.Value.Order;
            if (!order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)) continue;

            if (price <= order.StopLossPrice || price >= order.TakeProfitPrice)
            {
                // Triggered! Remove the order from active status
                _activeOrders.TryRemove(kvp.Key, out _);
                // In a full implementation, this would emit an "OrderFilled" event back to the portfolio
            }
        }
    }

    public async Task StartStreamingAsync(
        TradingFlow.Engine.Abstractions.IMarketDataStreamer streamer,
        string[] tickers,
        CancellationToken cancellationToken)
    {
        await foreach (var bar in streamer.SubscribeBarsAsync(tickers, cancellationToken))
        {
            // Evaluate High/Low for stop loss and take profit hits
            EvaluateTick(bar.Ticker, bar.High, bar.Timestamp);
            EvaluateTick(bar.Ticker, bar.Low, bar.Timestamp);
            EvaluateTick(bar.Ticker, bar.Close, bar.Timestamp);
        }
    }
}
