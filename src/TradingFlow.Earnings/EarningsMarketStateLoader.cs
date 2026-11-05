using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Earnings;

/// <summary>
/// Builds earnings-analysis market state once, persists provider bars, and then advances
/// each symbol with small overlapping REST windows. The overlap makes provider revisions
/// idempotent while avoiding a full historical download on every monitor iteration.
/// </summary>
public sealed class EarningsMarketStateLoader
{
    private static readonly CandleStoreContext StoreContext = new("paper", "earnings-monitor", "alpaca");
    private readonly Lazy<IMarketDataProvider> marketData;
    private readonly ICandleStore candleStore;
    private readonly EarningsMonitorOptions options;
    private readonly ILogger<EarningsMarketStateLoader> logger;
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private readonly SemaphoreSlim calendarLock = new(1, 1);
    private readonly Dictionary<string, SortedDictionary<DateTimeOffset, OhlcvBar>> state =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule>? cachedSchedules;
    private DateOnly cachedScheduleStart;
    private DateOnly cachedScheduleEnd;

    public EarningsMarketStateLoader(
        Lazy<IMarketDataProvider> marketData,
        ICandleStore candleStore,
        EarningsMonitorOptions options,
        ILogger<EarningsMarketStateLoader>? logger = null)
    {
        this.marketData = marketData;
        this.candleStore = candleStore;
        this.options = options;
        this.logger = logger ?? NullLogger<EarningsMarketStateLoader>.Instance;
    }

