using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

/// <summary>
/// Read-only summary of one index benchmark's 50-session moving-average regime for the workspace header strip.
/// </summary>
public sealed record BenchmarkRegimePulse(
    string Symbol,
    bool Available,
    bool LongGateOn,
    bool ShortGateOn,
    decimal? LastClose,
    DateOnly? LastSessionDate,
    string SummaryLabel);

/// <summary>
/// Read-only summary of the previous session's FRED VIXCLS close and its resulting swing risk multipliers.
/// </summary>
public sealed record VixRegimePulse(
    bool Available,
    DateOnly? SessionDate,
    decimal? Close,
    decimal LongRiskScale,
    string LongLabel,
    decimal ShortRiskScale,
    string ShortLabel);

/// <summary>
/// Authenticated workspace-wide market regime, VIX sizing, and leading-sector snapshot displayed in the top bar.
/// </summary>
public sealed record WorkspaceRegimePulseResponse(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    BenchmarkRegimePulse Spy,
    BenchmarkRegimePulse Qqq,
    VixRegimePulse Vix,
    IReadOnlyList<string> TopSectors,
    string? TopSectorsAsOfDate);

/// <summary>
/// Maps <c>GET /api/v1/workspace/regime-pulse</c> using the shared <see cref="RegimeGateService"/> and
/// <see cref="VixRegime"/> evaluators with a short 60-second read cache so every screen shows identical regime facts.
/// </summary>
public static class WorkspacePulseEndpoints
{
    private sealed record CachedPulseEntry(WorkspaceRegimePulseResponse Payload, DateTimeOffset ExpiresAtUtc);

    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static volatile CachedPulseEntry? cachedPulse;

    private static readonly RegimeRules CanonicalLongSpyRule = new(
        BenchmarkSymbol: "SPY",
        Rule: RegimeRules.PriceAboveSma,
        SmaPeriod: 50,
        SmaRisingLookbackBars: 20,
        VixSeries: RegimeRules.FredVixSeries,
        VixFullRiskBelow: 18m,
        VixNoLongsAbove: 22m,
        VixReducedRiskScale: 0.5m);

    private static readonly RegimeRules CanonicalShortSpyRule = new(
        BenchmarkSymbol: "SPY",
        Rule: RegimeRules.PriceBelowFallingSma,
        SmaPeriod: 50,
        VixSeries: RegimeRules.FredVixSeries,
        VixReducedRiskScale: 0.5m,
        SmaFallingLookbackBars: 20,
        VixNoShortsBelow: 18m,
        VixShortFullRiskAbove: 22m);

