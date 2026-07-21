using System.Collections.Generic;
using System.Threading;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Abstractions;

public interface ITradeUpdateStreamer : IDisposable
{
    /// <summary>
    /// Connects, authenticates, and subscribes before the caller permits entries.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Yields authoritative account trade updates after a successful connection.
    /// </summary>
    IAsyncEnumerable<OrderUpdate> ReadUpdatesAsync(CancellationToken cancellationToken);
}
