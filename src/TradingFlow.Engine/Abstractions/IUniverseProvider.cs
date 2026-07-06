using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Engine.Abstractions;

public sealed record UniverseRequest(
    IReadOnlyList<string> ConfiguredTickers,
    UniverseConfig Universe,
    DateTimeOffset AsOfUtc,
    string DailyTimeframe,
    DateTimeOffset? WindowEndUtc = null);

/// <summary>
/// Per-day universe membership: the set of trading days each ticker qualified on, computed
/// with the same no-lookahead rule as the as-of screen (eligibility on day D uses only bars
/// dated before D). A trade is allowed only if its entry day is a member day for that ticker.
/// </summary>
public sealed class UniverseMembership
{
    private readonly IReadOnlyDictionary<string, HashSet<DateOnly>> eligibleDaysByTicker;

    public UniverseMembership(IReadOnlyDictionary<string, HashSet<DateOnly>> eligibleDaysByTicker)
    {
        this.eligibleDaysByTicker = eligibleDaysByTicker;
    }

    public IReadOnlyCollection<string> Tickers => eligibleDaysByTicker.Keys.ToArray();

    public bool IsMember(string ticker, DateOnly day) =>
        eligibleDaysByTicker.TryGetValue(ticker, out var days) && days.Contains(day);
}

public sealed record UniverseSelection(
    string Ticker,
    decimal LastClose,
    decimal AverageDollarVolume,
    decimal? PriorReturnPct);

public sealed record UniverseResolution(
    IReadOnlyList<string> Tickers,
    string Source,
    DateOnly AsOfDate,
    IReadOnlyList<UniverseSelection> Selected,
    IReadOnlyList<string> Rejections);

/// <summary>
/// Resolves the tradable ticker universe for a run. The whole point of a non-static
/// provider is the no-lookahead contract: a ticker may only enter the universe using
/// market data available strictly before the evaluation window begins.
/// </summary>
public interface IUniverseProvider
{
    Task<UniverseResolution> ResolveAsync(UniverseRequest request, CancellationToken cancellationToken);
}
