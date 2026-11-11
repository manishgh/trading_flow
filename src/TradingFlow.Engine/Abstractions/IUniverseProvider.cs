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

    /// <summary>The days a ticker qualified on, in date order; empty when it never qualified. Used to publish the audit record.</summary>
    public IReadOnlyList<DateOnly> EligibleDays(string ticker) =>
        eligibleDaysByTicker.TryGetValue(ticker, out var days) ? days.Order().ToArray() : [];

    /// <summary>Each session's ranked list when the run ranks its daily-eligible stocks; empty otherwise.</summary>
    public IReadOnlyList<TradingFlow.Engine.Universe.SessionRanking> SessionRankings { get; init; } = [];

    /// <summary>
    /// Earliest New York time of day at which a session's membership is known; null when it is decided before the session
    /// opens. A ranking by the first five minutes is known at 09:35.
    /// </summary>
    public TimeSpan? KnownFromNewYorkTime { get; init; }

    /// <summary>
    /// True when the ticker is a member on the entry's New York day and that day's membership was already known at the entry
    /// time, so an entry before 09:35 can never use a ranking decided at 09:35.
    /// </summary>
    public bool IsMemberAt(string ticker, DateTimeOffset entryUtc)
    {
        if (KnownFromNewYorkTime is not { } knownFrom) return IsMember(ticker, DateOnly.FromDateTime(entryUtc.UtcDateTime));
        var local = TimeZoneInfo.ConvertTime(entryUtc, NewYork);
        return IsMember(ticker, DateOnly.FromDateTime(local.DateTime)) && local.TimeOfDay >= knownFrom;
    }

    // Looked up once: IsMemberAt runs for every candidate of a run.
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
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
