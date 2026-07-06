using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Point-in-time universe screener. Eligibility on any given day uses only daily bars dated
/// strictly before that day, so selection can never "know" a stock ran during the window.
/// Supports a single as-of screen (per-run) and full per-day membership. See
/// docs/edge-recovery-master-plan.md phase 0.1.
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

            var eligibleDays = new HashSet<DateOnly>();
            for (var index = 0; index < ordered.Count; index++)
            {
                var day = ordered[index].Day;
                if (day < firstDay || day > lastDay)
                {
                    continue;
                }

                var priorBars = ordered.Take(index).Select(item => item.Bar).ToList();
                var (eligible, _, _) = Evaluate(ticker, priorBars, universe);
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

        return new UniverseMembership(eligibleDaysByTicker);
    }

    private static IReadOnlyList<string> ResolveCandidates(UniverseRequest request)
    {
        return (request.Universe.Candidates.Count > 0 ? request.Universe.Candidates : request.ConfiguredTickers)
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTimeOffset FetchStart(DateTimeOffset asOf, UniverseConfig universe)
    {
        var lookback = Math.Max(universe.LookbackDays, 1);
        // Over-fetch calendar days so at least `lookback` trading days exist before the window.
        return asOf.AddDays(-((lookback * 3) + 15));
    }

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

    // Shared eligibility rule so the as-of screen and per-day membership stay identical.
    private static (bool Eligible, UniverseSelection? Selection, string? Rejection) Evaluate(
        string ticker,
        IReadOnlyList<OhlcvBar> priorBars,
        UniverseConfig universe)
    {
        if (priorBars.Count == 0)
        {
            return (false, null, $"{ticker}: no_prior_daily_data");
        }

        var lookback = Math.Max(universe.LookbackDays, 1);
        var window = priorBars.Count <= lookback ? priorBars : priorBars.Skip(priorBars.Count - lookback).ToList();
        var lastClose = priorBars[^1].Close;
        var averageDollarVolume = window.Average(bar => bar.Close * bar.Volume);
        decimal? priorReturnPct = window.Count >= 2 && window[0].Close > 0m
            ? (window[^1].Close - window[0].Close) / window[0].Close * 100m
            : null;

        if (lastClose < universe.MinPrice)
        {
            return (false, null, $"{ticker}: price {lastClose:F2} < min {universe.MinPrice:F2}");
        }

        if (averageDollarVolume < universe.MinAvgDollarVolume)
        {
            return (false, null, $"{ticker}: adv {averageDollarVolume:F0} < min {universe.MinAvgDollarVolume:F0}");
        }

        if (universe.MinPriorReturnPct is { } minReturn &&
            (priorReturnPct is null || priorReturnPct.Value < minReturn))
        {
            return (false, null, $"{ticker}: prior_return {(priorReturnPct?.ToString("F2") ?? "n/a")} < min {minReturn:F2}");
        }

        return (true, new UniverseSelection(ticker, lastClose, averageDollarVolume, priorReturnPct), null);
    }
}