    /// <summary>Registers the read-only workspace regime pulse endpoint on the authenticated v1 API group.</summary>
    public static void MapWorkspacePulseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/workspace/regime-pulse", async (
            ConfigCatalogService catalog,
            PaperRuntimeFactory runtimeFactory,
            ProjectPaths paths,
            TimeProvider clock,
            IVixCloseSource? vixSource,
            CancellationToken cancellationToken) =>
        {
            var now = clock.GetUtcNow().ToUniversalTime();
            if (cachedPulse is { } hit && hit.ExpiresAtUtc > now)
            {
                return Results.Ok(hit.Payload);
            }

            await CacheGate.WaitAsync(cancellationToken);
            try
            {
                now = clock.GetUtcNow().ToUniversalTime();
                if (cachedPulse is { } rechecked && rechecked.ExpiresAtUtc > now)
                {
                    return Results.Ok(rechecked.Payload);
                }

                var computed = await BuildPulseAsync(catalog, runtimeFactory, paths, vixSource, now, cancellationToken);
                cachedPulse = new CachedPulseEntry(computed, now.AddSeconds(60));
                return Results.Ok(computed);
            }
            finally
            {
                CacheGate.Release();
            }
        });
    }

    // Evaluates SPY, QQQ, FRED VIXCLS, and the latest published Top-3 sector screen under a bounded 6-second timeout.
    private static async Task<WorkspaceRegimePulseResponse> BuildPulseAsync(
        ConfigCatalogService catalog,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        IVixCloseSource? vixSource,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));

        var spyPulse = UnavailableBenchmark("SPY");
        var qqqPulse = UnavailableBenchmark("QQQ");
        DateOnly? previousSession = null;
        var scheduleSucceeded = true;
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null;

        var defaultProfile = catalog.DefaultPaperConfig()?.Config;
        if (defaultProfile is not null)
        {
            try
            {
                var provider = runtimeFactory.CreateProvider(defaultProfile);
                var gate = new RegimeGateService(vixSource);
                var start = now.AddDays(-170);
                (scheduleSucceeded, schedules) = await TryLoadSchedulesAsync(provider, now, deadline.Token, cancellationToken);
                if (scheduleSucceeded)
                {
                    var spyTask = gate.LoadBenchmarkDailyBarsAsync(provider, "SPY", ["1d", "1h"], start, now, deadline.Token);
                    var qqqTask = gate.LoadBenchmarkDailyBarsAsync(provider, "QQQ", ["1d", "1h"], start, now, deadline.Token);
                    await Task.WhenAll(spyTask, qqqTask);

                    var spyBars = VixRegime.FilterTradingDayBars(await spyTask, schedules);
                    var qqqBars = VixRegime.FilterTradingDayBars(await qqqTask, schedules);
                    spyPulse = EvaluateBenchmark("SPY", spyBars, schedules, now);
                    qqqPulse = EvaluateBenchmark("QQQ", qqqBars, schedules, now);
                    previousSession = spyPulse.Available ? spyPulse.LastSessionDate : null;
                }
                else
                {
                    spyPulse = UnavailableBenchmark("SPY", "Exchange schedule unavailable");
                    qqqPulse = UnavailableBenchmark("QQQ", "Exchange schedule unavailable");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Preserve fail-closed unavailable badges when market data is offline or unconfigured.
                scheduleSucceeded = false;
                schedules = null;
                previousSession = null;
            }
        }

        VixCloseSeries? vixSeries = null;
        try
        {
            vixSeries = await VixRegime.TryLoadLatestAsync(vixSource, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Degrade to an unavailable VIX badge on deadline timeout instead of failing the request with HTTP 500.
            vixSeries = null;
        }

        var vixPulse = BuildVixPulse(vixSeries, previousSession, now, scheduleSucceeded, schedules);
        var (topSectors, sectorsDate) = TryReadLatestTopSectors(paths.ResearchScreensRoot);

        return new WorkspaceRegimePulseResponse(
            ContractVersions.VersionOne,
            now,
            spyPulse,
            qqqPulse,
            vixPulse,
            topSectors,
            sectorsDate);
    }

    // Evaluates both bullish (rising 50-SMA) and bearish (falling 50-SMA) gates on completed trading-day bars, failing closed on missing, stale, or insufficient history.
    internal static BenchmarkRegimePulse EvaluateBenchmark(
        string symbol,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now)
    {
        var tradingBars = VixRegime.FilterTradingDayBars(bars, schedules);
        var completedBars = tradingBars.Where(bar => IsCompletedBar(bar, now)).ToArray();
        var latestSession = VixRegime.PreviousSession(completedBars, now);
        if (latestSession is not { } session)
        {
            return UnavailableBenchmark(symbol, "No completed daily bar");
        }

        if (!VixRegime.HasFreshBenchmarkSession(session, schedules, now))
        {
            return UnavailableBenchmark(symbol, "Benchmark session is stale");
        }

        if (completedBars.Length < CanonicalLongSpyRule.RequiredCloses)
        {
            return UnavailableBenchmark(symbol, "Insufficient completed daily bars");
        }

        // Daily bars are keyed at UTC midnight; match UTC date directly instead of shifting to New York time.
        var latestBar = completedBars.LastOrDefault(b => DateOnly.FromDateTime(b.Timestamp.UtcDateTime) == session);
        if (latestBar is null)
        {
            return UnavailableBenchmark(symbol, "Latest session close missing");
        }

        var longRule = new RegimeRules(symbol, RegimeRules.PriceAboveSma, 50, SmaRisingLookbackBars: 20);
        var shortRule = new RegimeRules(symbol, RegimeRules.PriceBelowFallingSma, 50, SmaFallingLookbackBars: 20);
        var longOn = RegimeGateService.Evaluate(longRule, completedBars, now, schedules: schedules).Rejection is null;
        var shortOn = RegimeGateService.Evaluate(shortRule, completedBars, now, schedules: schedules).Rejection is null;
        var label = longOn ? "Rising 50-SMA (Longs ON)"
            : shortOn ? "Falling 50-SMA (Shorts ON)"
            : "Neutral / Gate OFF";

        return new BenchmarkRegimePulse(
            symbol,
            Available: true,
            LongGateOn: longOn,
            ShortGateOn: shortOn,
            LastClose: Math.Round(latestBar.Close, 2),
            LastSessionDate: session,
            SummaryLabel: label);
    }

    // Returns true only when a bar has both finished its timeframe window and become known by the observation instant.
    private static bool IsCompletedBar(OhlcvBar bar, DateTimeOffset now) =>
        bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= now &&
        (bar.KnownAtUtc is null || bar.KnownAtUtc <= now);

    // Resolves the exact previous completed exchange session for VIX lookup without walking back across arbitrary days.
    internal static DateOnly? ResolveVixSession(
        DateOnly? benchmarkPreviousSession,
        bool scheduleSucceeded,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now)
    {
        if (!scheduleSucceeded)
        {
            return null;
        }

        if (benchmarkPreviousSession is { } session)
        {
            if (schedules is { Count: > 0 } && !VixRegime.HasFreshBenchmarkSession(session, schedules, now))
            {
                return null;
            }

            return session;
        }

        if (schedules is { Count: > 0 } && VixRegime.TryExpectedPreviousSession(schedules, now, out var expected))
        {
            return expected;
        }

        return null;
    }

    // Computes long and short VIX risk scales strictly from the verified previous exchange session's FRED VIXCLS close.
    internal static VixRegimePulse BuildVixPulse(
        VixCloseSeries? series,
        DateOnly? benchmarkPreviousSession,
        DateTimeOffset now,
        bool scheduleSucceeded = true,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null)
    {
        if (!scheduleSucceeded)
        {
            return new VixRegimePulse(false, null, null, 0m, "VIX schedule unavailable", 0m, "VIX schedule unavailable");
        }

        if (series is null)
        {
            return new VixRegimePulse(false, benchmarkPreviousSession, null, 0m, "VIX unavailable", 0m, "VIX unavailable");
        }

        var targetSession = ResolveVixSession(benchmarkPreviousSession, scheduleSucceeded, schedules, now);
        if (targetSession is null)
        {
            return new VixRegimePulse(false, null, null, 0m, "VIX session unknown", 0m, "VIX session unknown");
        }

        var longDecision = VixRegime.Decide(CanonicalLongSpyRule, series, targetSession);
        var shortDecision = VixRegime.Decide(CanonicalShortSpyRule, series, targetSession);
        if (longDecision.Close is not { } close)
        {
            return new VixRegimePulse(false, targetSession, null, 0m, "VIX close pending", 0m, "VIX close pending");
        }

        var longLabel = longDecision.Rejection is not null ? "Longs OFF (>22)"
            : longDecision.RiskScale < 1m ? $"Longs {longDecision.RiskScale:0.##}x (Half)"
            : "Longs 1.0x (Full)";
        var shortLabel = shortDecision.Rejection is not null ? "Shorts OFF (<18)"
            : shortDecision.RiskScale < 1m ? $"Shorts {shortDecision.RiskScale:0.##}x (Half)"
            : "Shorts 1.0x (Full)";

        return new VixRegimePulse(
            true,
            targetSession,
            Math.Round(close, 2),
            longDecision.Rejection is null ? longDecision.RiskScale : 0m,
            longLabel,
            shortDecision.Rejection is null ? shortDecision.RiskScale : 0m,
            shortLabel);
    }

    // Loads recent authoritative exchange schedules when supported by the provider, failing closed on empty results or errors.
    internal static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesAsync(
        IMarketDataProvider provider,
        DateTimeOffset now,
        CancellationToken deadlineToken,
        CancellationToken callerToken = default)
    {
        if (provider is not IMarketSessionScheduleProvider scheduleProvider)
        {
            return (true, null);
        }

        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        try
        {
            var schedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(
                nyToday.AddDays(-15), nyToday.AddDays(1), deadlineToken);
            return schedules.Count > 0 ? (true, schedules) : (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    // Reads the final session's Top-3 ranked sector ETFs from any published sector-funds-top3 research screen on disk.
    internal static (IReadOnlyList<string> Sectors, string? AsOfDate) TryReadLatestTopSectors(string screensDir)
    {
        try
        {
            if (!Directory.Exists(screensDir))
            {
                return ([], null);
            }

            var candidateFolder = Directory.GetDirectories(screensDir, "sector-funds-top3*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidateFolder is null)
            {
                return ([], null);
            }

            var eligiblePath = Path.Combine(candidateFolder, "eligible.csv");
            if (!File.Exists(eligiblePath))
            {
                return ([], null);
            }

            // Read only the tail lines of eligible.csv so this stays O(1) regardless of multi-year screen length.
            var tailLines = ReadTailLines(eligiblePath, maxBytes: 4096);
            string? latestDate = null;
            var symbols = new List<string>(3);
            for (var i = tailLines.Count - 1; i >= 0; i--)
            {
                var line = tailLines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("session_date,", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 2)
                {
                    continue;
                }

                var rowDate = parts[0].Trim();
                var ticker = parts[1].Trim().ToUpperInvariant();
                if (latestDate is null)
                {
                    latestDate = rowDate;
                }
                else if (!string.Equals(rowDate, latestDate, StringComparison.Ordinal))
                {
                    break;
                }

                symbols.Add(ticker);
            }

            symbols.Reverse();
            return (symbols, latestDate);
        }
        catch
        {
            return ([], null);
        }
    }

    // Reads the trailing UTF-8 lines from a CSV file without loading the full file into memory.
    private static List<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var readBytes = (int)Math.Min(length, maxBytes);
        stream.Seek(length - readBytes, SeekOrigin.Begin);
        var buffer = new byte[readBytes];
        var totalRead = 0;
        while (totalRead < readBytes)
        {
            var n = stream.Read(buffer, totalRead, readBytes - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (length > readBytes && lines.Count > 1)
        {
            // Drop potentially partial first line when seeking into the middle of the file.
            lines.RemoveAt(0);
        }

        return lines;
    }

    // Builds a fail-closed unavailable benchmark pulse with an explicit diagnostic label.
    private static BenchmarkRegimePulse UnavailableBenchmark(string symbol, string reason = "Unavailable") =>
        new(symbol, Available: false, LongGateOn: false, ShortGateOn: false, null, null, reason);
}
