using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Web.Services;

/// <summary>One sector fund's rank in the live leader list shown in the workspace status strip.</summary>
public sealed record SectorLeader(int Rank, string Symbol, string Name, decimal Return12Minus1);

/// <summary>The live sector ranking for one completed session, or the reason it is unavailable.</summary>
public sealed record SectorLeaderRanking(DateOnly? AsOfSession, IReadOnlyList<SectorLeader> Leaders, string? UnavailableReason);

/// <summary>
/// Ranks the sector funds listed in <c>configs/research/sector-funds.txt</c> by the same 12-1 month return the published
/// sector research screens use (<see cref="ResearchDailyScreenPublisher.Return12Minus1"/>: the close 22 sessions before the
/// latest completed session divided by the close 253 sessions before it, minus one) on completed daily bars, and keeps the
/// top six. Display context only; it
/// does not admit or block any strategy entry. One ranking is kept per completed session, so the strip's one-minute
/// refresh does not re-download a year of bars for eleven funds.
/// </summary>
public static class SectorLeadersService
{
    /// <summary>How many leading sectors the status strip shows.</summary>
    public const int LeaderCount = 6;

    private const int RequiredSessions = 254;
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(400);
    private static readonly object CacheLock = new();
    private static SectorLeaderRanking? cachedRanking;

    // Plain sector names for the Select Sector SPDR funds, used only for the strip tooltip.
    private static readonly IReadOnlyDictionary<string, string> SectorNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["XLB"] = "Materials", ["XLC"] = "Communication services", ["XLE"] = "Energy", ["XLF"] = "Financials",
        ["XLI"] = "Industrials", ["XLK"] = "Technology", ["XLP"] = "Consumer staples", ["XLRE"] = "Real estate",
        ["XLU"] = "Utilities", ["XLV"] = "Health care", ["XLY"] = "Consumer discretionary"
    };

    /// <summary>
    /// Returns the cached ranking when it was computed for <paramref name="latestSession"/>; otherwise downloads completed
    /// daily bars for the configured funds, ranks them and caches the result. Failures are returned as an unavailable
    /// ranking with a reason and are not cached, so the next strip refresh retries.
    /// </summary>
    public static async Task<SectorLeaderRanking> GetLeadersAsync(
        IMarketDataProvider provider,
        string sectorFundsFile,
        DateOnly? latestSession,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (CacheLock)
        {
            if (cachedRanking is { } hit && latestSession is not null && hit.AsOfSession == latestSession) return hit;
        }

        var funds = ReadFunds(sectorFundsFile);
        if (funds.Count == 0) return new SectorLeaderRanking(null, [], "No sector funds are configured.");

        var barsBySymbol = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var bar in provider.GetBarsAsync(funds, ["1d"], now - HistoryWindow, now, cancellationToken))
        {
            if (!barsBySymbol.TryGetValue(bar.Ticker, out var list)) barsBySymbol[bar.Ticker] = list = [];
            list.Add(bar);
        }

        var ranking = Rank(funds, barsBySymbol, schedules, now);
        if (ranking.UnavailableReason is null && latestSession is not null && ranking.AsOfSession == latestSession)
        {
            lock (CacheLock) cachedRanking = ranking;
        }

        return ranking;
    }

    /// <summary>
    /// Ranks funds by 12-1 month return on completed trading-day bars known at <paramref name="now"/>. A fund with fewer than
    /// 254 sessions, or whose latest completed session differs from the most common one, is left out rather than compared
    /// on a different date.
    /// </summary>
    internal static SectorLeaderRanking Rank(
        IReadOnlyList<string> funds,
        IReadOnlyDictionary<string, List<OhlcvBar>> barsBySymbol,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now)
    {
        var measured = new List<(string Symbol, DateOnly Session, decimal Return)>();
        foreach (var symbol in funds)
        {
            if (!barsBySymbol.TryGetValue(symbol, out var bars)) continue;
            var completed = VixRegime.FilterTradingDayBars(bars, schedules)
                .Where(bar => bar.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) && WorkspacePulseEndpoints.IsCompletedBar(bar, now))
                .OrderBy(bar => bar.Timestamp)
                .ToArray();
            if (completed.Length < RequiredSessions) continue;
            if (ResearchDailyScreenPublisher.Return12Minus1(completed, [], completed.Length - 1) is not { } value) continue;
            measured.Add((symbol, DateOnly.FromDateTime(completed[^1].Timestamp.UtcDateTime), value));
        }

        if (measured.Count == 0) return new SectorLeaderRanking(null, [], "No sector fund had a full year of completed daily bars.");

        var session = measured.GroupBy(item => item.Session).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).First().Key;
        var leaders = measured
            .Where(item => item.Session == session)
            .OrderByDescending(item => item.Return)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Take(LeaderCount)
            .Select((item, index) => new SectorLeader(index + 1, item.Symbol, SectorNames.GetValueOrDefault(item.Symbol, item.Symbol), Math.Round(item.Return, 4)))
            .ToArray();
        return new SectorLeaderRanking(session, leaders, null);
    }

    // Reads one fund symbol per line, ignoring blanks and '#' comments.
    private static IReadOnlyList<string> ReadFunds(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Select(line => line.Trim().ToUpperInvariant())
                .Where(line => line.Length > 0 && !line.StartsWith('#')).Distinct(StringComparer.Ordinal).ToArray()
            : [];
}
