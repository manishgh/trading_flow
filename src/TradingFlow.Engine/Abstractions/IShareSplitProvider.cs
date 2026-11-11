using TradingFlow.Engine.Universe;

namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// A provider that can list each stock's share splits (ex-date and ratio) from a frozen corporate-actions dataset. A research
/// screen backtest on as-traded prices reads it to close a held position before a split changes its share basis.
/// </summary>
public interface IShareSplitProvider
{
    /// <summary>Every recorded split by symbol (upper case); an empty map when the dataset lists none.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>> LoadShareSplitsAsync(CancellationToken cancellationToken);
}
