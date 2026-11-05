using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Earnings;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class EarningsMarketStateLoaderTests
{
    [Fact]
    public async Task LoadAsync_BackfillsOnceThenUsesOverlappingIncrementalWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-31T15:00:00Z");
        var sourceBars = Enumerable.Range(0, 100)
            .Select(index => Bar("TEST", now.AddMinutes(-500 + index * 5)))
            .ToArray();
        var provider = new RecordingMarketDataProvider(sourceBars);
        var store = new RecordingCandleStore();
        var options = EarningsMonitorOptions.Default;
        var loader = new EarningsMarketStateLoader(new Lazy<IMarketDataProvider>(() => provider), store, options);

        var first = await loader.LoadAsync(["TEST"], now, CancellationToken.None);
        var second = await loader.LoadAsync(["TEST"], now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(100, first["TEST"].Count);
        Assert.Equal(100, second["TEST"].Count);
        Assert.Equal(now.AddDays(-options.SubDailyLookbackDays), provider.Requests[0].Start);
        // The next download starts 5 minutes before the newest stored bar, so a provider correction to it is picked up.
        Assert.Equal(sourceBars[^1].Timestamp.AddMinutes(-5), provider.Requests[1].Start);
        Assert.Equal(2, store.WriteCount);
        Assert.Equal(100, store.Bars.Count);
    }

    /// <summary>
    /// On 4 Nov 2026 one stock that last traded at 19:10 UTC made every pass re-download from 19:05 for all
    /// 900 tracked stocks. A quiet stock must only widen its own download window.
    /// </summary>
    [Fact]
    public async Task LoadAsync_QuietStockDoesNotWidenTheDownloadWindowOfActiveStocks()
    {
        var now = DateTimeOffset.Parse("2026-11-04T22:31:00Z");
        var store = new RecordingCandleStore();
        store.Seed(Bar("BUSY", DateTimeOffset.Parse("2026-11-04T22:25:00Z")));
        store.Seed(Bar("BUSY2", DateTimeOffset.Parse("2026-11-04T22:20:00Z")));
        store.Seed(Bar("QUIET", DateTimeOffset.Parse("2026-11-04T19:10:00Z")));
        store.Seed(Bar("STALE", DateTimeOffset.Parse("2026-11-01T15:00:00Z")));
        var provider = new RecordingMarketDataProvider([]);
        var loader = new EarningsMarketStateLoader(
            new Lazy<IMarketDataProvider>(() => provider), store, EarningsMonitorOptions.Default);

        await loader.LoadAsync(["BUSY", "QUIET", "STALE", "BUSY2"], now, CancellationToken.None);

        Assert.Equal(
            new (string, string)[]
            {
                ("2026-11-01T14:55:00Z", "STALE"),
                ("2026-11-04T19:05:00Z", "QUIET"),
                ("2026-11-04T22:15:00Z", "BUSY,BUSY2")
            },
            provider.Requests.Select(request => (
                request.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                String.Join(",", request.Tickers.Order(StringComparer.Ordinal)))));
        Assert.All(provider.Requests, request => Assert.Equal(now, request.End));
    }

    /// <summary>
    /// A restart reads every tracked stock's stored bars before anything else happens. One at a time this
    /// took 18 minutes for 900 stocks on 4 Nov 2026, so reads must overlap, up to the configured limit.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ReadsStoredBarsForSeveralStocksAtOnceWithinTheLimit()
    {
        var now = DateTimeOffset.Parse("2026-11-04T22:31:00Z");
        var tickers = Enumerable.Range(0, 12).Select(index => $"T{index:00}").ToArray();
        var store = new RecordingCandleStore { ReadDelay = TimeSpan.FromMilliseconds(200) };
        foreach (var ticker in tickers)
        {
            store.Seed(Bar(ticker, now.AddMinutes(-10)));
        }

        var options = EarningsMonitorOptions.Default with { MaximumParallelBarFileReads = 4 };
        var loader = new EarningsMarketStateLoader(
            new Lazy<IMarketDataProvider>(() => new RecordingMarketDataProvider([])), store, options);

        var loaded = await loader.LoadAsync(tickers, now, CancellationToken.None);

        Assert.InRange(store.MaximumConcurrentReads, 2, 4);
        Assert.All(tickers, ticker => Assert.Equal(now.AddMinutes(-10), Assert.Single(loaded[ticker]).Timestamp));
    }

    /// <summary>
    /// Bars from a finished download window must be saved even when a later window fails. The next pass only
    /// downloads from after the bars it holds in memory, so unsaved bars would stay missing from the files.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SavesEachDownloadWindowBeforeTheNextOneStarts()
    {
        var now = DateTimeOffset.Parse("2026-11-04T22:31:00Z");
        var store = new RecordingCandleStore();
        store.Seed(Bar("QUIET", DateTimeOffset.Parse("2026-11-04T19:10:00Z")));
        store.Seed(Bar("FAIL", DateTimeOffset.Parse("2026-11-04T22:20:00Z")));
        var provider = new RecordingMarketDataProvider([Bar("QUIET", DateTimeOffset.Parse("2026-11-04T19:15:00Z"))])
        {
            FailingTicker = "FAIL"
        };
        var loader = new EarningsMarketStateLoader(
            new Lazy<IMarketDataProvider>(() => provider), store, EarningsMonitorOptions.Default);

        await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync(["QUIET", "FAIL"], now, CancellationToken.None));

        Assert.Equal(1, store.WriteCount);
        Assert.True(store.Bars.ContainsKey(("QUIET", DateTimeOffset.Parse("2026-11-04T19:15:00Z"))));
    }

    /// <summary>
    /// Stocks whose earnings window has ended leave memory but keep their files: tracked again, a stock is
    /// read back from the local store, and the stocks still tracked are not read again.
    /// </summary>
    [Fact]
    public async Task ReleaseUntrackedAsync_DropsOnlyUntrackedStocksAndReadsThemBackFromStore()
    {
        var now = DateTimeOffset.Parse("2026-11-04T22:31:00Z");
        var store = new RecordingCandleStore();
        store.Seed(Bar("DONE", now.AddMinutes(-10)));
        store.Seed(Bar("LIVE", now.AddMinutes(-10)));
        var loader = new EarningsMarketStateLoader(
            new Lazy<IMarketDataProvider>(() => new RecordingMarketDataProvider([])), store, EarningsMonitorOptions.Default);
        await loader.LoadAsync(["DONE", "LIVE"], now, CancellationToken.None);

        var released = await loader.ReleaseUntrackedAsync(["live"], CancellationToken.None);
        var readsBefore = store.ReadCount;
        var reloaded = await loader.LoadAsync(["DONE", "LIVE"], now, CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(1, store.ReadCount - readsBefore);
        Assert.Single(reloaded["DONE"]);
        Assert.Single(reloaded["LIVE"]);
        Assert.Equal(2, store.Bars.Count);
    }

    /// <summary>
    /// The short lookback must still hold a full regular session before a release in the worst common case: a
    /// Monday 09:00 New York report after Thanksgiving week, when Thursday is closed and Friday closes at 13:00.
    /// Wednesday 25 Nov 2026 from its 09:30 open must be inside the window.
    /// </summary>
    [Fact]
    public void DefaultLookback_HoldsAFullSessionBeforeAMondayAfterThanksgiving()
    {
        var mondayNineNewYork = DateTimeOffset.Parse("2026-11-30T14:00:00Z");
        var windowStart = mondayNineNewYork.AddDays(-EarningsMonitorOptions.Default.SubDailyLookbackDays);

        Assert.True(windowStart <= DateTimeOffset.Parse("2026-11-25T14:30:00Z"));
    }

    private static OhlcvBar Bar(string ticker, DateTimeOffset timestamp) =>
        new(ticker, timestamp, "5m", 100m, 101m, 99m, 100m, 1000m);

    private sealed class RecordingMarketDataProvider(IReadOnlyList<OhlcvBar> source) : IMarketDataProvider
    {
        public List<(IReadOnlyCollection<string> Tickers, DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];
        public string? FailingTicker { get; init; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add((tickers.ToArray(), start, end));
            if (FailingTicker is { } failing && tickers.Contains(failing))
            {
                throw new HttpRequestException($"Simulated provider failure for {failing}.");
            }

            foreach (var bar in source.Where(bar => tickers.Contains(bar.Ticker) && bar.Timestamp >= start && bar.Timestamp <= end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RecordingCandleStore : ICandleStore
    {
        private readonly Lock gate = new();
        private int activeReads;

        public Dictionary<(string Ticker, DateTimeOffset Timestamp), OhlcvBar> Bars { get; } = [];
        public int WriteCount { get; private set; }
        public TimeSpan ReadDelay { get; init; }
        public int MaximumConcurrentReads { get; private set; }
        public int ReadCount { get; private set; }

        /// <summary>Stores a bar as if a previous run had persisted it, without counting a write.</summary>
        public void Seed(OhlcvBar bar) => Bars[(bar.Ticker, bar.Timestamp)] = bar;

        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCount++;
            foreach (var bar in request.Bars)
            {
                Seed(bar);
            }

            return Task.CompletedTask;
        }

        /// <summary>Returns the seeded bars for one ticker, recording how many reads overlap in time.</summary>
        public async Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                activeReads++;
                ReadCount++;
                MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, activeReads);
            }

            try
            {
                await Task.Delay(ReadDelay, cancellationToken);
                lock (gate)
                {
                    return Bars.Values
                        .Where(bar => bar.Ticker == request.Ticker &&
                            bar.Timeframe == request.Timeframe &&
                            bar.Timestamp >= request.Start &&
                            bar.Timestamp <= request.End)
                        .OrderBy(bar => bar.Timestamp)
                        .ToArray();
                }
            }
            finally
            {
                lock (gate)
                {
                    activeReads--;
                }
            }
        }
    }
}