    /// <summary>
    /// The latest trade of each stock, for display. Returns an empty result when the market-data provider cannot
    /// report trades (CSV and test providers); provider errors propagate to the caller.
    /// </summary>
    public Task<IReadOnlyDictionary<string, LatestTrade>> GetLatestTradesAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken) =>
        marketData.Value is ILatestTradeProvider provider
            ? provider.GetLatestTradesAsync(tickers, cancellationToken)
            : Task.FromResult<IReadOnlyDictionary<string, LatestTrade>>(new Dictionary<string, LatestTrade>());

    /// <summary>
    /// Reads one stock's stored 5-minute bars between two times straight from the candle store, without touching
    /// the in-memory state or the provider. Replay uses it because its window can reach further back than the
    /// live lookback.
    /// </summary>
    public Task<IReadOnlyList<OhlcvBar>> ReadStoredBarsAsync(
        string ticker,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken) =>
        candleStore.ReadBarsAsync(
            new CandleStoreReadRequest(StoreContext.Scope, StoreContext.RunName, StoreContext.ProviderName, ticker, "5m", start, end),
            cancellationToken);

    public async Task<IReadOnlyDictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule>>
        LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken)
    {
        if (marketData.Value is not IMarketSessionScheduleProvider scheduleProvider)
        {
            throw new InvalidOperationException(
                "The earnings market-data provider does not expose an authoritative exchange calendar.");
        }

        await calendarLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedSchedules is not null &&
                cachedScheduleStart <= startDateInclusive &&
                cachedScheduleEnd >= endDateInclusive)
            {
                return cachedSchedules;
            }

            cachedSchedules = await scheduleProvider.LoadMarketSessionSchedulesAsync(
                startDateInclusive,
                endDateInclusive,
                cancellationToken);
            cachedScheduleStart = startDateInclusive;
            cachedScheduleEnd = endDateInclusive;
            return cachedSchedules;
        }
        finally
        {
            calendarLock.Release();
        }
    }

    /// <summary>
    /// Returns the last <see cref="EarningsMonitorOptions.SubDailyLookbackDays"/> days of 5-minute bars for
    /// each ticker. Tickers not yet in memory are first read from the local candle store; tickers with no
    /// stored bars download the whole window, and the rest download only from just before their newest bar.
    /// Logs how long each stage took so a slow pass shows where its time went.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>>> LoadAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        }

        await loadLock.WaitAsync(cancellationToken);
        try
        {
            var end = nowUtc.ToUniversalTime();
            var windowStart = end.AddDays(-options.SubDailyLookbackDays);
            var stage = Stopwatch.StartNew();
            var hydratedCount = await HydrateMissingSymbolsAsync(normalized, windowStart, end, cancellationToken);
            var hydrateTime = stage.Elapsed;

            var cold = normalized.Where(ticker => state[ticker].Count == 0).ToArray();
            var warm = normalized.Where(ticker => state[ticker].Count > 0).ToArray();
            stage.Restart();
            var coldBars = cold.Length == 0
                ? []
                : await DownloadAndMergeAsync(cold, windowStart, end, cancellationToken);
            var coldTime = stage.Elapsed;
            stage.Restart();
            await PersistAsync(coldBars, cancellationToken);
            var persistTime = stage.Elapsed;

            // Each window is saved before the next one downloads. Bars held only in memory would be skipped
            // by the next pass's window and leave a permanent gap in the files if a later window failed.
            var warmWindows = GroupByIncrementalStart(warm, end);
            var warmBars = new List<OhlcvBar>();
            var warmTime = TimeSpan.Zero;
            foreach (var window in warmWindows)
            {
                stage.Restart();
                var windowBars = await DownloadAndMergeAsync(window.Tickers, window.Start, end, cancellationToken);
                warmTime += stage.Elapsed;
                stage.Restart();
                await PersistAsync(windowBars, cancellationToken);
                persistTime += stage.Elapsed;
                warmBars.AddRange(windowBars);
            }

            DropBarsBefore(normalized, windowStart);
            logger.LogInformation(
                "Earnings bar load for {TickerCount} stocks: read {HydratedCount} stocks from local candle files in {HydrateMs} ms; " +
                "downloaded {ColdBarCount} bars for {ColdCount} stocks without stored bars in {ColdMs} ms; " +
                "downloaded {WarmBarCount} bars for {WarmCount} stocks in {WarmWindowCount} windows, earliest from {EarliestWarmStartUtc}, in {WarmMs} ms; " +
                "wrote {SavedBarCount} bars to local candle files in {PersistMs} ms.",
                normalized.Length,
                hydratedCount,
                (long)hydrateTime.TotalMilliseconds,
                coldBars.Count,
                cold.Length,
                (long)coldTime.TotalMilliseconds,
                warmBars.Count,
                warm.Length,
                warmWindows.Count,
                warmWindows.Count == 0 ? null : warmWindows[0].Start,
                (long)warmTime.TotalMilliseconds,
                coldBars.Count + warmBars.Count,
                (long)persistTime.TotalMilliseconds);

            return normalized.ToDictionary(
                ticker => ticker,
                ticker => (IReadOnlyList<OhlcvBar>)state[ticker].Values.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>
    /// Drops the in-memory bars of every stock not in <paramref name="tracked"/>, for example once its earnings
    /// window has ended, and returns how many stocks were dropped. Files on disk are kept: Replay and the next
    /// quarter's report read them back. Only the monitor calls this, after loading its tracked list, so a
    /// one-stock Replay never unloads the stocks the monitor is following.
    /// </summary>
    public async Task<int> ReleaseUntrackedAsync(IReadOnlyCollection<string> tracked, CancellationToken cancellationToken)
    {
        var keep = tracked.Select(ticker => ticker.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            var released = state.Keys.Where(ticker => !keep.Contains(ticker)).ToArray();
            foreach (var ticker in released)
            {
                state.Remove(ticker);
            }

            return released.Length;
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>
    /// Reads stored bars for the tickers not yet held in memory and returns how many it read. Several
    /// tickers are read at once: on 4 Nov 2026 a restart spent 18 minutes reading about 65,000 day files
    /// for 900 stocks one ticker at a time, nearly all of it waiting on each file to open. Only this
    /// method's own result array is written in parallel; the shared state is filled in afterwards.
    /// </summary>
    private async Task<int> HydrateMissingSymbolsAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var missing = tickers.Where(ticker => !state.ContainsKey(ticker)).ToArray();
        var persisted = new IReadOnlyList<OhlcvBar>[missing.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, missing.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumParallelBarFileReads,
                CancellationToken = cancellationToken
            },
            async (index, token) => persisted[index] = await ReadStoredBarsAsync(missing[index], start, end, token));

        for (var index = 0; index < missing.Length; index++)
        {
            state[missing[index]] = new SortedDictionary<DateTimeOffset, OhlcvBar>(
                persisted[index].ToDictionary(bar => bar.Timestamp, bar => bar));
        }

        return missing.Length;
    }

    /// <summary>
    /// Splits the tickers that already have bars into download windows: tickers whose newest bar falls in the
    /// same UTC hour (newest bar under a day old) or UTC day (older) share one window, which starts 5 minutes
    /// before the oldest newest-bar in that group. One window for every ticker used to start at the oldest
    /// newest-bar of any of them: on 4 Nov 2026 a stock that last traded at 19:10 UTC made every pass
    /// re-download from 19:05 for all 900 stocks, and that start only moves once the quiet stock trades
    /// again. The overlap still re-reads each member's newest bar so provider corrections are picked up;
    /// hour and day groups keep the request count small.
    /// </summary>
    private List<(DateTimeOffset Start, string[] Tickers)> GroupByIncrementalStart(
        IReadOnlyCollection<string> warm,
        DateTimeOffset end) =>
        warm
            .Select(ticker => (Ticker: ticker, LastBarUtc: state[ticker].Keys.Last().ToUniversalTime()))
            .GroupBy(item => FloorToHourOrDay(item.LastBarUtc, end))
            .OrderBy(group => group.Key)
            .Select(group => (
                group.Min(item => item.LastBarUtc).AddMinutes(-5),
                group.Select(item => item.Ticker).ToArray()))
            .ToList();

    /// <summary>Rounds a UTC bar time down to its hour when it is under a day older than the end, else to its UTC day.</summary>
    private static DateTimeOffset FloorToHourOrDay(DateTimeOffset lastBarUtc, DateTimeOffset end)
    {
        var bucket = end - lastBarUtc < TimeSpan.FromDays(1) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        return new DateTimeOffset(lastBarUtc.UtcTicks - lastBarUtc.UtcTicks % bucket.Ticks, TimeSpan.Zero);
    }

    /// <summary>Downloads 5-minute bars for the tickers and writes them into the in-memory state; returns the bars received.</summary>
    private async Task<List<OhlcvBar>> DownloadAndMergeAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var downloaded = new List<OhlcvBar>();
        await foreach (var bar in marketData.Value.GetBarsAsync(tickers, ["5m"], start, end, cancellationToken))
        {
            if (!state.TryGetValue(bar.Ticker, out var tickerState))
            {
                tickerState = new SortedDictionary<DateTimeOffset, OhlcvBar>();
                state[bar.Ticker] = tickerState;
            }

            tickerState[bar.Timestamp] = bar;
            downloaded.Add(bar);
        }

        return downloaded;
    }

    /// <summary>Writes downloaded bars to the local candle store so a restart reads them back instead of downloading again.</summary>
    private async Task PersistAsync(IReadOnlyList<OhlcvBar> bars, CancellationToken cancellationToken)
    {
        if (bars.Count > 0)
        {
            await candleStore.UpsertBarsAsync(
                new CandleStoreWriteRequest(StoreContext, "provider", bars),
                cancellationToken);
        }
    }

    /// <summary>
    /// Removes in-memory bars older than the lookback window so memory stays bounded. Keys are kept in time
    /// order, so the scan stops at the first bar inside the window instead of reading every bar each pass.
    /// </summary>
    private void DropBarsBefore(IEnumerable<string> tickers, DateTimeOffset windowStart)
    {
        foreach (var ticker in tickers)
        {
            var expired = state[ticker].Keys.TakeWhile(timestamp => timestamp < windowStart).ToArray();
            foreach (var timestamp in expired)
            {
                state[ticker].Remove(timestamp);
            }
        }
    }
}
