using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Regime;

/// <summary>
/// Shared market-regime gate used by both the backtest and the
/// live/paper paths, so a strategy's regime rule (e.g. SPY above its 50-day SMA) is enforced identically
/// everywhere instead of silently applying only in backtests. Loads benchmark daily bars — resampling a
/// finer timeframe up to daily when a cache holds no native daily bars — and answers no-lookahead
/// "is the regime on for entering on day D?" queries. Provider-owned caches may reuse actual bars;
/// a boolean result is never shared across strategies, feeds or later observations.
/// </summary>
public sealed class RegimeGateService
{
    private readonly BarResampler resampler = new();

    /// <summary>
    /// Loads daily benchmark bars, preferring native daily and otherwise resampling the finest available
    /// sub-daily timeframe to daily. Returns an empty list when the benchmark has no data at all.
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

        // Coarsest sub-daily interval first (fewer bars to resample, same daily closes).
        var subDailyTimeframes = availableIntervals
            .Where(interval => !TimeframeParser.IsDailyOrHigher(interval))
            .OrderByDescending(interval => TimeframeParser.Parse(interval))
            .ToArray();
        foreach (var interval in subDailyTimeframes)
        {
            var subDaily = await LoadIntervalAsync(provider, benchmarkSymbol, interval, start, end, cancellationToken);
            if (subDaily.Count > 0)
            {
                return resampler.Resample(subDaily, "1d");
            }
        }

        return Array.Empty<OhlcvBar>();
    }

    /// <summary>
    /// Live/paper query: is the regime on for entering today? Fails closed (returns false) when the
    /// benchmark can't be confirmed above its SMA, so a data hiccup pauses new entries rather than
    /// removing the protection. Evaluates this exact period/provider/clock from actual bars on each call;
    /// another strategy's SMA result or a prior feed observation cannot be reused as evidence.
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

        // Over-fetch so the SMA has enough prior daily closes.
        var start = now.AddDays(-((regime.RequiredCloses * 2) + 20));
        var daily = await LoadBenchmarkDailyBarsAsync(provider, regime.BenchmarkSymbol, availableIntervals, start, now, cancellationToken);
        if (daily.Count == 0)
        {
            // Could not fetch the benchmark at all — fail closed but do not cache, so we retry next cycle.
            return false;
        }

        return RegimeCalendarBuilder.IsRegimeOnAt(daily, regime.SmaPeriod, now, regime.SmaRisingLookbackBars);
    }

    /// <summary>
    /// Live/paper per-stock query with the same reasons as the backtest calendar (<see cref="RegimeCalendar.RejectionFor"/>):
    /// null when a new entry in <paramref name="ticker"/> is allowed now; otherwise the benchmark is off, or under the Nasdaq
    /// home-market rule the stock's listing exchange is unknown (no asset source, no asset record or no exchange), or the
    /// stock is listed on Nasdaq and the home-market fund (QQQ) is off. Every missing piece fails closed.
    /// </summary>
    public async Task<string?> RegimeRejectionAsync(
        RegimeRules regime,
        IMarketDataProvider provider,
        IReadOnlyList<string> availableIntervals,
        DateTimeOffset now,
        string ticker,
        TradingFlow.Engine.Execution.IAssetTradingEligibilityProvider? assets,
        CancellationToken cancellationToken)
    {
        if (!await IsRegimeOnAsync(regime, provider, availableIntervals, now, cancellationToken))
            return regime.IsActive ? RegimeCalendar.MarketOffReason : null;
        if (!regime.IsActive || !regime.UsesNasdaqHomeMarket) return null;
        string? exchange;
        try
        {
            exchange = assets is null ? null : (await assets.GetEligibilityAsync(ticker, cancellationToken))?.Exchange;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed asset lookup (rate limit, server error, network) refuses the entry with an audited reason instead of
            // ending the ticker's evaluation with an error.
            return RegimeCalendar.ListingExchangeLookupFailedReason;
        }
        if (String.IsNullOrWhiteSpace(exchange)) return RegimeCalendar.ListingExchangeUnknownReason;
        if (!exchange.Equals(RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase)) return null;
        var homeMarket = regime with { BenchmarkSymbol = regime.NasdaqListingBenchmark!, NasdaqListingBenchmark = null };
        return await IsRegimeOnAsync(homeMarket, provider, availableIntervals, now, cancellationToken)
            ? null : RegimeCalendar.NasdaqMarketOffReason;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A missing timeframe/benchmark surfaces as empty so the caller can try the next timeframe.
            return Array.Empty<OhlcvBar>();
        }

        return bars;
    }
}
