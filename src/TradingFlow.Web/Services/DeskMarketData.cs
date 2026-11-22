using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Web.Services;

/// <summary>
/// Shared read-side rules for the workspace status strip, the options/ADR cards and the sector ranking, so all three
/// decide "is this bar finished", "which exchange sessions exist", and how completed daily bars are cached in
/// <see cref="ICandleStore"/> the same way.
/// </summary>
public static class DeskMarketData
{
    private const string DeskStoreScope = "desk";
    private const string DeskStoreRunPrefix = "daily-bars";
    private const string StoredFeed = "sip";
    private const string StoredAdjustment = "all";
    private const int StoreReadParallelism = 4;
    private static readonly TimeSpan StoreWriteLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StoreReadBaseLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StoreReadLimitPerSymbol = TimeSpan.FromSeconds(0.5);

    // Background writes still running, so tests can wait for them.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> StoreWrites = new();
    private const string ProviderBarSource = "provider";
    private static readonly TimeSpan ScheduleFlightDeadline = TimeSpan.FromSeconds(15);

    // One SingleFlight per IMemoryCache instance so concurrent cold calendar misses share a single upstream request
    // while isolated test caches never interfere with each other.
    private static readonly ConditionalWeakTable<
        IMemoryCache,
        SingleFlight<string, (bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)>> ScheduleFlights = new();

    /// <summary>
    /// True only when a bar has both finished its timeframe window and become known by <paramref name="now"/>; a daily bar
    /// dated D is finished at D + 1 day (UTC), never during its own session.
    /// </summary>
    public static bool IsCompletedBar(OhlcvBar bar, DateTimeOffset now) =>
        bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= now &&
        (bar.KnownAtUtc is null || bar.KnownAtUtc <= now);

    /// <summary>
    /// Same as <see cref="TryLoadSchedulesAsync"/> but reuses a successful calendar for the rest of the New York day, so the
    /// one-minute status-strip refresh does not call (and archive) the provider's calendar endpoint every minute.
    /// Failures are not cached.
    /// </summary>
    public static Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesCachedAsync(
        IMarketDataProvider provider,
        DateTimeOffset now,
        int daysBack,
        IMemoryCache cache,
        CancellationToken deadlineToken,
        ILogger? logger = null,
        int daysForward = 1) =>
        TryLoadSchedulesCachedAsync(
            () => provider,
            now,
            daysBack,
            cache,
            deadlineToken,
            logger: logger,
            daysForward: daysForward,
            disposeCreatedProvider: false);

    /// <summary>
    /// Checks <paramref name="cache"/> first before invoking <paramref name="providerFactory"/> so warm reads allocate zero
    /// providers/HTTP clients. Cold misses for the same key coalesce via <see cref="SingleFlight{TKey, TValue}"/> so
    /// concurrent callers issue one upstream calendar request, and any provider created by the factory is disposed when
    /// <paramref name="disposeCreatedProvider"/> is true. Failures are never cached.
    /// </summary>
    public static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesCachedAsync(
        Func<IMarketDataProvider?> providerFactory,
        DateTimeOffset now,
        int daysBack,
        IMemoryCache cache,
        CancellationToken deadlineToken,
        ILogger? logger = null,
        int daysForward = 1,
        bool disposeCreatedProvider = true)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(cache);

        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        var key = $"exchange-calendar:{nyToday:yyyy-MM-dd}:{daysBack}:{daysForward}";
        if (cache.TryGetValue(key, out IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? hit) && hit is not null)
        {
            return (true, hit);
        }

