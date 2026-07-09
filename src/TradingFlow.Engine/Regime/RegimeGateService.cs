using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Regime;

/// <summary>
/// Shared Layer-2 regime gate (docs/strategy-design-doctrine.md §2) used by BOTH the backtest and the
/// live/paper paths, so a strategy's regime rule (e.g. SPY above its 50-day SMA) is enforced identically
/// everywhere instead of silently applying only in backtests. Loads benchmark daily bars — resampling a
/// finer timeframe up to daily when a cache holds no native daily bars — and answers no-lookahead
/// "is the regime on for entering on day D?" queries. A per-benchmark/per-day cache keeps the live path
/// from re-fetching the benchmark on every ticker.
/// </summary>
public sealed class RegimeGateService
{
    private readonly BarResampler resampler = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (DateOnly AsOf, bool On)> liveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads daily benchmark bars, preferring native daily and otherwise resampling the finest available
    /// intraday timeframe to daily. Returns an empty list when the benchmark has no data at all.
    /// </summary>
    public async Task<IReadOnlyList<OhlcvBar>> LoadBenchmarkDailyBarsAsync(
        IMarketDataProvider provider,
        string benchmarkSymbol,
        IReadOnlyList<string> availableIntervals,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var daily = await LoadIntervalAsync(provider, benchmarkSymbol, "1d", start, end, cancellationToken);
        if (daily.Count > 0)
        {
            return daily;
        }

        // Coarsest intraday first (fewer bars to resample, same daily closes).
        var intradayTimeframes = availableIntervals
            .Where(interval => !TimeframeParser.IsDailyOrHigher(interval))
            .OrderByDescending(interval => TimeframeParser.Parse(interval))
            .ToArray();
        foreach (var interval in intradayTimeframes)
        {
            var intraday = await LoadIntervalAsync(provider, benchmarkSymbol, interval, start, end, cancellationToken);
            if (intraday.Count > 0)
            {
                return resampler.Resample(intraday, "1d");
            }
        }

        return Array.Empty<OhlcvBar>();
    }

    /// <summary>
    /// Live/paper query: is the regime on for entering today? Fails closed (returns false) when the
    /// benchmark can't be confirmed above its SMA, so a data hiccup pauses new entries rather than
    /// removing the protection. Cached per benchmark for the day, but a total fetch failure is not cached
    /// so it retries on the next cycle.
    /// </summary>
    public async Task<bool> IsRegimeOnAsync(
        RegimeRules regime,
        IMarketDataProvider provider,
        IReadOnlyList<string> availableIntervals,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!regime.IsActive)
        {
            return true;
        }

        var asOf = DateOnly.FromDateTime(now.UtcDateTime);
        lock (gate)
        {
            if (liveCache.TryGetValue(regime.BenchmarkSymbol, out var cached) && cached.AsOf == asOf)
            {
                return cached.On;
            }
        }

        // Over-fetch so the SMA has enough prior daily closes.
        var start = now.AddDays(-((regime.SmaPeriod * 2) + 20));
        var daily = await LoadBenchmarkDailyBarsAsync(provider, regime.BenchmarkSymbol, availableIntervals, start, now, cancellationToken);
        if (daily.Count == 0)
        {
            // Could not fetch the benchmark at all — fail closed but do not cache, so we retry next cycle.
            return false;
        }

        var on = RegimeCalendarBuilder.IsRegimeOnAsOf(daily, regime.SmaPeriod, asOf);
        lock (gate)
        {
            liveCache[regime.BenchmarkSymbol] = (asOf, on);
        }

        return on;
    }

    private static async Task<IReadOnlyList<OhlcvBar>> LoadIntervalAsync(
        IMarketDataProvider provider,
        string ticker,
        string interval,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var bars = new List<OhlcvBar>();
        try
        {
            await foreach (var bar in provider.GetBarsAsync(new[] { ticker }, new[] { interval }, start, end, cancellationToken))
            {
                bars.Add(bar);
            }
        }
        catch (Exception)
        {
            // A missing timeframe/benchmark surfaces as empty so the caller can try the next timeframe.
            return Array.Empty<OhlcvBar>();
        }

        return bars;
    }
}
