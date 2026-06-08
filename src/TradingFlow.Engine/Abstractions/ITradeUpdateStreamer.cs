using System.Collections.Generic;
using System.Threading;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Abstractions;

public interface ITradeUpdateStreamer
{
    /// <summary>
    /// Subscribes to real-time trade execution updates from the broker.
    /// Yields incoming OrderUpdates continuously until cancellation.
    /// </summary>
    IAsyncEnumerable<OrderUpdate> SubscribeTradeUpdatesAsync(CancellationToken cancellationToken);
}
