using System;
using System.Collections.Generic;
using System.Threading;
using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

public interface IMarketDataStreamer
{
    /// <summary>
    /// Subscribes to real-time tick/bar data for the specified tickers.
    /// Yields incoming OhlcvBars continuously until cancellation.
    /// </summary>
    IAsyncEnumerable<OhlcvBar> SubscribeBarsAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);
}
