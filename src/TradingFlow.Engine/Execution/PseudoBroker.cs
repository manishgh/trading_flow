using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Execution;

public sealed class PseudoBroker : IBrokerClient
{
    private readonly ConcurrentDictionary<string, FinalizedOrder> _activeOrders = new();

    public Task<string> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid().ToString("N");
        _activeOrders.TryAdd(orderId, order);
        
        // Simulate network latency
        return Task.FromResult(orderId);
    }

    public Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>>(Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>());
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
        return Task.FromResult(_activeOrders.TryRemove(orderId, out _));
    }

    public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken)
    {
        _activeOrders.Clear();
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
            var updated = existingOrder with 
            { 
                StopLossPrice = newStopLoss, 
                TakeProfitPrice = newTakeProfit 
            };
            _activeOrders[orderId] = updated;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    // Helper for local simulation: evaluate tick data against active orders to simulate bracket fills mid-bar
    public void EvaluateTick(string ticker, decimal price, DateTimeOffset timestamp)
    {
        foreach (var kvp in _activeOrders)
        {
            var order = kvp.Value;
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
