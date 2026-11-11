using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Point-in-time universe screener. Eligibility on any given day uses only daily bars dated
/// strictly before that day, so selection can never "know" a stock ran during the window.
/// Supports a single as-of screen (per-run) and full per-day membership. See
/// See the point-in-time universe boundary in docs/operating-boundaries.md.
/// </summary>
public sealed class HistoricalScreenerUniverseProvider(IMarketDataProvider provider) : IUniverseProvider
{
    public async Task<UniverseResolution> ResolveAsync(UniverseRequest request, CancellationToken cancellationToken)
    {
        var universe = request.Universe;
        var candidates = ResolveCandidates(request);
        var asOf = request.AsOfUtc;
        var asOfDate = DateOnly.FromDateTime(asOf.UtcDateTime);

        var barsByTicker = await FetchDailyBarsAsync(candidates, request.DailyTimeframe, FetchStart(asOf, universe), asOf, cancellationToken);

        var selected = new List<UniverseSelection>();
        var rejections = new List<string>();
        foreach (var ticker in candidates)
        {
            var priorBars = OrderedBarsBefore(barsByTicker, ticker, asOf);
            var (eligible, selection, rejection) = Evaluate(ticker, priorBars, universe);
            if (eligible && selection is not null)
            {
                selected.Add(selection);
            }
            else if (rejection is not null)
            {
                rejections.Add(rejection);
            }
        }

        IEnumerable<UniverseSelection> ranked = selected.OrderByDescending(item => item.AverageDollarVolume);
        if (universe.MaxSymbols is { } cap && cap > 0)
        {
            ranked = ranked.Take(cap);
        }

        var finalSelected = ranked.ToList();
        return new UniverseResolution(
            finalSelected.Select(item => item.Ticker).ToArray(),
            universe.MergesCuratedAndFinviz ? "historical_screener/curated+finviz" : UniverseConfig.HistoricalScreenerMode,
            asOfDate,
            finalSelected,
            rejections);
    }

    /// <summary>
    /// Computes, for every candidate, the set of trading days it qualifies on across
    /// [as-of .. window end], each day judged only on bars before it. A ticker is a member
    /// on day D iff it passed the screen using data available before D.
    /// </summary>
    public async Task<UniverseMembership> ResolveMembershipAsync(UniverseRequest request, CancellationToken cancellationToken)
    {
        var universe = request.Universe;
        var candidates = ResolveCandidates(request);
        var asOf = request.AsOfUtc;
        var windowEnd = request.WindowEndUtc ?? asOf;
        var firstDay = DateOnly.FromDateTime(asOf.UtcDateTime);
        var lastDay = DateOnly.FromDateTime(windowEnd.UtcDateTime);

        var barsByTicker = await FetchDailyBarsAsync(candidates, request.DailyTimeframe, FetchStart(asOf, universe), windowEnd, cancellationToken);

        var eligibleDaysByTicker = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        var sessionsByTicker = new Dictionary<string, IReadOnlyList<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticker in candidates)
        {
            if (!barsByTicker.TryGetValue(ticker, out var bars) || bars.Count == 0)
            {
                continue;
            }

            // One bar per date, ascending. Each decision day uses the strictly-earlier bars.
            var ordered = bars
                .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
                .Select(group => (Day: group.Key, Bar: group.OrderBy(bar => bar.Timestamp).Last()))
                .OrderBy(item => item.Day)
                .ToList();
            sessionsByTicker[ticker] = ordered.Select(item => item.Day).ToArray();

            var eligibleDays = new HashSet<DateOnly>();
            for (var index = 0; index < ordered.Count; index++)
            {
                var day = ordered[index].Day;
                if (day < firstDay || day > lastDay)
                {
                    continue;
                }

                // A fixed trailing window of completed sessions, so a day's decision does not depend on where the run starts.
                // The legacy screen reads only its look-back too, so its decisions are unchanged.
                var first = Math.Max(0, index - universe.ScreenLookbackSessions);
                var priorBars = ordered.Skip(first).Take(index - first).Select(item => item.Bar).ToList();
                // The decision day's own open is known once that session opens; it is read only when the rules compare it.
                var (eligible, _, _) = Evaluate(ticker, priorBars, universe, ordered[index].Bar.Open);
                if (eligible)
                {
                    eligibleDays.Add(day);
                }
            }

            if (eligibleDays.Count > 0)
            {
                eligibleDaysByTicker[ticker] = eligibleDays;
            }
        }

