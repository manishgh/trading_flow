using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Web.Services;

/// <summary>The regime rules the status strip shows, taken from the enabled strategy configurations.</summary>
/// <param name="Long">The long-side benchmark rule most enabled long strategies use, or null when none uses one.</param>
/// <param name="LongSource">Plain description of where <paramref name="Long"/> came from, for the tooltip.</param>
/// <param name="Short">The short-side benchmark rule most enabled short strategies use, or null when none uses one.</param>
/// <param name="ShortSource">Plain description of where <paramref name="Short"/> came from, for the tooltip.</param>
public sealed record WorkspaceRegimeRules(RegimeRules? Long, string LongSource, RegimeRules? Short, string ShortSource);

/// <summary>
/// Builds the workspace status strip: SPY/QQQ against the configured regime rules, the previous session's VIX close and
/// its sizing, and the live top-six sector funds. Display context only; each strategy still applies its own rules.
/// One shared build runs at a time and is reused for 60 seconds; a browser that disconnects cannot cancel it. Daily
/// benchmark bars are fetched once per completed session (they only change once a day), not on every refresh.
/// </summary>
public sealed class WorkspacePulseService(
    ConfigCatalogService catalog,
    PaperRuntimeFactory runtimeFactory,
    ProjectPaths paths,
    TimeProvider clock,
    IMemoryCache cache,
    ILogger<WorkspacePulseService> logger,
    IVixCloseSource? vixSource = null,
    ICandleStore? candleStore = null)
{
    private const string PulseCacheKey = "workspace-pulse";
    private static readonly TimeSpan PulseLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BenchmarkDeadline = TimeSpan.FromSeconds(10);

    // Windows .NET TLS to FRED can hang; FredVixCloseSource tries curl first on Windows, but the very first read after a
    // restart can still take over 20 seconds, so VIX gets its own limit instead of the benchmark limit.
    private static readonly TimeSpan VixDeadline = TimeSpan.FromSeconds(35);

    // A year of daily bars for eleven sector funds is fetched once per completed session.
    private static readonly TimeSpan SectorDeadline = TimeSpan.FromSeconds(20);

    private readonly SingleFlight<string, WorkspaceRegimePulseResponse> builds = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the cached strip while fresh; otherwise joins (or starts) the single shared build. The caller's token only
    /// stops this caller's wait, never the shared build.
    /// </summary>
    public async Task<WorkspaceRegimePulseResponse> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(PulseCacheKey, out WorkspaceRegimePulseResponse? hit) && hit is not null) return hit;
        return await builds.RunAsync(PulseCacheKey, BuildAndCacheAsync).WaitAsync(cancellationToken);
    }

    // Builds the strip without a caller token and stores it for 60 seconds.
    private async Task<WorkspaceRegimePulseResponse> BuildAndCacheAsync()
    {
        var pulse = await BuildAsync(clock.GetUtcNow().ToUniversalTime());
        cache.Set(PulseCacheKey, pulse, PulseLifetime);
        return pulse;
    }

    // Runs the benchmark, VIX and sector parts in parallel under their own limits; each part fails closed to an explicit
    // "unavailable" value with a logged reason rather than failing the whole strip.
    private async Task<WorkspaceRegimePulseResponse> BuildAsync(DateTimeOffset now)
    {
        var rules = await ResolveRulesAsync();
        using var vixDeadline = new CancellationTokenSource(VixDeadline);
        var vixTask = LoadVixAsync(vixDeadline.Token);

        var spyPulse = UnavailableBenchmark("SPY");
        var qqqPulse = UnavailableBenchmark("QQQ");
        DateOnly? previousSession = null;
        var scheduleSucceeded = true;
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null;
        var sectors = new SectorLeaderRanking(null, [], "Market data provider is not configured.");

        var defaultProfile = catalog.DefaultPaperConfig()?.Config;
        if (defaultProfile is not null)
        {
            using var benchmarkDeadline = new CancellationTokenSource(BenchmarkDeadline);
            using var sectorDeadline = new CancellationTokenSource(SectorDeadline);
            var requiredCloses = Math.Max(rules.Long?.RequiredCloses ?? 0, rules.Short?.RequiredCloses ?? 0);
            // Cover all 254 completed sector sessions and any benchmark regime lookback so ICandleStore gap checks
            // verify every single expected session across the entire window rather than only the newest two weeks.
            var calendarDaysBack = Math.Max(420, requiredCloses * 2 + 45);
            var providerLock = new object();
            IMarketDataProvider? lazyProvider = null;
            // Creates the provider at most once per strip build, only when a calendar or candle-store miss actually requires it.
            IMarketDataProvider GetOrCreateProvider()
            {
                lock (providerLock)
                {
                    return lazyProvider ??= runtimeFactory.CreateProvider(defaultProfile);
                }
            }

            try
            {
                try
                {
                    (scheduleSucceeded, schedules) = await DeskMarketData.TryLoadSchedulesCachedAsync(
                        () => runtimeFactory.CreateProvider(defaultProfile),
                        now,
                        calendarDaysBack,
                        cache,
                        benchmarkDeadline.Token,
                        logger,
                        disposeCreatedProvider: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Market data provider or exchange calendar unavailable for the regime strip ({ErrorType}).", ex.GetType().Name);
                    scheduleSucceeded = false;
                    schedules = null;
                }

                if (scheduleSucceeded)
                {
                    DateOnly? expectedSession = schedules is { Count: > 0 } && VixRegime.TryExpectedPreviousSession(schedules, now, out var expected)
                        ? expected : null;
                    var providerName = defaultProfile.Provider;
                    var sectorTask = LoadSectorsAsync(GetOrCreateProvider, providerName, schedules, expectedSession, now, sectorDeadline.Token);
                    var spyTask = LoadBenchmarkOrReasonAsync(GetOrCreateProvider, providerName, "SPY", requiredCloses, expectedSession, schedules, now, benchmarkDeadline.Token);
                    var qqqTask = LoadBenchmarkOrReasonAsync(GetOrCreateProvider, providerName, "QQQ", requiredCloses, expectedSession, schedules, now, benchmarkDeadline.Token);
                    var (spyBars, spyReason) = await spyTask;
                    var (qqqBars, qqqReason) = await qqqTask;
                    spyPulse = spyBars is null ? UnavailableBenchmark("SPY", spyReason ?? "Unavailable") : EvaluateBenchmark("SPY", spyBars, schedules, now, rules);
                    qqqPulse = qqqBars is null ? UnavailableBenchmark("QQQ", qqqReason ?? "Unavailable") : EvaluateBenchmark("QQQ", qqqBars, schedules, now, rules);
                    previousSession = spyPulse.Available ? spyPulse.LastSessionDate : null;
                    sectors = await sectorTask;
                }
                else
                {
                    spyPulse = UnavailableBenchmark("SPY", "Exchange schedule unavailable");
                    qqqPulse = UnavailableBenchmark("QQQ", "Exchange schedule unavailable");
                    sectors = new SectorLeaderRanking(null, [], "Exchange schedule unavailable.");
                }
            }
            finally
            {
                (lazyProvider as IDisposable)?.Dispose();
            }
        }

        var vixPulse = BuildVixPulse(await vixTask, previousSession, now, scheduleSucceeded, schedules, rules);
        return new WorkspaceRegimePulseResponse(
            ContractVersions.VersionOne,
            now,
            spyPulse,
            qqqPulse,
            vixPulse,
            sectors.Leaders.Select(leader => leader.Symbol).ToArray(),
            sectors.AsOfSession?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sectors.Leaders,
            sectors.UnavailableReason,
            rules.LongSource,
            rules.ShortSource);
    }

    /// <summary>
    /// Picks the regime rule most enabled long strategies use and the one most enabled short strategies use, so the strip
    /// follows the strategy configurations instead of a separate hard-coded copy. The source text says how many
    /// strategies share the rule when they differ.
    /// </summary>
    internal async Task<WorkspaceRegimeRules> ResolveRulesAsync()
    {
        IReadOnlyList<RegimeRules> active;
        try
        {
            var strategies = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest);
            active = strategies.Select(option => option.Definition.Regime)
                .Where(rule => rule is { IsActive: true })
                .Select(rule => rule!)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Strategy catalog could not be read for the regime strip ({ErrorType}).", ex.GetType().Name);
            active = [];
        }

        return SelectRules(active);
    }

    /// <summary>Chooses the most common long and short rule among the given active strategy rules.</summary>
    internal static WorkspaceRegimeRules SelectRules(IReadOnlyList<RegimeRules> active)
    {
        // Groups one side's rules and returns the most common one with a plain sentence saying how many strategies share it.
        (RegimeRules? Rule, string Source) Pick(IEnumerable<RegimeRules> candidates, string side)
        {
            var groups = candidates.Select(StripRule).GroupBy(rule => rule).OrderByDescending(group => group.Count()).ToArray();
            if (groups.Length == 0) return (null, $"No enabled {side} strategy uses a market regime rule.");
            var total = groups.Sum(group => group.Count());
            var chosen = groups[0];
            var source = groups.Length == 1
                ? $"Rule shared by all {total} enabled {side} strategies with a regime rule."
                : $"Rule used by {chosen.Count()} of {total} enabled {side} strategies; the others use different settings.";
            return (chosen.Key, source);
        }

        var (longRule, longSource) = Pick(active.Where(rule => !rule.IsBearishRule), "long");
        var (shortRule, shortSource) = Pick(active.Where(rule => rule.IsBearishRule), "short");
        return new WorkspaceRegimeRules(longRule, longSource, shortRule, shortSource);
    }

    // Keeps the parts the strip evaluates (benchmark rule and VIX thresholds) and drops the Nasdaq home-market link,
    // which applies per stock, so strategies that differ only in that link count as one rule.
    private static RegimeRules StripRule(RegimeRules rule) => rule with { NasdaqListingBenchmark = null };

    /// <summary>
    /// Evaluates one benchmark against the configured long and short rules on completed trading-day bars, failing closed
    /// on missing, stale or insufficient history. VIX thresholds are left out here; the VIX chip shows them separately.
    /// </summary>
    internal static BenchmarkRegimePulse EvaluateBenchmark(
        string symbol,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        WorkspaceRegimeRules rules)
    {
        if (rules.Long is null && rules.Short is null) return UnavailableBenchmark(symbol, "No strategy uses a regime rule");
        var completedBars = VixRegime.FilterTradingDayBars(bars, schedules).Where(bar => DeskMarketData.IsCompletedBar(bar, now)).ToArray();
        if (VixRegime.PreviousSession(completedBars, now) is not { } session) return UnavailableBenchmark(symbol, "No completed daily bar");
        if (!VixRegime.HasFreshBenchmarkSession(session, schedules, now)) return UnavailableBenchmark(symbol, "Benchmark session is stale");
        var required = Math.Max(rules.Long?.RequiredCloses ?? 0, rules.Short?.RequiredCloses ?? 0);
        if (completedBars.Length < required) return UnavailableBenchmark(symbol, "Insufficient completed daily bars");

        // Daily bars are keyed at UTC midnight; match the UTC date directly instead of shifting to New York time.
        var latestBar = completedBars.LastOrDefault(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime) == session);
        if (latestBar is null) return UnavailableBenchmark(symbol, "Latest session close missing");

        var longOn = rules.Long is { } longRule && RegimeGateService.Evaluate(ForBenchmark(longRule, symbol), completedBars, now, schedules: schedules).Rejection is null;
        var shortOn = rules.Short is { } shortRule && RegimeGateService.Evaluate(ForBenchmark(shortRule, symbol), completedBars, now, schedules: schedules).Rejection is null;
        // Each side names its own average length, in case the long and short rules ever differ.
        var longPeriod = rules.Long?.SmaPeriod;
        var shortPeriod = rules.Short?.SmaPeriod;
        var shownPeriod = shortOn ? shortPeriod ?? longPeriod : longPeriod ?? shortPeriod;
        var label = longOn ? $"Close above a rising {longPeriod}-day average: the configured rule allows new longs, not shorts."
            : shortOn ? $"Close below a falling {shortPeriod}-day average: the configured rule allows new shorts, not longs."
            : longPeriod == shortPeriod || shortPeriod is null || longPeriod is null
                ? $"Close and {shownPeriod}-day average point different ways: the configured rule allows neither."
                : $"Neither the {longPeriod}-day long rule nor the {shortPeriod}-day short rule holds: no new longs or shorts.";
        return new BenchmarkRegimePulse(symbol, true, longOn, shortOn, Math.Round(latestBar.Close, 2), session, label, $"{shownPeriod}-day");
    }

    // Applies a strategy's rule to one benchmark without its per-stock Nasdaq link or VIX sizing.
    private static RegimeRules ForBenchmark(RegimeRules rule, string symbol) => rule with
    {
        BenchmarkSymbol = symbol, NasdaqListingBenchmark = null, VixSeries = null, VixFullRiskBelow = null,
        VixNoLongsAbove = null, VixReducedRiskScale = null, VixNoShortsBelow = null, VixShortFullRiskAbove = null
    };

    /// <summary>Resolves the exact previous completed exchange session for the VIX lookup without walking back across days.</summary>
    internal static DateOnly? ResolveVixSession(
        DateOnly? benchmarkPreviousSession,
        bool scheduleSucceeded,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now)
    {
        if (!scheduleSucceeded) return null;
        if (benchmarkPreviousSession is { } session)
        {
            return schedules is { Count: > 0 } && !VixRegime.HasFreshBenchmarkSession(session, schedules, now) ? null : session;
        }

        return schedules is { Count: > 0 } && VixRegime.TryExpectedPreviousSession(schedules, now, out var expected) ? expected : null;
    }

    /// <summary>
    /// Computes long and short VIX risk scales from the verified previous session's FRED close using the configured rules'
    /// thresholds. When that close is not published yet, the newest published close is returned for display only.
    /// </summary>
    internal static VixRegimePulse BuildVixPulse(
        VixCloseSeries? series,
        DateOnly? benchmarkPreviousSession,
        DateTimeOffset now,
        bool scheduleSucceeded,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        WorkspaceRegimeRules rules)
    {
        if (!scheduleSucceeded) return new VixRegimePulse(false, null, null, 0m, "VIX schedule unavailable", 0m, "VIX schedule unavailable");
        if (series is null) return new VixRegimePulse(false, benchmarkPreviousSession, null, 0m, "VIX unavailable", 0m, "VIX unavailable");
        if (ResolveVixSession(benchmarkPreviousSession, scheduleSucceeded, schedules, now) is not { } targetSession)
            return new VixRegimePulse(false, null, null, 0m, "VIX session unknown", 0m, "VIX session unknown");
        if (!series.TryGetClose(targetSession, out var close))
        {
            decimal? latestClose = series.LastDate is { } lastDate && series.TryGetClose(lastDate, out var lastClose) ? Math.Round(lastClose, 2) : null;
            return new VixRegimePulse(false, targetSession, null, 0m, "VIX close pending", 0m, "VIX close pending",
                latestClose, latestClose is null ? null : series.LastDate);
        }

        var (longScale, longLabel) = rules.Long is { UsesLongVix: true } longRule ? Side(longRule, series, targetSession, "Longs") : (1m, "Longs: no VIX sizing configured");
        var (shortScale, shortLabel) = rules.Short is { UsesShortVix: true } shortRule ? Side(shortRule, series, targetSession, "Shorts") : (1m, "Shorts: no VIX sizing configured");
        return new VixRegimePulse(true, targetSession, Math.Round(close, 2), longScale, longLabel, shortScale, shortLabel);
    }

    // Applies one side's configured VIX thresholds through the shared VixRegime decision and names the outcome with the
    // configured numbers (for example "Longs 0.5x (VIX 18-22)").
    private static (decimal Scale, string Label) Side(RegimeRules rule, VixCloseSeries series, DateOnly session, string side)
    {
        var decision = VixRegime.Decide(rule, series, session);
        var band = rule.IsBearishRule
            ? $"off below {rule.VixNoShortsBelow:0.##}, full above {rule.VixShortFullRiskAbove:0.##}"
            : $"full below {rule.VixFullRiskBelow:0.##}, off above {rule.VixNoLongsAbove:0.##}";
        if (decision.Rejection is not null) return (0m, $"{side} off ({band})");
        return decision.RiskScale < 1m
            ? (decision.RiskScale, $"{side} {decision.RiskScale:0.##}x ({band})")
            : (1m, $"{side} full size ({band})");
    }

    // Loads a benchmark's daily bars once per completed session through ICandleStore (reusing persisted native completed 1d
    // bars when complete), invoking providerFactory lazily only on cache/store miss.
    private async Task<IReadOnlyList<OhlcvBar>> LoadBenchmarkBarsAsync(
        Func<IMarketDataProvider> providerFactory,
        string providerName,
        string symbol,
        int requiredCloses,
        DateOnly? expectedSession,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        CancellationToken token)
    {
        var key = $"workspace-pulse-bars:{symbol}:{expectedSession:yyyy-MM-dd}:{requiredCloses}";
        if (expectedSession is not null && cache.TryGetValue(key, out IReadOnlyList<OhlcvBar>? cached) && cached is not null) return cached;
        var start = now.AddDays(-Math.Max(170, requiredCloses * 2 + 40));
        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            providerFactory,
            [symbol],
            start,
            now,
            Math.Max(1, requiredCloses),
            schedules,
            candleStore,
            providerName,
            token,
            allowHourlyFallback: true,
            vixSource: vixSource,
            logger: logger);
        var bars = VixRegime.FilterTradingDayBars(loaded.GetValueOrDefault(symbol) ?? [], schedules);
        var latest = VixRegime.PreviousSession(bars.Where(bar => DeskMarketData.IsCompletedBar(bar, now)).ToArray(), now);
        if (expectedSession is not null && latest == expectedSession) cache.Set(key, bars, TimeSpan.FromHours(20));
        return bars;
    }

    // Loads one benchmark's bars, turning a timeout or provider error into a plain reason for that benchmark only.
    private async Task<(IReadOnlyList<OhlcvBar>? Bars, string? Reason)> LoadBenchmarkOrReasonAsync(
        Func<IMarketDataProvider> providerFactory,
        string providerName,
        string symbol,
        int requiredCloses,
        DateOnly? expectedSession,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        CancellationToken token)
    {
        try
        {
            return (await LoadBenchmarkBarsAsync(providerFactory, providerName, symbol, requiredCloses, expectedSession, schedules, now, token), null);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("{Symbol} daily bars did not arrive within {Seconds} seconds.", symbol, BenchmarkDeadline.TotalSeconds);
            return (null, $"{symbol} daily bars did not arrive within {BenchmarkDeadline.TotalSeconds:0} seconds");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Symbol} daily bars could not be read ({ErrorType}).", symbol, ex.GetType().Name);
            return (null, $"{symbol} daily bars could not be read");
        }
    }

    // Loads FRED VIX closes under the VIX limit, degrading a timeout to "unavailable" with a logged reason.
    private async Task<VixCloseSeries?> LoadVixAsync(CancellationToken token)
    {
        try
        {
            return await VixRegime.TryLoadLatestAsync(vixSource, token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("FRED VIX closes did not arrive within {Seconds} seconds; the VIX chip shows unavailable.", VixDeadline.TotalSeconds);
            return null;
        }
    }

    // Ranks the configured sector funds for the expected latest completed session, returning a reason on failure.
    private async Task<SectorLeaderRanking> LoadSectorsAsync(
        Func<IMarketDataProvider> providerFactory,
        string providerName,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateOnly? expectedSession,
        DateTimeOffset now,
        CancellationToken token)
    {
        try
        {
            return await SectorLeadersService.GetLeadersAsync(providerFactory, Path.Combine(paths.ConfigsRoot, "research", "sector-funds.txt"),
                expectedSession, schedules, now, cache, token, candleStore, providerName, logger);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Sector fund bars did not arrive within {Seconds} seconds.", SectorDeadline.TotalSeconds);
            return new SectorLeaderRanking(null, [], $"Sector fund bars did not arrive within {SectorDeadline.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sector fund bars could not be read ({ErrorType}).", ex.GetType().Name);
            return new SectorLeaderRanking(null, [], "Sector fund bars could not be read: " + ex.Message);
        }
    }

    // Builds a fail-closed unavailable benchmark pulse with an explicit reason.
    private static BenchmarkRegimePulse UnavailableBenchmark(string symbol, string reason = "Unavailable") =>
        new(symbol, false, false, false, null, null, reason, null);
}
