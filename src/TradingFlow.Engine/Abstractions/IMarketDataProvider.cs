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

/// <summary>
/// Declares the provider's omission semantics. Alpaca's authoritative stock-bar
/// response omits an interval when no qualifying trade occurred; an arbitrary
/// file or adapter must not inherit that assumption implicitly.
/// </summary>
public interface IMarketDataCompletenessProvider
{
    bool OmittedSubDailyIntervalsMeanNoQualifyingTrades { get; }
}
