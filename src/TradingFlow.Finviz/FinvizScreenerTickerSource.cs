using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Engine.Market;

namespace TradingFlow.Finviz;

public sealed class FinvizScreenerTickerSource : ITickerSource
{
    private readonly FinvizClient _finvizClient;
    private readonly string _filterQuery;

    public FinvizScreenerTickerSource(FinvizClient finvizClient, string filterQuery)
    {
        _finvizClient = finvizClient;
        _filterQuery = filterQuery;
    }

    public string Name => $"FinvizScreener({_filterQuery})";

    public async Task<IReadOnlyList<string>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        return await _finvizClient.GetScreenerTickersAsync(_filterQuery, cancellationToken);
    }
}
