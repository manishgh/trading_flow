namespace TradingFlow.Engine.Abstractions;

/// <summary>One stock's most recent trade as the provider reported it.</summary>
public sealed record LatestTrade(string Ticker, decimal Price, DateTimeOffset TradedAtUtc);

/// <summary>
/// Reads each stock's most recent trade. For display only: decisions use completed bars, so a price from the
/// middle of an unfinished bar never reaches an assessment.
/// </summary>
public interface ILatestTradeProvider
{
    /// <summary>
    /// Returns the latest trade for each requested stock that has one, keyed by the requested ticker without
    /// regard to letter case. Stocks without a trade are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, LatestTrade>> GetLatestTradesAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);
}
