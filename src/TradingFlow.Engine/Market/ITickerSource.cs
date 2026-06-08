using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Engine.Market;

/// <summary>
/// Defines a source that can dynamically fetch a list of tickers to be consumed by the pipeline.
/// </summary>
public interface ITickerSource
{
    /// <summary>
    /// Gets the name of the source (e.g., "FinvizScreener", "CsvFile").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Fetches the list of tickers.
    /// </summary>
    Task<IReadOnlyList<string>> GetTickersAsync(CancellationToken cancellationToken = default);
}
