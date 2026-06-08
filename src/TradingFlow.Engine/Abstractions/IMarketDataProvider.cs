using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

public interface IMarketDataProvider
{
    IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken);
}

