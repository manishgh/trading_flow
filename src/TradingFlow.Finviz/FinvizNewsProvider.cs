using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Finviz;

public sealed class FinvizNewsProvider : ICatalystProvider
{
    private readonly FinvizClient _finvizClient;

    public FinvizNewsProvider(FinvizClient finvizClient)
    {
        _finvizClient = finvizClient;
    }

    public string ProviderName => "finviz";

    public Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        // Finviz Elite only provides a CSV export API for Screener tables (e.g., /export).
        // It does NOT provide a JSON/CSV API for Historical News without HTML scraping.
        throw new NotSupportedException("Finviz does not provide a raw API for News. Please use Alpaca instead, or enable HTML Scraping.");
    }
}