        var flight = ScheduleFlights.GetValue(cache, static _ => new(StringComparer.Ordinal));
        var task = flight.RunAsync(key, async () =>
        {
            if (cache.TryGetValue(key, out IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? recheck) && recheck is not null)
            {
                return (true, recheck);
            }

            IMarketDataProvider? provider = null;
            try
            {
                provider = providerFactory();
                if (provider is null)
                {
                    return (false, (IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?)null);
                }

                using var flightCts = new CancellationTokenSource(ScheduleFlightDeadline);
                var loaded = await TryLoadSchedulesAsync(provider, now, daysBack, flightCts.Token, logger: logger, daysForward: daysForward);
                if (loaded.Succeeded && loaded.Schedules is { } schedules)
                {
                    cache.Set(key, schedules, TimeSpan.FromHours(6));
                }

                return loaded;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Exchange calendar provider could not be created ({ErrorType}); dependent values are shown as unavailable.",
                    ex.GetType().Name);
                return (false, (IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?)null);
            }
            finally
            {
                if (disposeCreatedProvider)
                {
                    (provider as IDisposable)?.Dispose();
                }
            }
        });

        try
        {
            return await task.WaitAsync(deadlineToken);
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            logger?.LogWarning("Exchange calendar read for {Key} timed out waiting for shared load.", key);
            return (false, null);
        }
    }

    /// <summary>
    /// Loads the provider's exchange calendar from <paramref name="daysBack"/> New York days before <paramref name="now"/>
    /// to <paramref name="daysForward"/> New York days after. Providers without a calendar succeed with no schedules (callers
    /// then use bar dates). An empty result, an error or the deadline fails closed with <c>Succeeded = false</c> and a logged
    /// reason; only the caller's own cancellation is rethrown.
    /// </summary>
    public static async Task<(bool Succeeded, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? Schedules)> TryLoadSchedulesAsync(
        IMarketDataProvider provider,
        DateTimeOffset now,
        int daysBack,
        CancellationToken deadlineToken,
        CancellationToken callerToken = default,
        ILogger? logger = null,
        int daysForward = 1)
    {
        if (provider is not IMarketSessionScheduleProvider scheduleProvider)
        {
            return (true, null);
        }

        var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime);
        var from = nyToday.AddDays(-daysBack);
        var to = nyToday.AddDays(daysForward);
        try
        {
            var schedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(from, to, deadlineToken);
            if (schedules.Count > 0) return (true, schedules);
            logger?.LogWarning("Exchange calendar for {From}..{To} came back empty; dependent values are shown as unavailable.",
                from, to);
            return (false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Exchange calendar for {From}..{To} could not be read ({ErrorType}); dependent values are shown as unavailable.",
                from, to, ex.GetType().Name);
            return (false, null);
        }
    }

    /// <summary>
    /// Returns daily bars for <paramref name="symbols"/> over [<paramref name="start"/>, <paramref name="now"/>], from the
    /// candle store where it holds every expected completed session and from the provider otherwise. A fetch also returns the
    /// unfinished bar of the current day, so callers filter with <see cref="IsCompletedBar"/>. The provider is created only when
    /// something must be fetched. Store problems never fail the call; a slow store counts as a miss.
    /// </summary>
    public static async Task<Dictionary<string, List<OhlcvBar>>> LoadDailyBarsWithStoreAsync(
        Func<IMarketDataProvider> providerFactory,
        IReadOnlyList<string> symbols,
        DateTimeOffset start,
        DateTimeOffset now,
        int requiredSessions,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        ICandleStore? candleStore,
        string providerName,
        CancellationToken cancellationToken,
        bool allowHourlyFallback = false,
        IVixCloseSource? vixSource = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(symbols);
        var wanted = symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var providerSegment = string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName.Trim().ToLowerInvariant();
        var context = new CandleStoreContext(DeskStoreScope, StoreRunName(start, now), providerSegment);
        var sessions = Math.Max(1, requiredSessions);

        var stored = await ReadStoredAsync(candleStore, context, wanted, start, now, logger, cancellationToken);
        var result = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in wanted)
        {
            if (stored.TryGetValue(symbol, out var bars) && TryValidateDailyBars(bars, schedules, now, sessions, out var valid)) result[symbol] = valid;
        }

        var missing = wanted.Where(symbol => !result.ContainsKey(symbol)).ToArray();
        if (missing.Length == 0) return result;

        var fetched = await FetchAsync(providerFactory(), missing, start, now, allowHourlyFallback, vixSource, cancellationToken);
        foreach (var symbol in missing) result[symbol] = fetched[symbol].Bars;
        if (candleStore is not null)
        {
            StartStoring(candleStore, context, SelectStorable(fetched.Values.Select(item => item.NativeDaily), stored, schedules, now, sessions), logger);
        }

        return result;
    }

    // Reads every symbol's stored bars, a few at a time, under a limit of its own: when that limit passes, the symbols not read
    // yet are simply fetched, so a slow disk can never make available data unavailable. Read failures are logged and count as a miss.
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>>> ReadStoredAsync(
        ICandleStore? candleStore,
        CandleStoreContext context,
        IReadOnlyList<string> symbols,
        DateTimeOffset start,
        DateTimeOffset now,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var stored = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        if (candleStore is null || symbols.Count == 0) return stored;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(StoreReadBaseLimit + (StoreReadLimitPerSymbol * symbols.Count));
        try
        {
            await Parallel.ForEachAsync(symbols, new ParallelOptions { MaxDegreeOfParallelism = StoreReadParallelism, CancellationToken = limit.Token },
                async (symbol, token) => stored[symbol] = await TryReadStoreBarsAsync(candleStore, context, symbol, start, now, logger, token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Stored daily bars were not all read in time ({Read} of {Wanted} symbols); the rest are fetched.", stored.Count, symbols.Count);
        }

        return stored;
    }

    // Fetches each symbol's bars, keeping the provider's own daily bars apart from daily bars rebuilt from hourly ones.
    private static async Task<IReadOnlyDictionary<string, (List<OhlcvBar> Bars, IReadOnlyList<OhlcvBar> NativeDaily)>> FetchAsync(
        IMarketDataProvider provider,
        IReadOnlyList<string> symbols,
        DateTimeOffset start,
        DateTimeOffset now,
        bool allowHourlyFallback,
        IVixCloseSource? vixSource,
        CancellationToken cancellationToken)
    {
        var fetched = symbols.ToDictionary(symbol => symbol, _ => (Bars: new List<OhlcvBar>(), NativeDaily: (IReadOnlyList<OhlcvBar>)[]), StringComparer.OrdinalIgnoreCase);
        if (allowHourlyFallback)
        {
            var gate = new RegimeGateService(vixSource);
            foreach (var symbol in symbols)
            {
                var tracking = new NativeDailyTrackingProvider(provider);
                var bars = await gate.LoadBenchmarkDailyBarsAsync(tracking, symbol, ["1d", "1h"], start, now, cancellationToken);
                fetched[symbol] = ([.. bars], tracking.NativeDailyBars);
            }

            return fetched;
        }

        await foreach (var bar in provider.GetBarsAsync(symbols, ["1d"], start, now, cancellationToken))
        {
            if (fetched.TryGetValue(bar.Ticker, out var entry)) entry.Bars.Add(bar);
        }

        foreach (var symbol in symbols) fetched[symbol] = (fetched[symbol].Bars, fetched[symbol].Bars);
        return fetched;
    }

    // Chooses what to store: only symbols whose fetched bars are themselves complete (a short provider answer is never stored),
    // and of those only bars that are new or differ from the stored ones.
    private static IReadOnlyList<OhlcvBar> SelectStorable(
        IEnumerable<IReadOnlyList<OhlcvBar>> fetchedBySymbol,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> stored,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        int sessions)
    {
        var storable = new List<OhlcvBar>();
        foreach (var fetched in fetchedBySymbol)
        {
            if (TryValidateDailyBars(fetched, schedules, now, sessions, out var complete)) storable.AddRange(ChangedBars(complete, stored));
        }

        return storable;
    }

    // Stores bars in the background: the first history is thousands of small files and must never hold back or fail the page.
    // Each symbol's newest bar is written last, so a set only counts as complete once everything older is in place.
    private static void StartStoring(ICandleStore candleStore, CandleStoreContext context, IReadOnlyList<OhlcvBar> bars, ILogger? logger)
    {
        if (bars.Count == 0) return;
        var newest = bars.GroupBy(bar => bar.Ticker, StringComparer.OrdinalIgnoreCase).Select(group => group.MaxBy(bar => bar.Timestamp)!).ToArray();
        var older = bars.Except(newest).ToArray();
        var write = Task.Run(() => TryStoreAsync(candleStore, context, older, newest, logger), CancellationToken.None);
        StoreWrites[write] = 0;
        _ = write.ContinueWith(finished => StoreWrites.TryRemove(finished, out byte _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits until every background write started so far has finished (tests).</summary>
    internal static Task WhenStoreWritesFinishAsync() => Task.WhenAll(StoreWrites.Keys);

    // Names the stored set by its history length in days, so sets of different lengths are never mixed.
    private static string StoreRunName(DateTimeOffset start, DateTimeOffset now) =>
        $"{DeskStoreRunPrefix}-{(int)Math.Round((now - start).TotalDays)}d";

    // Keeps only bars that are new or whose prices or volume differ from the stored bar, so an ordinary session adds one file
    // per symbol instead of rewriting the whole history.
    private static IReadOnlyList<OhlcvBar> ChangedBars(
        IReadOnlyList<OhlcvBar> fetched,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> storedBySymbol)
    {
        var stored = storedBySymbol.ToDictionary(pair => pair.Key, pair => pair.Value
            .GroupBy(bar => bar.Timestamp.ToUniversalTime())
            .ToDictionary(group => group.Key, group => group.Last()), StringComparer.OrdinalIgnoreCase);
        return fetched.Where(bar => !stored.TryGetValue(bar.Ticker, out var byTime) ||
            !byTime.TryGetValue(bar.Timestamp.ToUniversalTime(), out var known) ||
            (known.Open, known.High, known.Low, known.Close, known.Volume, known.DataFeed, known.AdjustmentPolicy) !=
            (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.DataFeed, bar.AdjustmentPolicy)).ToArray();
    }

    // Reads stored provider 1d bars for one symbol up to AsOfUtc = now, logging and degrading to empty on I/O errors.
    private static async Task<IReadOnlyList<OhlcvBar>> TryReadStoreBarsAsync(
        ICandleStore candleStore,
        CandleStoreContext context,
        string symbol,
        DateTimeOffset start,
        DateTimeOffset now,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await candleStore.ReadBarsAsync(
                new CandleStoreReadRequest(
                    context.Scope,
                    context.RunName,
                    context.ProviderName,
                    symbol,
                    "1d",
                    start,
                    now,
                    Source: ProviderBarSource,
                    AsOfUtc: now),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "Candle store read failed for {Symbol} ({ErrorType}); falling back to provider.",
                symbol, ex.GetType().Name);
            return [];
        }
    }

    // Writes the older bars first and the newest bars only after they succeeded, under its own limit; failures are logged.
    private static async Task TryStoreAsync(
        ICandleStore candleStore,
        CandleStoreContext context,
        IReadOnlyCollection<OhlcvBar> older,
        IReadOnlyCollection<OhlcvBar> newest,
        ILogger? logger)
    {
        try
        {
            using var limit = new CancellationTokenSource(StoreWriteLimit);
            if (older.Count > 0) await candleStore.UpsertBarsAsync(new CandleStoreWriteRequest(context, ProviderBarSource, older), limit.Token);
            await candleStore.UpsertBarsAsync(new CandleStoreWriteRequest(context, ProviderBarSource, newest), limit.Token);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Storing {Count} daily bars failed ({ErrorType}); they are fetched again when next needed.",
                older.Count + newest.Count, ex.GetType().Name);
        }
    }

    // Checks that daily bars are valid SIP, fully adjusted bars covering every exact expected completed session at `now`, and
    // returns those completed trading-day bars. Used for stored bars before serving them and for fetched bars before storing them.
    // Fails closed (returns false -> fetch from provider) whenever an authoritative schedule cannot verify all `requiredSessions`
    // or when any intermediate session is missing or corrupt (Close <= 0 or High < Low).
    private static bool TryValidateDailyBars(
        IReadOnlyList<OhlcvBar> stored,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
        DateTimeOffset now,
        int requiredSessions,
        out List<OhlcvBar> validOrdered)
    {
        validOrdered = [];
        if (stored.Count == 0)
        {
            return false;
        }

        var filtered = VixRegime.FilterTradingDayBars(stored, schedules)
            .Where(bar =>
                bar.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) &&
                IsCompletedBar(bar, now) &&
                bar.Close > 0m &&
                bar.High >= bar.Low)
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
        if (filtered.Length == 0 || filtered.Any(bar => bar.DataFeed != StoredFeed || bar.AdjustmentPolicy != StoredAdjustment))
        {
            return false;
        }

        var bySessionDate = new Dictionary<DateOnly, OhlcvBar>();
        foreach (var bar in filtered)
        {
            bySessionDate[DateOnly.FromDateTime(bar.Timestamp.UtcDateTime)] = bar;
        }

        if (bySessionDate.Count < requiredSessions)
        {
            return false;
        }

        if (schedules is { Count: > 0 })
        {
            IReadOnlyList<DateOnly> expected;
            try
            {
                expected = TechnicalFactorCalculator.ExpectedCompletedSessions(schedules, now, requiredSessions);
            }
            catch (InvalidDataException)
            {
                // Fail closed when the loaded exchange calendar does not cover the full required session span so
                // intermediate gaps or corrupt older bars in ICandleStore are never silently trusted.
                return false;
            }

            if (!expected.All(bySessionDate.ContainsKey))
            {
                return false;
            }
        }
        else
        {
            var latestSession = VixRegime.PreviousSession(filtered, now);
            if (latestSession is not { } observed || observed < LatestCompletedWeekday(now))
            {
                return false;
            }
        }

        validOrdered = bySessionDate.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        return true;
    }

    // Computes the latest weekday strictly before New York today for calendarless providers lacking an exchange schedule.
    private static DateOnly LatestCompletedWeekday(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, VixRegime.NewYorkTimeZone).DateTime).AddDays(-1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    // Wraps a provider call so native 1d bars can be captured separately from any 1h bars resampled by RegimeGateService.
    private sealed class NativeDailyTrackingProvider(IMarketDataProvider inner) : IMarketDataProvider
    {
        public List<OhlcvBar> NativeDailyBars { get; } = [];

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var requestedDaily = timeframes.Any(tf => tf.Equals("1d", StringComparison.OrdinalIgnoreCase));
            await foreach (var bar in inner.GetBarsAsync(tickers, timeframes, start, end, cancellationToken))
            {
                if (requestedDaily && bar.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
                {
                    NativeDailyBars.Add(bar);
                }

                yield return bar;
            }
        }
    }
}
