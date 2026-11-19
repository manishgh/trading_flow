using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Regime;

/// <summary>
/// The live/paper regime decision for one entry: a refusal reason, or the per-trade risk scale the shared order planner
/// applies (1 = the strategy's normal risk). <paramref name="Vix"/> holds the VIX reading when the rule uses VIX sizing.
/// </summary>
public sealed record RegimeEntryDecision(string? Rejection, decimal RiskScale, VixEntryDecision? Vix = null)
{
    /// <summary>An entry allowed at the strategy's normal risk.</summary>
    public static RegimeEntryDecision Allowed { get; } = new(null, 1m);

    /// <summary>An entry refused for <paramref name="reason"/>.</summary>
    public static RegimeEntryDecision Refused(string reason) => new(reason, 0m);
}

/// <summary>
/// Shared market-regime gate used by both the backtest and the
/// live/paper paths, so a strategy's regime rule (e.g. SPY above its 50-day SMA) is enforced identically
/// everywhere instead of silently applying only in backtests. Loads benchmark daily bars — resampling a
/// finer timeframe up to daily when a cache holds no native daily bars — and answers no-lookahead
/// "is the regime on for entering on day D?" queries. Provider-owned caches may reuse actual bars;
/// a boolean result is never shared across strategies, feeds or later observations.
/// </summary>
public sealed class RegimeGateService(IVixCloseSource? vixCloses = null)
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

        var (scheduleSucceeded, schedules) = await TryLoadRecentSchedulesAsync(provider, now, cancellationToken);
        if (!scheduleSucceeded)
            return false;
        var daily = await LoadRegimeBarsAsync(regime, provider, availableIntervals, now, cancellationToken);
        // Could not fetch the benchmark at all or bars are stale against authoritative schedule — fail closed.
        return IsBenchmarkOn(daily, regime, now, schedules);
    }

    // The benchmark's daily bars a live decision at `now` needs, over-fetched so both averages have enough prior closes.
    private Task<IReadOnlyList<OhlcvBar>> LoadRegimeBarsAsync(RegimeRules regime, IMarketDataProvider provider,
        IReadOnlyList<string> availableIntervals, DateTimeOffset now, CancellationToken cancellationToken) =>
        LoadBenchmarkDailyBarsAsync(provider, regime.BenchmarkSymbol, availableIntervals,
            now.AddDays(-((regime.RequiredCloses * 2) + 20)), now, cancellationToken);

    /// <summary>
    /// Evaluates the complete market-regime gate (benchmark -> optional Nasdaq home-market -> optional VIX sizing)
    /// over already-loaded daily bars at <paramref name="decisionTime"/>. Shared by live/paper
    /// (<see cref="EntryDecisionAsync"/>) and the Desk (<c>StockStrategyEvidenceLoader</c>) so both enforce one
    /// decision order and identical refusal reasons.
    /// </summary>
    public static RegimeEntryDecision Evaluate(
        RegimeRules regime,
        IReadOnlyList<OhlcvBar> benchmarkDailyBars,
        DateTimeOffset decisionTime,
        string? listingExchange = null,
        bool listingLookupFailed = false,
        IReadOnlyList<OhlcvBar>? nasdaqHomeMarketBars = null,
        VixCloseSeries? vixCloses = null,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null)
    {
        if (!regime.IsActive) return RegimeEntryDecision.Allowed;
        var validBenchmark = VixRegime.FilterTradingDayBars(benchmarkDailyBars, schedules);
        if (!IsFilteredBenchmarkOn(validBenchmark, regime, decisionTime, schedules))
            return RegimeEntryDecision.Refused(RegimeCalendar.MarketOffReason);
        if (regime.UsesNasdaqHomeMarket)
        {
            if (listingLookupFailed)
                return RegimeEntryDecision.Refused(RegimeCalendar.ListingExchangeLookupFailedReason);
            if (String.IsNullOrWhiteSpace(listingExchange))
                return RegimeEntryDecision.Refused(RegimeCalendar.ListingExchangeUnknownReason);
            if (listingExchange.Equals(RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase) &&
                !IsBenchmarkOn(nasdaqHomeMarketBars ?? [], regime, decisionTime, schedules))
                return RegimeEntryDecision.Refused(RegimeCalendar.NasdaqMarketOffReason);
        }
        if (!regime.UsesVix) return RegimeEntryDecision.Allowed;
        var vix = VixRegime.Decide(regime, vixCloses, VixRegime.PreviousSession(validBenchmark, decisionTime));
        return new(vix.Rejection, vix.Rejection is null ? vix.RiskScale : 0m, vix);
    }

    /// <summary>
    /// Live/paper per-stock query with the same reasons as the backtest calendar (<see cref="RegimeCalendar.RejectionFor"/>)
    /// and the same per-trade risk scale (<see cref="RegimeCalendar.EntryRiskScaleAt"/>): the benchmark must be on; under
    /// the Nasdaq home-market rule the stock's listing exchange must be known (no asset source, no asset record or no exchange
    /// refuses; a failed lookup refuses with its own reason) and a Nasdaq listing needs the home-market fund (QQQ) on; with
    /// VIX sizing the previous session's VIX close (FRED's latest file) decides refusal, half or full risk. Every missing
    /// piece fails closed.
    /// </summary>
    public async Task<RegimeEntryDecision> EntryDecisionAsync(
        RegimeRules regime,
        IMarketDataProvider provider,
        IReadOnlyList<string> availableIntervals,
        DateTimeOffset now,
        string ticker,
        IAssetTradingEligibilityProvider? assets,
        CancellationToken cancellationToken)
    {
        if (!regime.IsActive) return RegimeEntryDecision.Allowed;
        var (scheduleSucceeded, schedules) = await TryLoadRecentSchedulesAsync(provider, now, cancellationToken);
        if (!scheduleSucceeded)
            return RegimeEntryDecision.Refused(RegimeCalendar.MarketOffReason);

        var daily = await LoadRegimeBarsAsync(regime, provider, availableIntervals, now, cancellationToken);
        var validDaily = VixRegime.FilterTradingDayBars(daily, schedules);
        if (!IsFilteredBenchmarkOn(validDaily, regime, now, schedules))
            return RegimeEntryDecision.Refused(RegimeCalendar.MarketOffReason);

        string? exchange = null;
        var lookupFailed = false;
        IReadOnlyList<OhlcvBar>? homeBars = null;
        if (regime.UsesNasdaqHomeMarket)
        {
            (exchange, lookupFailed) = await TryLookupListingExchangeAsync(ticker, assets, cancellationToken);
            if (!lookupFailed && !String.IsNullOrWhiteSpace(exchange) &&
                exchange.Equals(RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase))
            {
                var homeRule = regime with { BenchmarkSymbol = regime.NasdaqListingBenchmark!, NasdaqListingBenchmark = null };
                homeBars = await LoadRegimeBarsAsync(homeRule, provider, availableIntervals, now, cancellationToken);
            }
            var preVix = Evaluate(regime with { VixSeries = null, VixFullRiskBelow = null, VixNoLongsAbove = null, VixReducedRiskScale = null },
                validDaily, now, exchange, lookupFailed, homeBars, null, schedules);
            if (preVix.Rejection is not null)
                return preVix;
        }

        var vix = regime.UsesVix ? await VixRegime.TryLoadLatestAsync(vixCloses, cancellationToken) : null;
        return Evaluate(regime, validDaily, now, exchange, lookupFailed, homeBars, vix, schedules);
    }

    /// <summary>Checks one benchmark's trading-day bars for freshness against <paramref name="schedules"/> and the SMA rule.</summary>
    private static bool IsBenchmarkOn(IReadOnlyList<OhlcvBar> bars, RegimeRules regime, DateTimeOffset decisionTime,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules) =>
        IsFilteredBenchmarkOn(VixRegime.FilterTradingDayBars(bars, schedules), regime, decisionTime, schedules);

    /// <summary>Checks already-filtered trading-day bars for schedule freshness and the SMA/rising-SMA rule.</summary>
    private static bool IsFilteredBenchmarkOn(IReadOnlyList<OhlcvBar> validBars, RegimeRules regime, DateTimeOffset decisionTime,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (validBars.Count == 0) return false;
        if (schedules is { Count: > 0 } &&
            !VixRegime.HasFreshBenchmarkSession(VixRegime.PreviousSession(validBars, decisionTime), schedules, decisionTime))
            return false;
        return RegimeCalendarBuilder.IsRegimeOnAt(validBars, regime.SmaPeriod, decisionTime, regime.SmaRisingLookbackBars);
    }

    /// <summary>
    /// Reads the recent authoritative exchange session window ([today - 15d, today + 1d] New York) when the market data
    /// provider supports <see cref="IMarketSessionScheduleProvider"/>. Returns Succeeded=true with null schedules for
    /// synthetic providers without a calendar, and Succeeded=false (fail closed) if a schedule-capable provider fails or
    /// returns an empty calendar.
    /// </summary>
    private static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadRecentSchedulesAsync(
        IMarketDataProvider provider, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (provider is not IMarketSessionScheduleProvider scheduleProvider)
            return (true, null);
        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        try
        {
            var schedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(
                nyToday.AddDays(-15), nyToday.AddDays(1), cancellationToken);
            return schedules.Count > 0 ? (true, schedules) : (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    /// <summary>Looks up a stock's listing exchange from the broker; non-cancellation failures return LookupFailed=true.</summary>
    private static async Task<(string? Exchange, bool LookupFailed)> TryLookupListingExchangeAsync(
        string ticker, IAssetTradingEligibilityProvider? assets, CancellationToken cancellationToken)
    {
        if (assets is null) return (null, false);
        try
        {
            return ((await assets.GetEligibilityAsync(ticker, cancellationToken))?.Exchange, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
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
