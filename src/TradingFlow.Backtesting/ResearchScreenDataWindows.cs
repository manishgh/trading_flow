using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Backtesting;

/// <summary>
/// Per-stock bar request starts for a daily research-screen run on an eligible list: each stock is requested from its first
/// eligible day minus the run's warm-up (time_window.warmup_lookback_days, never before the run's data start) through the run
/// end. Bars after the last eligible day stay requested so a position opened near that day can still exit on real prices.
/// Other runs (and ranked intraday screens) request every stock over the whole window, unchanged.
/// </summary>
internal static class ResearchScreenDataWindows
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>The start of each pool stock's bar request; empty when the run is not a daily eligible-list research screen.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> Starts(BacktestRunConfig run, DateTimeOffset dataStart,
        DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        if (run.Universe?.RankedMembership is not { } reference || ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
            return new Dictionary<string, DateTimeOffset>();
        var screen = PublishedResearchScreen.Read(Path.GetDirectoryName(reference.ManifestPath)!, reference.ManifestSha256);
        var (first, _) = screen.EligibleSpan(NewYorkDate(windowStart), NewYorkDate(windowEnd.AddTicks(-1)));
        var warmup = TimeSpan.FromDays(Math.Max(0, run.TimeWindow.WarmupLookbackDays));
        return first.ToDictionary(pair => pair.Key.ToUpperInvariant(), pair =>
        {
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(pair.Value.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero) - warmup;
            return start > dataStart ? start : dataStart;
        }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Wraps a calendar-capable provider so each stock is read from its own start; others are returned unchanged.</summary>
    public static IMarketDataProvider Apply(IMarketDataProvider inner, IReadOnlyDictionary<string, DateTimeOffset> starts) =>
        starts.Count > 0 && inner is IMarketSessionScheduleProvider calendar ? new PerStockStartProvider(inner, calendar, starts) : inner;

    private static DateOnly NewYorkDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, NewYork).DateTime);

    /// <summary>
    /// Reads each requested stock separately from the later of the requested start and its own start, so a prepared source
    /// (which serves only exact pinned requests) and a live source receive the same per-stock request.
    /// </summary>
    private sealed class PerStockStartProvider(IMarketDataProvider inner, IMarketSessionScheduleProvider calendar,
        IReadOnlyDictionary<string, DateTimeOffset> starts) : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        /// <summary>One request per stock, starting at that stock's own start when it is later than the requested start.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var ticker in tickers)
            {
                var from = starts.TryGetValue(ticker, out var own) && own > start ? own : start;
                await foreach (var bar in inner.GetBarsAsync([ticker], timeframes, from, end, cancellationToken)) yield return bar;
            }
        }

        /// <summary>The inner provider's authoritative calendar, unchanged.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) =>
            calendar.LoadMarketSessionSchedulesAsync(startDateInclusive, endDateInclusive, cancellationToken);
    }
}
