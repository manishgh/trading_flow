using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services;

/// <summary>One symbol's read-only OPRA implied-move versus completed 20-session ADR% comparison card.</summary>
public sealed record CatalystOptionsAdrCard(
    string Symbol,
    decimal? FrontImpliedMovePct,
    int? TradingDaysToFrontExpiry,
    decimal? Adr20Pct,
    DateOnly? AdrAsOfSessionDate,
    decimal? ImpliedMoveToAdrRatio,
    decimal? Skew25Delta,
    string RegimeLabel,
    string SummaryNote,
    string? AdrUnavailableReason = null,
    string ImpliedMoveBand = "unavailable");

/// <summary>Authenticated read-only Catalysts payload comparing OPRA implied moves to completed 20-day ADR%.</summary>
public sealed record CatalystOptionsAdrResponse(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<CatalystOptionsAdrCard> Cards);

/// <summary>Result of computing a symbol's completed-bar 20-session ADR% with fail-closed session/schedule validation.</summary>
public sealed record Adr20CalculationResult(
    decimal? Adr20Pct,
    DateOnly? AsOfSessionDate,
    string? UnavailableReason);

/// <summary>
/// Loads the Catalysts options cards and the 20-session ADR% shared with the Desk's selected-stock options line.
/// ADR% is computed with <see cref="SignalGenerator.ComputeAverageRangeAndDollarVolume"/> on completed bars over the exact
/// 20 exchange sessions; daily bars only change once a session, so each symbol's ADR is cached until the next session
/// completes, and concurrent requests for the same symbols share one download. Failures are logged with a reason.
/// </summary>
public sealed class CatalystOptionsAdrService(
    ConfigCatalogService catalog,
    PaperRuntimeFactory runtimeFactory,
    IOptionsSentimentSource optionsSource,
    AlpacaQuoteService quotes,
    TimeProvider clock,
    IMemoryCache cache,
    ILogger<CatalystOptionsAdrService> logger,
    ICandleStore? candleStore = null)
{
    internal const int RequiredAdrSessions = 20;
    internal const int MaxSymbolsPerRequest = 6;
    private static readonly string[] DefaultSymbols = ["SPY", "QQQ"];
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(6);
    private readonly SingleFlight<string, Dictionary<string, Adr20CalculationResult>> downloads = new(StringComparer.Ordinal);

    /// <summary>
    /// Parses and validates 1–6 US equity symbols from a comma-separated query string, defaulting to SPY and QQQ when omitted.
    /// Rejects malformed tokens or symbol counts above 6 before querying providers.
    /// </summary>
    public static bool TryParseSymbols(string? rawSymbols, out string[] symbols, out string? error)
    {
        if (string.IsNullOrWhiteSpace(rawSymbols))
        {
            symbols = DefaultSymbols;
            error = null;
            return true;
        }

        var parsed = new List<string>(MaxSymbolsPerRequest);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in rawSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.ToUpperInvariant();
            if (!DeskOptionsEndpoints.IsValidEquitySymbol(normalized))
            {
                symbols = [];
                error = $"Invalid stock symbol '{token}'. Expected 1-12 ASCII letters, digits, '.' or '-'.";
                return false;
            }

            if (seen.Add(normalized))
            {
                parsed.Add(normalized);
            }
        }

        if (parsed.Count == 0)
        {
            symbols = DefaultSymbols;
            error = null;
            return true;
        }

        if (parsed.Count > MaxSymbolsPerRequest)
        {
            symbols = [];
            error = $"At most {MaxSymbolsPerRequest} symbols may be requested at once.";
            return false;
        }

        symbols = [.. parsed];
        error = null;
        return true;
    }

    /// <summary>
    /// Computes completed-bar 20-session ADR% using <see cref="SignalGenerator.ComputeAverageRangeAndDollarVolume"/>
    /// (<c>mean(High - Low) / latestClose * 100</c>) over the exact 20 completed trading-day <c>1d</c> sessions known at
    /// <paramref name="now"/>. Fails closed (<c>Adr20Pct = null</c>) on failed schedules, missing intermediate sessions,
    /// stale sessions, fewer than 20 bars, or any bar with <c>Close &lt;= 0</c> or <c>High &lt; Low</c>.
    /// </summary>
    public static Adr20CalculationResult ComputeAdr20Pct(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        bool scheduleSucceeded = true)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (!scheduleSucceeded)
        {
            return new Adr20CalculationResult(null, null, "Exchange schedule unavailable");
        }

        var dailyBars = bars
            .Where(bar => bar.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var tradingBars = VixRegime.FilterTradingDayBars(dailyBars, schedules);
        var completedBars = tradingBars
            .Where(bar => DeskMarketData.IsCompletedBar(bar, now))
            .ToArray();

        if (completedBars.Length == 0)
        {
            return new Adr20CalculationResult(null, null, "No completed daily bar");
        }

        // Deduplicate by UTC session date without dropping corrupt bars so a bad D-1 or intermediate bar never silently falls back to older sessions.
        var bySessionDate = new Dictionary<DateOnly, OhlcvBar>();
        foreach (var bar in completedBars.OrderBy(item => item.Timestamp))
        {
            bySessionDate[DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)] = bar;
        }

        OhlcvBar[] window;
        DateOnly latestSession;
        if (schedules is { Count: > 0 })
        {
            IReadOnlyList<DateOnly> expectedSessions;
            try
            {
                expectedSessions = TechnicalFactorCalculator.ExpectedCompletedSessions(schedules, now, RequiredAdrSessions);
            }
            catch (InvalidDataException)
            {
                return new Adr20CalculationResult(null, null, "Authoritative exchange calendar coverage is incomplete");
            }

            latestSession = expectedSessions[^1];
            if (!VixRegime.HasFreshBenchmarkSession(VixRegime.PreviousSession(completedBars, now), schedules, now))
            {
                return new Adr20CalculationResult(null, null, "Daily bar session is stale");
            }

            window = new OhlcvBar[RequiredAdrSessions];
            for (var i = 0; i < RequiredAdrSessions; i++)
            {
                if (!bySessionDate.TryGetValue(expectedSessions[i], out var bar))
                {
                    return new Adr20CalculationResult(
                        null,
                        latestSession,
                        $"Missing completed daily bar for session {expectedSessions[i]:yyyy-MM-dd}");
                }

                window[i] = bar;
            }
        }
        else
        {
            var observedSession = VixRegime.PreviousSession(completedBars, now);
            if (observedSession is not { } session)
            {
                return new Adr20CalculationResult(null, null, "No completed daily bar");
            }

            latestSession = session;
            var ordered = bySessionDate.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            if (ordered.Length < RequiredAdrSessions)
            {
                return new Adr20CalculationResult(
                    null,
                    latestSession,
                    $"Fewer than {RequiredAdrSessions} completed daily sessions ({ordered.Length} available)");
            }

            window = ordered[^RequiredAdrSessions..];
        }

        // Reject any corrupt bar inside the 20-session window rather than silently dropping it and bridging with older history.
        foreach (var bar in window)
        {
            if (bar.Close <= 0m || bar.High < bar.Low)
            {
                return new Adr20CalculationResult(
                    null,
                    latestSession,
                    $"Invalid daily bar price on session {DateOnly.FromDateTime(bar.Timestamp.UtcDateTime):yyyy-MM-dd}");
            }
        }

        var (averageRangePct, _) = SignalGenerator.ComputeAverageRangeAndDollarVolume(
            window,
            window.Length - 1,
            RequiredAdrSessions);
        if (averageRangePct is not { } adr || adr <= 0m)
        {
            return new Adr20CalculationResult(null, latestSession, "20-session ADR% unavailable");
        }

        return new Adr20CalculationResult(Math.Round(adr, 2), latestSession, null);
    }

    /// <summary>
    /// Compares a one-day implied move with the 20-day average daily range using the shared options calculator:
    /// the front-expiry straddle move is divided by √(trading days to expiry) before dividing by ADR%, so a 5-day move is
    /// not compared with a 1-day range. Returns null when the move, the days to expiry or ADR% is missing or non-positive.
    /// </summary>
    public static decimal? ComputeDailyImpliedMoveToAdrRatio(OptionsSentimentSnapshot snapshot, decimal? adr20Pct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ratio = OptionsSentimentCalculator.WithAdr20Pct(snapshot, adr20Pct).ImpliedToRealizedAdrRatio;
        return ratio is { } value ? Math.Round(value, 2) : null;
    }

    /// <summary>
    /// Classifies a one-day expected move ÷ 20-day ADR ratio into a display band. About 0.5x is an ordinary day (a day's
    /// expected close-to-close move is roughly half its high-to-low range); under 0.75x normal, 0.75x–1.25x elevated,
    /// over 1.25x extreme. A rule of thumb for display, not calibrated on history and not a trading rule.
    /// </summary>
    public static string ClassifyImpliedMoveBand(decimal? ratio) => ratio switch
    {
        null or <= 0m => "unavailable",
        < 0.75m => "normal",
        <= 1.25m => "elevated",
        _ => "extreme"
    };

    private const int CalendarDaysBack = 65;
    private const int CalendarDaysForward = 65;

    /// <summary>
    /// Loads the cached exchange session calendar covering 65 days back and 65 days forward (spanning the 60 DTE OPRA
    /// expiry window) so Desk and Catalysts options cards count holiday-accurate trading days to expiry. Checks
    /// <see cref="IMemoryCache"/> before reading paper config or allocating a provider, deduplicates concurrent cold misses
    /// via <see cref="DeskMarketData.TryLoadSchedulesCachedAsync"/>, and disposes any created provider. Returns null on
    /// missing profile or provider outage so callers degrade gracefully to weekday counts.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?> TryLoadOptionsSchedulesAsync(CancellationToken deadlineToken)
    {
        try
        {
            var (succeeded, schedules) = await DeskMarketData.TryLoadSchedulesCachedAsync(
                () =>
                {
                    var profile = catalog.DefaultPaperConfig()?.Config;
                    return profile is null ? null : runtimeFactory.CreateProvider(profile);
                },
                clock.GetUtcNow().ToUniversalTime(),
                CalendarDaysBack,
                cache,
                deadlineToken,
                logger: logger,
                daysForward: CalendarDaysForward,
                disposeCreatedProvider: true);
            return succeeded ? schedules : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !deadlineToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Exchange calendar for options trading-day context could not be read ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Loads options snapshots, holiday-aware trading days, and ADR% for 1–6 symbols under a 6-second limit, degrading each symbol on its own.</summary>
    public async Task<CatalystOptionsAdrResponse> LoadAsync(string[] symbols, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);

        var adrTask = LoadAdrAsync(symbols, deadline.Token);
        var schedulesTask = TryLoadOptionsSchedulesAsync(deadline.Token);
        var spotHints = await DeskOptionsEndpoints.TryLoadSpotHintsAsync(quotes, symbols, deadline.Token, cancellationToken);
        var opraTasks = symbols.ToDictionary(
            symbol => symbol,
            symbol => TryLoadOptionsSnapshotAsync(symbol, spotHints.GetValueOrDefault(symbol), now, deadline.Token, cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(opraTasks.Values.Cast<Task>().Append(adrTask).Append(schedulesTask));
        var adrBySymbol = await adrTask;
        var schedules = await schedulesTask;

        var cards = new List<CatalystOptionsAdrCard>(symbols.Length);
        foreach (var symbol in symbols)
        {
            var rawSnapshot = await opraTasks[symbol];
            var adr = adrBySymbol.GetValueOrDefault(symbol) ?? new Adr20CalculationResult(null, null, "ADR% unavailable");
            var snapshot = OptionsSentimentCalculator.WithMarketContext(rawSnapshot, adr.Adr20Pct, schedules);
            decimal? ratio = snapshot.ImpliedToRealizedAdrRatio is { } value ? Math.Round(value, 2) : null;
            cards.Add(new CatalystOptionsAdrCard(symbol, snapshot.FrontImpliedMovePct, snapshot.TradingDaysToFrontExpiry, adr.Adr20Pct,
                adr.AsOfSessionDate, ratio, snapshot.Skew25Delta, snapshot.RegimeLabel, snapshot.SummaryNote, adr.UnavailableReason,
                ClassifyImpliedMoveBand(ratio)));
        }

        return new CatalystOptionsAdrResponse(ContractVersions.VersionOne, now, cards);
    }

    /// <summary>
    /// Returns each symbol's completed 20-session ADR%. Cached per symbol for the current New York date; symbols not
    /// cached are downloaded together, and a concurrent request for the same symbols joins that download instead of
    /// starting another. A failed calendar or download is reported per symbol and not cached.
    /// </summary>
    public async Task<Dictionary<string, Adr20CalculationResult>> LoadAdrAsync(IReadOnlyList<string> symbols, CancellationToken deadlineToken)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        var result = new Dictionary<string, Adr20CalculationResult>(StringComparer.OrdinalIgnoreCase);
        var nyDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        var missing = new List<string>();
        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cache.TryGetValue(AdrKey(symbol, nyDate), out Adr20CalculationResult? hit) && hit is not null) result[symbol] = hit;
            else missing.Add(symbol);
        }

        if (missing.Count == 0) return result;
        var flightKey = nyDate.ToString("yyyy-MM-dd") + ":" + string.Join(",", missing.Select(item => item.ToUpperInvariant()).Order(StringComparer.Ordinal));
        var download = downloads.RunAsync(flightKey, () => DownloadAdrAsync(missing, nyDate, now));
        try
        {
            foreach (var pair in await download.WaitAsync(deadlineToken)) result[pair.Key] = pair.Value;
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            foreach (var symbol in missing) result.TryAdd(symbol, new Adr20CalculationResult(null, null, "Daily bars did not arrive in time."));
        }

        return result;
    }

    // Cache key for one symbol's ADR on one New York date (a session's bars complete once per day).
    private static string AdrKey(string symbol, DateOnly nyDate) => $"adr20:{symbol.ToUpperInvariant()}:{nyDate:yyyy-MM-dd}";

    // Loads 65 days of daily bars through ICandleStore (allocating and disposing a provider lazily only on calendar/store miss)
    // under its own limit, computes ADR% and caches each successful result under that New York date's key; failures are logged and not cached.
    private async Task<Dictionary<string, Adr20CalculationResult>> DownloadAdrAsync(IReadOnlyList<string> symbols, DateOnly nyDate, DateTimeOffset now)
    {
        var result = new Dictionary<string, Adr20CalculationResult>(StringComparer.OrdinalIgnoreCase);
        var profile = catalog.DefaultPaperConfig()?.Config;
        if (profile is null)
        {
            foreach (var symbol in symbols) result[symbol] = new Adr20CalculationResult(null, null, "No paper profile is configured for market data.");
            return result;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var providerLock = new object();
        IMarketDataProvider? lazyProvider = null;
        // Creates the provider at most once per ADR download batch, only when calendar cache or ICandleStore actually misses.
        IMarketDataProvider GetOrCreateProvider()
        {
            lock (providerLock)
            {
                return lazyProvider ??= runtimeFactory.CreateProvider(profile);
            }
        }

        try
        {
            var (scheduleSucceeded, schedules) = await DeskMarketData.TryLoadSchedulesCachedAsync(
                () => runtimeFactory.CreateProvider(profile),
                now,
                CalendarDaysBack,
                cache,
                deadline.Token,
                logger: logger,
                daysForward: CalendarDaysForward,
                disposeCreatedProvider: true);
            if (!scheduleSucceeded)
            {
                foreach (var symbol in symbols) result[symbol] = new Adr20CalculationResult(null, null, "Exchange schedule unavailable");
                return result;
            }

            var bars = await DeskMarketData.LoadDailyBarsWithStoreAsync(
                GetOrCreateProvider,
                symbols,
                now.AddDays(-CalendarDaysBack),
                now,
                RequiredAdrSessions,
                schedules,
                candleStore,
                profile.Provider,
                deadline.Token,
                logger: logger);

            foreach (var symbol in symbols)
            {
                var computed = ComputeAdr20Pct(bars.GetValueOrDefault(symbol) ?? [], schedules, now, scheduleSucceeded: true);
                result[symbol] = computed;
                if (computed.UnavailableReason is null)
                {
                    cache.Set(AdrKey(symbol, nyDate), computed, TimeSpan.FromHours(12));
                }
                else
                {
                    logger.LogInformation("20-session ADR% unavailable for {Symbol}: {Reason}", symbol, computed.UnavailableReason);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Daily bars for ADR% could not be read for {Symbols} ({ErrorType}).", string.Join(",", symbols), ex.GetType().Name);
            foreach (var symbol in symbols) result.TryAdd(symbol, new Adr20CalculationResult(null, null, "Daily bars could not be read."));
        }
        finally
        {
            (lazyProvider as IDisposable)?.Dispose();
        }

        return result;
    }

    // Reads one symbol's options snapshot under the request limit, degrading to an explicit unavailable snapshot.
    private async Task<OptionsSentimentSnapshot> TryLoadOptionsSnapshotAsync(
        string symbol, decimal? spotHint, DateTimeOffset now, CancellationToken deadlineToken, CancellationToken callerToken)
    {
        try
        {
            return await optionsSource.GetSnapshotAsync(symbol, spotHint, cancellationToken: deadlineToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Options snapshot for {Symbol} timed out or failed ({ErrorType}).", symbol, ex.GetType().Name);
            return OptionsSentimentSnapshot.Unavailable(symbol, now, "OPRA options context timed out or unavailable.");
        }
    }
}