        return universe.DailyRanking is { } ranking
            ? await RankMembershipAsync(eligibleDaysByTicker, sessionsByTicker, ranking, asOf, windowEnd, cancellationToken)
            : new UniverseMembership(eligibleDaysByTicker);
    }

    /// <summary>
    /// Narrows daily eligibility to each session's ranked stocks in play. Reads the 09:30-09:35 New York five-minute bar of
    /// each stock's own trading sessions, ranks every session's eligible stocks by the shared opening-volume ranking, and keeps
    /// the members. A missing opening bar counts as zero volume only when the stock has other regular-session five-minute bars
    /// that session (so the provider covered the day and had no trade to report in the first five minutes); otherwise the
    /// value is unproven and the stock is excluded that session as opening_bar_coverage_missing. The membership is marked as
    /// known from 09:35, so no entry before that time can use it. Backtests have no issuer records here, so the symbol stands
    /// in for the issuer identity.
    /// </summary>
    private async Task<UniverseMembership> RankMembershipAsync(
        IReadOnlyDictionary<string, HashSet<DateOnly>> eligibleDaysByTicker,
        IReadOnlyDictionary<string, IReadOnlyList<DateOnly>> sessionsByTicker,
        DailyRankingRules ranking,
        DateTimeOffset asOf,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        ranking.Validate();
        var evidence = eligibleDaysByTicker.Count == 0
            ? new OpeningBarEvidence()
            : await FetchOpeningBarEvidenceAsync(eligibleDaysByTicker.Keys.ToArray(),
                asOf.AddDays(-ranking.BaselineFetchCalendarDays), windowEnd, cancellationToken);
        var observationsByDay = new SortedDictionary<DateOnly, List<OpeningVolumeObservation>>();
        foreach (var (ticker, days) in eligibleDaysByTicker)
        {
            var sessions = sessionsByTicker[ticker];
            var identity = new UniverseSecurityIdentity(ticker, ticker, ticker);
            for (var index = 0; index < sessions.Count; index++)
            {
                if (!days.Contains(sessions[index])) continue;
                if (!observationsByDay.TryGetValue(sessions[index], out var list)) observationsByDay[sessions[index]] = list = [];
                list.Add(Observe(identity, sessions, index, ranking.BaselineSessions, evidence));
            }
        }

        var rankings = observationsByDay.Select(pair => OpeningVolumeRanking.Rank(pair.Key, pair.Value, ranking)).ToArray();
        var members = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in rankings.SelectMany(session => session.Members))
        {
            if (!members.TryGetValue(member.Security.Symbol, out var days)) members[member.Security.Symbol] = days = [];
            days.Add(member.Session);
        }

        return new UniverseMembership(members) { SessionRankings = rankings, KnownFromNewYorkTime = OpeningVolumeKnownAt };
    }

    /// <summary>The ranked opening interval ends at 09:35 New York; membership is unknown before then.</summary>
    public static readonly TimeSpan OpeningVolumeKnownAt = new(9, 30 + DailyRankingRules.OpeningMinutes, 0);

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan RegularOpen = new(9, 30, 0);
    private static readonly TimeSpan RegularCloseLimit = new(16, 0, 0);

    /// <summary>
    /// One stock's observation on the session at <paramref name="index"/> of its traded sessions: that session's opening
    /// volume and the opening volumes of the up to <paramref name="baselineSessions"/> traded sessions before it, oldest first,
    /// each null when unproven, plus how many of those earlier values are proven zeros.
    /// </summary>
    private static OpeningVolumeObservation Observe(UniverseSecurityIdentity identity, IReadOnlyList<DateOnly> sessions, int index,
        int baselineSessions, OpeningBarEvidence evidence)
    {
        var ticker = identity.Symbol;
        var baseline = sessions.Skip(Math.Max(0, index - baselineSessions)).Take(Math.Min(index, baselineSessions)).ToArray();
        return new OpeningVolumeObservation(identity, evidence.Volume(ticker, sessions[index]),
            baseline.Select(day => evidence.Volume(ticker, day)).ToArray(), baseline.Count(day => evidence.IsProvenZero(ticker, day)));
    }

    /// <summary>
    /// Streams five-minute bars over exactly the range a prepared run admits for the ranking (the as-of time less the
    /// baseline's calendar days, to the window end), and keeps per stock and New York session only the 09:30 bar's volume and
    /// whether the stock had any regular-session (09:30-16:00) bar, so memory stays at two small entries per stock and session.
    /// The later bars of a decision day are read only as proof that the provider covered that stock and day; their prices and
    /// volumes never enter the ranking. One request over the whole range is kept instead of one per session: both the
    /// ordinary cache and prepared runs serve only exact requested ranges, and the zero-volume proof needs the rest of the day.
    /// </summary>
    private async Task<OpeningBarEvidence> FetchOpeningBarEvidenceAsync(
        IReadOnlyList<string> tickers, DateTimeOffset from, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var evidence = new OpeningBarEvidence();
        await foreach (var bar in provider.GetBarsAsync(tickers, ["5m"], from, windowEnd, cancellationToken))
        {
            if (bar.Timestamp > windowEnd) continue;
            var local = TimeZoneInfo.ConvertTime(bar.Timestamp, NewYork);
            if (local.TimeOfDay < RegularOpen || local.TimeOfDay >= RegularCloseLimit) continue;
            evidence.Record(bar.Ticker, DateOnly.FromDateTime(local.DateTime), local.TimeOfDay == RegularOpen ? bar.Volume : null);
        }

        return evidence;
    }

    /// <summary>
    /// What five-minute bars prove about each stock's first five minutes per New York session: the 09:30 bar's volume where
    /// the bar exists, zero where it is missing but the stock had another regular-session bar that day (no trade in the
    /// interval), and nothing (null) otherwise.
    /// </summary>
    private sealed class OpeningBarEvidence
    {
        private readonly Dictionary<(string Ticker, DateOnly Day), decimal> openingVolumes = new(OpeningKeyComparer.Instance);
        private readonly HashSet<(string Ticker, DateOnly Day)> coveredSessions = new(OpeningKeyComparer.Instance);

        /// <summary>Records one regular-session bar; <paramref name="openingVolume"/> is set only for the 09:30 bar.</summary>
        public void Record(string ticker, DateOnly day, decimal? openingVolume)
        {
            coveredSessions.Add((ticker, day));
            if (openingVolume is { } volume) openingVolumes[(ticker, day)] = volume;
        }

        /// <summary>The proven first five-minute volume of the stock on the day, or null when it is not proven.</summary>
        public decimal? Volume(string ticker, DateOnly day) =>
            openingVolumes.TryGetValue((ticker, day), out var volume) ? volume : IsProvenZero(ticker, day) ? 0m : null;

        /// <summary>True when the opening bar is missing but other regular-session bars prove the day was covered.</summary>
        public bool IsProvenZero(string ticker, DateOnly day) =>
            !openingVolumes.ContainsKey((ticker, day)) && coveredSessions.Contains((ticker, day));
    }

    /// <summary>Compares stock-session keys with symbols ignoring case, like the rest of the screen.</summary>
    private sealed class OpeningKeyComparer : IEqualityComparer<(string Ticker, DateOnly Day)>
    {
        /// <summary>The one shared instance; the comparer holds no state.</summary>
        public static readonly OpeningKeyComparer Instance = new();

        /// <summary>Equal when the session dates match and the symbols match ignoring case.</summary>
        public bool Equals((string Ticker, DateOnly Day) x, (string Ticker, DateOnly Day) y) =>
            x.Day == y.Day && StringComparer.OrdinalIgnoreCase.Equals(x.Ticker, y.Ticker);

        /// <summary>Hashes the symbol ignoring case together with the date, consistent with <see cref="Equals"/>.</summary>
        public int GetHashCode((string Ticker, DateOnly Day) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Ticker), key.Day);
    }

    private static IReadOnlyList<string> ResolveCandidates(UniverseRequest request)
    {
        return (request.Universe.Candidates.Count > 0 ? request.Universe.Candidates : request.ConfiguredTickers)
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Over-fetch calendar days so the screen's required completed sessions exist before the window.
    private static DateTimeOffset FetchStart(DateTimeOffset asOf, UniverseConfig universe) =>
        asOf.AddDays(-universe.ScreenFetchCalendarDays);

    private async Task<Dictionary<string, List<OhlcvBar>>> FetchDailyBarsAsync(
        IReadOnlyList<string> candidates,
        string dailyTimeframe,
        DateTimeOffset fetchStart,
        DateTimeOffset fetchEnd,
        CancellationToken cancellationToken)
    {
        var barsByTicker = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
        {
            return barsByTicker;
        }

        await foreach (var bar in provider.GetBarsAsync(
            candidates,
            new[] { dailyTimeframe },
            fetchStart,
            fetchEnd,
            cancellationToken))
        {
            if (bar.Timestamp > fetchEnd)
            {
                continue;
            }

            if (!barsByTicker.TryGetValue(bar.Ticker, out var list))
            {
                list = new List<OhlcvBar>();
                barsByTicker[bar.Ticker] = list;
            }

            list.Add(bar);
        }

        return barsByTicker;
    }

    private static List<OhlcvBar> OrderedBarsBefore(
        Dictionary<string, List<OhlcvBar>> barsByTicker,
        string ticker,
        DateTimeOffset cutoffExclusive)
    {
        if (!barsByTicker.TryGetValue(ticker, out var bars))
        {
            return new List<OhlcvBar>();
        }

        // CRITICAL no-lookahead guard: never use a bar dated at or after the cutoff.
        return bars
            .Where(bar => bar.Timestamp < cutoffExclusive)
            .OrderBy(bar => bar.Timestamp)
            .ToList();
    }

    // Shared eligibility rule so the as-of screen and per-day membership stay identical. A run's explicit daily eligibility
    // rules replace the legacy price and dollar-volume fields; sessionOpen is the decision day's open when it is known.
    private static (bool Eligible, UniverseSelection? Selection, string? Rejection) Evaluate(
        string ticker,
        IReadOnlyList<OhlcvBar> priorBars,
        UniverseConfig universe,
        decimal? sessionOpen = null)
    {
        var lookback = Math.Max(universe.LookbackDays, 1);
        var rules = universe.DailyEligibility ?? new DailyScreenRules(universe.MinPrice, DailyScreenPriceBasis.PriorClose, lookback,
            MinAverageDollarVolume: universe.MinAvgDollarVolume);
        var screen = DailyScreen.Evaluate(ticker, priorBars, sessionOpen, rules);
        if (!screen.Eligible)
        {
            return (false, null, screen.Rejection);
        }

        var window = priorBars.Count <= lookback ? priorBars : priorBars.Skip(priorBars.Count - lookback).ToList();
        var lastClose = priorBars[^1].Close;
        var averageDollarVolume = screen.Values!.AverageDollarVolume;
        decimal? priorReturnPct = window.Count >= 2 && window[0].Close > 0m
            ? (window[^1].Close - window[0].Close) / window[0].Close * 100m
            : null;

        if (universe.MinPriorReturnPct is { } minReturn &&
            (priorReturnPct is null || priorReturnPct.Value < minReturn))
        {
            return (false, null, $"{ticker}: prior_return {(priorReturnPct?.ToString("F2") ?? "n/a")} < min {minReturn:F2}");
        }

        return (true, new UniverseSelection(ticker, lastClose, averageDollarVolume, priorReturnPct), null);
    }
}
