using System.Collections.Concurrent;
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
    string? AdrUnavailableReason = null);

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
/// Read-only calculator and loader for Catalysts OPRA implied-move vs completed 20-session ADR% ratio cards.
/// Delegates ADR% math to <see cref="SignalGenerator.ComputeAverageRangeAndDollarVolume"/>, enforces completed-bar causality,
/// authoritative exchange session continuity, bounded timeouts, and a short bounded in-memory ADR cache (never caching failed schedules).
/// </summary>
public static class CatalystOptionsAdrService
{
    private sealed record CachedAdrEntry(Adr20CalculationResult Result, DateTimeOffset ExpiresAtUtc);

    internal const int RequiredAdrSessions = 20;
    internal const int MaxSymbolsPerRequest = 6;
    private const int MaxCacheEntries = 256;
    private static readonly TimeSpan AdrCacheTtl = TimeSpan.FromMinutes(3);
    private static readonly string[] DefaultSymbols = ["SPY", "QQQ"];
    private static readonly ConcurrentDictionary<string, CachedAdrEntry> AdrCache = new(StringComparer.OrdinalIgnoreCase);

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
            .Where(bar => IsCompletedBar(bar, now))
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
    /// Computes the ratio of OPRA front-expiry implied move (%) to completed 20-day ADR%, returning null when either input is missing or non-positive.
    /// </summary>
    public static decimal? ComputeImpliedMoveToAdrRatio(decimal? frontImpliedMovePct, decimal? adr20Pct) =>
        frontImpliedMovePct is > 0m && adr20Pct is > 0m
            ? Math.Round(frontImpliedMovePct.Value / adr20Pct.Value, 2)
            : null;

