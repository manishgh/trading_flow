using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Catalysts;

public sealed class CatalystStreamer
{
    private readonly ICatalystProvider? _provider;

    public CatalystStreamer(ICatalystProvider? provider)
    {
        _provider = provider;
    }

    public async Task<IReadOnlyList<CatalystEvent>> LoadTickerCatalystsAsync(
        string ticker, 
        DateTimeOffset windowStart, 
        DateTimeOffset windowEnd, 
        CancellationToken cancellationToken)
    {
        if (_provider == null)
        {
            return Array.Empty<CatalystEvent>();
        }

        return await _provider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
    }
}
