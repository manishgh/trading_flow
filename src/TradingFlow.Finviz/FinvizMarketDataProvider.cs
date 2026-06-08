using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Finviz;

public sealed class FinvizMarketDataProvider : IMarketDataProvider
{
    private readonly FinvizClient _finvizClient;

    public FinvizMarketDataProvider(FinvizClient finvizClient)
    {
        _finvizClient = finvizClient;
    }

    public IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        // Finviz Elite only provides a CSV export API for Screener tables (e.g., /export).
        // It does NOT provide a JSON/CSV API for Historical OHLCV without HTML scraping.
        throw new NotSupportedException("Finviz does not provide a raw API for Historical OHLCV Bars. Please use Alpaca instead.");
    }
}