    /// <summary>
    /// Loads OPRA snapshots and completed 20-session ADR% for 1–6 symbols under a bounded 6-second timeout.
    /// Reuses a bounded 3-minute in-memory ADR cache (skipping failed schedules) and degrades individual symbols cleanly on timeout.
    /// </summary>
    public static async Task<CatalystOptionsAdrResponse> LoadAsync(
        string[] symbols,
        IOptionsSentimentSource optionsSource,
        AlpacaQuoteService quotes,
        ConfigCatalogService catalog,
        PaperRuntimeFactory runtimeFactory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));

        var cachedAdr = new Dictionary<string, Adr20CalculationResult>(StringComparer.OrdinalIgnoreCase);
        var uncachedSymbols = new List<string>(symbols.Length);
        foreach (var symbol in symbols)
        {
            if (AdrCache.TryGetValue(symbol, out var hit) && hit.ExpiresAtUtc > now)
            {
                cachedAdr[symbol] = hit.Result;
            }
            else
            {
                uncachedSymbols.Add(symbol);
            }
        }

        var spotHintsTask = DeskOptionsEndpoints.TryLoadSpotHintsAsync(quotes, symbols, deadline.Token, cancellationToken);
        var barsTask = uncachedSymbols.Count > 0
            ? TryLoadDailyBarsAndSchedulesAsync([.. uncachedSymbols], catalog, runtimeFactory, now, deadline.Token, cancellationToken)
            : Task.FromResult((true, (IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?)null, new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)));

        var spotHints = await spotHintsTask;
        var opraTasks = symbols.ToDictionary(
            symbol => symbol,
            symbol => TryLoadOptionsSnapshotAsync(optionsSource, symbol, spotHints.GetValueOrDefault(symbol), now, deadline.Token, cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(opraTasks.Values.Cast<Task>().Append(barsTask));
        var (scheduleSucceeded, schedules, barsBySymbol) = await barsTask;

        if (uncachedSymbols.Count > 0 && scheduleSucceeded)
        {
            SweepExpiredCacheEntries(now);
            foreach (var symbol in uncachedSymbols)
            {
                var symbolBars = barsBySymbol.GetValueOrDefault(symbol) ?? [];
                var computed = ComputeAdr20Pct(symbolBars, schedules, now, scheduleSucceeded: true);
                cachedAdr[symbol] = computed;
                if (AdrCache.Count < MaxCacheEntries || AdrCache.ContainsKey(symbol))
                {
                    AdrCache[symbol] = new CachedAdrEntry(computed, now.Add(AdrCacheTtl));
                }
            }
        }
        else if (uncachedSymbols.Count > 0)
        {
            foreach (var symbol in uncachedSymbols)
            {
                cachedAdr[symbol] = new Adr20CalculationResult(null, null, "Exchange schedule unavailable");
            }
        }

        var cards = new List<CatalystOptionsAdrCard>(symbols.Length);
        foreach (var symbol in symbols)
        {
            var snapshot = await opraTasks[symbol];
            var adr = cachedAdr[symbol];
            var ratio = ComputeImpliedMoveToAdrRatio(snapshot.FrontImpliedMovePct, adr.Adr20Pct);

            cards.Add(new CatalystOptionsAdrCard(
                Symbol: symbol,
                FrontImpliedMovePct: snapshot.FrontImpliedMovePct,
                TradingDaysToFrontExpiry: snapshot.TradingDaysToFrontExpiry,
                Adr20Pct: adr.Adr20Pct,
                AdrAsOfSessionDate: adr.AsOfSessionDate,
                ImpliedMoveToAdrRatio: ratio,
                Skew25Delta: snapshot.Skew25Delta,
                RegimeLabel: snapshot.RegimeLabel,
                SummaryNote: snapshot.SummaryNote,
                AdrUnavailableReason: adr.UnavailableReason));
        }

        return new CatalystOptionsAdrResponse(ContractVersions.VersionOne, now, cards);
    }

    // Evicts expired entries from the bounded in-memory ADR cache before inserting fresh symbol calculations.
    private static void SweepExpiredCacheEntries(DateTimeOffset now)
    {
        foreach (var pair in AdrCache)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                AdrCache.TryRemove(pair.Key, out _);
            }
        }
    }

    // Returns true only when a bar has finished its full timeframe interval and become known by the observation instant.
    private static bool IsCompletedBar(OhlcvBar bar, DateTimeOffset now) =>
        bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= now &&
        (bar.KnownAtUtc is null || bar.KnownAtUtc <= now);

    // Loads recent authoritative exchange schedules across the 20-session ADR lookback, failing closed on empty results or errors.
    internal static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadAdrSchedulesAsync(
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
                nyToday.AddDays(-65), nyToday.AddDays(1), deadlineToken);
            return schedules.Count > 0 ? (true, schedules) : (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    // Fetches 65 calendar days of 1d bars and authoritative exchange schedules for uncached symbols under the shared deadline.
    private static async Task<(bool ScheduleSucceeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules, Dictionary<string, List<OhlcvBar>> BarsBySymbol)>
        TryLoadDailyBarsAndSchedulesAsync(
            string[] symbols,
            ConfigCatalogService catalog,
            PaperRuntimeFactory runtimeFactory,
            DateTimeOffset now,
            CancellationToken deadlineToken,
            CancellationToken callerToken)
    {
        var grouped = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        var defaultProfile = catalog.DefaultPaperConfig()?.Config;
        if (defaultProfile is null)
        {
            return (false, null, grouped);
        }

        try
        {
            var provider = runtimeFactory.CreateProvider(defaultProfile);
            var (scheduleSucceeded, schedules) = await TryLoadAdrSchedulesAsync(provider, now, deadlineToken, callerToken);
            if (!scheduleSucceeded)
            {
                return (false, null, grouped);
            }

            var start = now.AddDays(-65);
            await foreach (var bar in provider.GetBarsAsync(symbols, ["1d"], start, now, deadlineToken))
            {
                if (!grouped.TryGetValue(bar.Ticker, out var list))
                {
                    list = [];
                    grouped[bar.Ticker] = list;
                }

                list.Add(bar);
            }

            return (true, schedules, grouped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            return (false, null, grouped);
        }
    }

    // Reads one symbol's OPRA snapshot under the shared deadline, degrading to an explicit unavailable snapshot on timeout or error.
    private static async Task<OptionsSentimentSnapshot> TryLoadOptionsSnapshotAsync(
        IOptionsSentimentSource optionsSource,
        string symbol,
        decimal? spotHint,
        DateTimeOffset now,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        try
        {
            return await optionsSource.GetSnapshotAsync(symbol, spotHint, cancellationToken: deadlineToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            return OptionsSentimentSnapshot.Unavailable(symbol, now, "OPRA options context timed out or unavailable.");
        }
    }
}
