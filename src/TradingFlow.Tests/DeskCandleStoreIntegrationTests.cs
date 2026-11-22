using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Data.Candles;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// End-to-end behavioral integration tests verifying that <see cref="WorkspacePulseService"/>,
/// <see cref="CatalystOptionsAdrService"/>, <see cref="SectorLeadersService"/>, and
/// <see cref="DeskMarketData.LoadDailyBarsWithStoreAsync"/> persist verified native completed <c>1d</c> bars
/// into a real <see cref="LocalFileCandleStore"/> and serve subsequent fresh service instances with zero
/// upstream bar requests, while never persisting incomplete in-progress bars or <c>1h</c>-resampled fallbacks.
/// </summary>
public sealed class DeskCandleStoreIntegrationTests : IDisposable
{
    private static readonly DateOnly LatestSession = new(2026, 11, 19);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero); // Fri 10:00 NY
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "tf-desk-store-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verifies that <see cref="SectorLeadersService.GetLeadersAsync"/> persists completed daily bars to a real
    /// <see cref="LocalFileCandleStore"/> on the first call and serves a fresh cache instance with zero provider calls.
    /// </summary>
    [Fact]
    public async Task SectorLeadersService_PersistsCompletedBarsAndServesFreshCacheWithZeroProviderCalls()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var fundsFile = Path.Combine(tempRoot, "sector-funds.txt");
        Directory.CreateDirectory(tempRoot);
        var funds = new[] { "XLK", "XLF", "XLI", "XLV", "XLY", "XLC", "XLE" };
        await File.WriteAllLinesAsync(fundsFile, funds);

        var barsBySymbol = funds.Select((symbol, index) => (symbol, bars: BuildDailyBars(symbol, 260, LatestSession, 100m, 0.15m * (funds.Length - index))))
            .ToDictionary(item => item.symbol, item => item.bars, StringComparer.OrdinalIgnoreCase);
        var provider = new CountingMarketDataProvider(barsBySymbol);
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-420), LatestSession.AddDays(2));

        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        var first = await SectorLeadersService.GetLeadersAsync(
            () => provider, fundsFile, LatestSession, schedules, Now, firstCache, CancellationToken.None, store, "alpaca");
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Null(first.UnavailableReason);
        Assert.Equal(LatestSession, first.AsOfSession);
        Assert.Equal(6, first.Leaders.Count);
        Assert.Equal("XLK", first.Leaders[0].Symbol);
        Assert.Equal(1, provider.BarRequestCount);

        // Fresh MemoryCache simulates process restart or cache eviction: LocalFileCandleStore must satisfy all 7 funds with 0 provider calls.
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var second = await SectorLeadersService.GetLeadersAsync(
            () => provider, fundsFile, LatestSession, schedules, Now, secondCache, CancellationToken.None, store, "alpaca");
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Null(second.UnavailableReason);
        Assert.Equal(first.AsOfSession, second.AsOfSession);
        Assert.Equal(first.Leaders.Select(l => (l.Symbol, l.Return12Minus1)), second.Leaders.Select(l => (l.Symbol, l.Return12Minus1)));
        Assert.Equal(1, provider.BarRequestCount);
    }

    /// <summary>
    /// Verifies fail-closed intermediate gap and corrupt-bar detection in both the 20-day ADR window and the 254-day
    /// sector window: missing an interior trading session or holding a <c>Close &lt;= 0</c> bar forces a provider re-fetch.
    /// </summary>
    [Fact]
    public async Task DeskMarketData_IntermediateGapOrCorruptBarIn20DayAnd254DayWindows_TriggersProviderRefetch()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-420), LatestSession.AddDays(2));
        var context = new CandleStoreContext("desk", "daily-bars-65d", "alpaca");
        var longContext = new CandleStoreContext("desk", "daily-bars-400d", "alpaca");

        // 1. 20-day window: seed 25 completed sessions ending on LatestSession, then remove interior session index 15 (gap).
        var full20 = BuildDailyBars("GAP20", 25, LatestSession, 100m, 0.5m);
        var gapped20 = full20.Where((_, index) => index != 15).ToArray();
        await store.UpsertBarsAsync(new CandleStoreWriteRequest(context, "provider", gapped20), CancellationToken.None);

        // Also seed a 20-day symbol with an interior corrupt bar (Close <= 0) at index 18.
        var fullBad20 = BuildDailyBars("BAD20", 25, LatestSession, 100m, 0.5m);
        var corrupt20 = fullBad20.Select((bar, index) => index == 18 ? bar with { Close = 0m } : bar).ToArray();
        await store.UpsertBarsAsync(new CandleStoreWriteRequest(context, "provider", corrupt20), CancellationToken.None);

        // 2. 254-day window: seed 265 completed sessions ending on LatestSession, then remove interior session index 200 (gap),
        //    and another symbol with a corrupt Close <= 0 bar at index 180.
        var full254 = BuildDailyBars("GAP254", 265, LatestSession, 100m, 0.2m);
        var gapped254 = full254.Where((_, index) => index != 200).ToArray();
        await store.UpsertBarsAsync(new CandleStoreWriteRequest(longContext, "provider", gapped254), CancellationToken.None);

        var fullBad254 = BuildDailyBars("BAD254", 265, LatestSession, 100m, 0.2m);
        var corrupt254 = fullBad254.Select((bar, index) => index == 180 ? bar with { Close = -1m } : bar).ToArray();
        await store.UpsertBarsAsync(new CandleStoreWriteRequest(longContext, "provider", corrupt254), CancellationToken.None);

        var provider = new CountingMarketDataProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["GAP20"] = full20,
            ["BAD20"] = fullBad20,
            ["GAP254"] = full254,
            ["BAD254"] = fullBad254
        });

        // Both 20-day symbols must fail validation and trigger a provider fetch (never falsely skip provider).
        await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => provider, ["GAP20", "BAD20"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(1, provider.BarRequestCount);

        // Both 254-day symbols must fail validation and trigger a provider fetch (never falsely skip provider).
        await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => provider, ["GAP254", "BAD254"], Now.AddDays(-400), Now, requiredSessions: 254, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(2, provider.BarRequestCount);

        // After the provider repaired the store, re-reading all 4 symbols must make 0 additional provider calls.
        await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => provider, ["GAP20", "BAD20"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => provider, ["GAP254", "BAD254"], Now.AddDays(-400), Now, requiredSessions: 254, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(2, provider.BarRequestCount);
    }

    /// <summary>
    /// After a split the provider re-adjusts every past price. A short refetch must not leave a longer stored set holding a
    /// mixture of old and new price bases.
    /// </summary>
    [Fact]
    public async Task DeskMarketData_ShortRefetchNeverMixesPriceBasesIntoALongerStoredSet()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-420), LatestSession.AddDays(8));
        var before = BuildDailyBars("XLK", 265, LatestSession, 400m, 0m);
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => new CountingMarketDataProvider(Only("XLK", before)), ["XLK"],
            Now.AddDays(-400), Now, 254, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();

        // Next session: a 2-for-1 split, so the provider now serves every past bar at half the price.
        var nextSession = new DateOnly(2026, 11, 20);
        var later = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
        var after = BuildDailyBars("XLK", 266, nextSession, 200m, 0m);
        var provider = new CountingMarketDataProvider(Only("XLK", after));
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["XLK"], later.AddDays(-65), later, 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        var longSet = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["XLK"], later.AddDays(-400), later, 254, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(200m, Assert.Single(longSet["XLK"].Select(bar => bar.Close).Distinct()));
        Assert.Equal(2, provider.BarRequestCount);

        // Read back from the store alone: one price basis, no provider call.
        var fromStore = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["XLK"], later.AddDays(-400), later, 254, schedules, store, "alpaca", CancellationToken.None);
        Assert.Equal(200m, Assert.Single(fromStore["XLK"].Select(bar => bar.Close).Distinct()));
        Assert.Equal(2, provider.BarRequestCount);
    }

    /// <summary>An ordinary new session stores one new bar per symbol, not the whole history again.</summary>
    [Fact]
    public async Task DeskMarketData_NewSessionStoresOnlyTheBarsThatChanged()
    {
        var store = new CountingWriteStore(new LocalFileCandleStore(Path.Combine(tempRoot, "candles")));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-120), LatestSession.AddDays(8));
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 40, LatestSession, 100m, 0m))),
            ["MU"], Now.AddDays(-65), Now, 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.True(store.Written[0] > 30);
        var later = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 41, new DateOnly(2026, 11, 20), 100m, 0m))),
            ["MU"], later.AddDays(-65), later, 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(1, store.Written.Skip(2).Sum());
    }

    /// <summary>Storing fetched bars never depends on the page request, so a cancelled request cannot lose or fail them.</summary>
    [Fact]
    public async Task DeskMarketData_CancelledRequestStillReturnsFetchedBarsAndFinishesTheWrite()
    {
        using var request = new CancellationTokenSource();
        var store = new CancellingWriteStore(request);
        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 25, LatestSession, 100m, 0m))), ["MU"], Now.AddDays(-65), Now, 20,
            BuildWeekdaySchedules(LatestSession.AddDays(-120), LatestSession.AddDays(2)), store, "alpaca", request.Token);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(25, loaded["MU"].Count);
        Assert.False(store.WriteSawCancellation);
    }

    /// <summary>A failed write of the older bars must leave the newest bar unwritten, so the set is never taken as complete.</summary>
    [Fact]
    public async Task DeskMarketData_InterruptedWriteIsNeverAcceptedAsComplete()
    {
        var inner = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-120), LatestSession.AddDays(8));
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 45, LatestSession, 400m, 0m))),
            ["MU"], Now.AddDays(-65), Now, 20, schedules, inner, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();

        // A split re-prices everything and the write of the older bars stops half way.
        var later = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
        var provider = new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 46, new DateOnly(2026, 11, 20), 200m, 0m)));
        await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["MU"], later.AddDays(-65), later, 20, schedules, new HalfFirstWriteStore(inner), "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        var partly = await inner.ReadBarsAsync(new CandleStoreReadRequest("desk", "daily-bars-65d", "alpaca", "MU", "1d", later.AddDays(-65), later, Source: "provider", AsOfUtc: null), CancellationToken.None);
        Assert.Equal(2, partly.Select(bar => bar.Close).Distinct().Count());
        Assert.DoesNotContain(partly, bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime) == new DateOnly(2026, 11, 20));

        // The half-written set is not taken as complete: the next load fetches again and leaves one price basis.
        var repaired = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["MU"], later.AddDays(-65), later, 20, schedules, inner, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(2, provider.BarRequestCount);
        Assert.Equal(200m, Assert.Single(repaired["MU"].Select(bar => bar.Close).Distinct()));
        var fromStore = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["MU"], later.AddDays(-65), later, 20, schedules, inner, "alpaca", CancellationToken.None);
        Assert.Equal(200m, Assert.Single(fromStore["MU"].Select(bar => bar.Close).Distinct()));
        Assert.Equal(2, provider.BarRequestCount);
    }

    /// <summary>A provider answer with a missing session, or bars of another feed, is used but never stored.</summary>
    [Theory]
    [InlineData("gap")]
    [InlineData("iex")]
    public async Task DeskMarketData_IncompleteOrOtherFeedAnswersAreNotStored(string fault)
    {
        var store = new CountingWriteStore(new LocalFileCandleStore(Path.Combine(tempRoot, "candles")));
        var full = BuildDailyBars("MU", 40, LatestSession, 100m, 0m);
        IReadOnlyList<OhlcvBar> answer = fault == "gap" ? full.Where((_, index) => index != 30).ToArray() : full.Select(bar => bar with { DataFeed = "iex" }).ToArray();
        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => new CountingMarketDataProvider(Only("MU", answer)), ["MU"], Now.AddDays(-65), Now, 20,
            BuildWeekdaySchedules(LatestSession.AddDays(-120), LatestSession.AddDays(2)), store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.NotEmpty(loaded["MU"]);
        Assert.Empty(store.Written);
    }

    /// <summary>A store that hangs on reads is treated as a miss and the bars are fetched.</summary>
    [Fact]
    public async Task DeskMarketData_SlowStoreReadFallsBackToTheProvider()
    {
        var provider = new CountingMarketDataProvider(Only("MU", BuildDailyBars("MU", 40, LatestSession, 100m, 0m)));
        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(() => provider, ["MU"], Now.AddDays(-65), Now, 20,
            BuildWeekdaySchedules(LatestSession.AddDays(-120), LatestSession.AddDays(2)), new HangingReadStore(), "alpaca", CancellationToken.None);
        Assert.NotEmpty(loaded["MU"]);
        Assert.Equal(1, provider.BarRequestCount);
    }

    // One symbol's bars as a provider source.
    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> Only(string symbol, IReadOnlyList<OhlcvBar> bars) =>
        new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase) { [symbol] = bars };

    /// <summary>
    /// Verifies that lazy provider factories allocate zero providers on warm cache/store hits, coalesce concurrent
    /// cold calendar misses via SingleFlight, and call <see cref="IDisposable.Dispose"/> on every created provider.
    /// </summary>
    [Fact]
    public async Task DeskMarketData_LazyProviderFactory_SkipsCreationOnWarmHitCoalescesColdCalendarAndDisposesCreatedProviders()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-65), LatestSession.AddDays(65));
        var barsMap = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSFT"] = BuildDailyBars("MSFT", 30, LatestSession, 400m, 1m)
        };

        var createdCount = 0;
        var disposedCount = 0;
        var calendarLoads = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        DisposableTestProvider CreateProvider()
        {
            Interlocked.Increment(ref createdCount);
            return new DisposableTestProvider(
                barsMap,
                async ct =>
                {
                    Interlocked.Increment(ref calendarLoads);
                    await gate.Task.WaitAsync(ct);
                    return schedules;
                },
                () => Interlocked.Increment(ref disposedCount));
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());

        // 1. Two concurrent cold calendar reads must coalesce via SingleFlight into 1 provider creation, 1 load, and 1 Dispose.
        var t1 = DeskMarketData.TryLoadSchedulesCachedAsync(CreateProvider, Now, 65, cache, CancellationToken.None, daysForward: 65, disposeCreatedProvider: true);
        var t2 = DeskMarketData.TryLoadSchedulesCachedAsync(CreateProvider, Now, 65, cache, CancellationToken.None, daysForward: 65, disposeCreatedProvider: true);
        gate.SetResult(true);
        var r1 = await t1;
        var r2 = await t2;
        Assert.True(r1.Succeeded && r2.Succeeded);
        Assert.Same(r1.Schedules, r2.Schedules);
        Assert.Equal(1, createdCount);
        Assert.Equal(1, calendarLoads);
        Assert.Equal(1, disposedCount);

        // 2. Warm calendar hit must invoke providerFactory 0 times.
        var r3 = await DeskMarketData.TryLoadSchedulesCachedAsync(CreateProvider, Now, 65, cache, CancellationToken.None, daysForward: 65, disposeCreatedProvider: true);
        Assert.True(r3.Succeeded);
        Assert.Equal(1, createdCount);

        // 3. A cold store creates one provider; a warm store creates none.
        var coldBars = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            CreateProvider, ["MSFT"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(30, coldBars["MSFT"].Count);
        Assert.Equal(2, createdCount);

        var warmBars = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            CreateProvider, ["MSFT"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(30, warmBars["MSFT"].Count);
        Assert.Equal(2, createdCount); // Zero provider creation on warm ICandleStore hit!
    }

    /// <summary>
    /// Verifies that <see cref="DeskMarketData.LoadDailyBarsWithStoreAsync"/> never persists an incomplete in-progress
    /// daily bar (<c>Timestamp + 1d &gt; now</c>) and never persists <c>1h</c>-resampled fallback bars as native
    /// <c>provider</c> <c>1d</c> records in <see cref="LocalFileCandleStore"/>.
    /// </summary>
    [Fact]
    public async Task DeskMarketData_NeverPersistsIncompleteDailyBarsOrHourlyFallbackBars()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-60), LatestSession.AddDays(2));

        // 1. Provider returns 20 completed 1d bars PLUS 1 in-progress 1d bar for today (2026-11-20 00:00Z -> completes 2026-11-21 00:00Z > Now).
        var completedSpy = BuildDailyBars("SPY", 20, LatestSession, 500m, 1m);
        var inProgressBar = new OhlcvBar("SPY", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "1d", 520m, 525m, 519m, 524m, 100m);
        var providerWithInProgress = new CountingMarketDataProvider(
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPY"] = [.. completedSpy, inProgressBar]
            });

        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => providerWithInProgress, ["SPY"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(1, providerWithInProgress.BarRequestCount);
        Assert.Equal(21, loaded["SPY"].Count);

        // Verify raw store contents strictly exclude the incomplete 2026-11-20 bar (even when read with AsOfUtc = null!).
        var persistedSpy = await store.ReadBarsAsync(
            new CandleStoreReadRequest("desk", "daily-bars-65d", "alpaca", "SPY", "1d", Now.AddDays(-65), Now.AddDays(5), Source: "provider", AsOfUtc: null),
            CancellationToken.None);
        Assert.Equal(20, persistedSpy.Count);
        Assert.DoesNotContain(persistedSpy, bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime) == new DateOnly(2026, 11, 20));

        // Subsequent call must be served entirely from LocalFileCandleStore with 0 provider calls.
        var secondRead = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => providerWithInProgress, ["SPY"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, store, "alpaca", CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(20, secondRead["SPY"].Count);
        Assert.Equal(1, providerWithInProgress.BarRequestCount);

        // 2. Provider with ONLY 1h bars (allowHourlyFallback: true) returns resampled daily bars but persists ZERO 1d bars into ICandleStore.
        var hourlyOnly = new HourlyOnlyProvider("QQQ", LatestSession, sessions: 5);
        var fallback = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => hourlyOnly, ["QQQ"], Now.AddDays(-20), Now, requiredSessions: 5, schedules, store, "alpaca", CancellationToken.None, allowHourlyFallback: true);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.NotEmpty(fallback["QQQ"]);
        var persistedQqq = await store.ReadBarsAsync(
            new CandleStoreReadRequest("desk", "daily-bars-20d", "alpaca", "QQQ", "1d", Now.AddDays(-20), Now.AddDays(5), Source: null, AsOfUtc: null),
            CancellationToken.None);
        Assert.Empty(persistedQqq);
    }

    /// <summary>
    /// Verifies end-to-end that <see cref="CatalystOptionsAdrService"/> persists completed 1d bars to a real
    /// <see cref="LocalFileCandleStore"/> on first load and serves a brand-new service instance (empty cache)
    /// with zero upstream <c>/v2/stocks/bars</c> HTTP calls and zero new bar raw-archive writes.
    /// </summary>
    [Fact]
    public async Task CatalystOptionsAdrService_PersistsDailyBarsAndServesFreshServiceWithZeroBarRequests()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var rawArchiveDir = Path.Combine(tempRoot, "raw");
        var barHttpCalls = 0;
        var calendarHttpCalls = 0;
        var providerCreations = 0;
        var barsMap = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = BuildDailyBars("AAPL", 30, LatestSession, 200m, 0.5m)
        };
        var runtimeFactory = CreateScriptedRuntimeFactory(
            rawArchiveDir,
            barsMap,
            () => Interlocked.Increment(ref barHttpCalls),
            () => Interlocked.Increment(ref calendarHttpCalls),
            () => Interlocked.Increment(ref providerCreations));
        var catalog = CreateCatalog();
        var clock = new FixedClock(Now);
        var optionsSource = new StubOptionsSource(Now);
        var emptyConfig = new ConfigurationBuilder().Build();
        var quotes = new AlpacaQuoteService(
            new AlpacaCredentialProvider(emptyConfig),
            new StubHttpClientFactory(),
            NullLogger<AlpacaQuoteService>.Instance);

        // First service instance: fetches calendar + AAPL daily bars once and persists to LocalFileCandleStore.
        using var cache1 = new MemoryCache(new MemoryCacheOptions());
        var service1 = new CatalystOptionsAdrService(
            catalog, runtimeFactory, optionsSource, quotes, clock, cache1, NullLogger<CatalystOptionsAdrService>.Instance, store);
        var first = await service1.LoadAdrAsync(["AAPL"], CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Null(first["AAPL"].UnavailableReason);
        Assert.NotNull(first["AAPL"].Adr20Pct);
        Assert.Equal(LatestSession, first["AAPL"].AsOfSessionDate);
        Assert.Equal(1, barHttpCalls);
        Assert.Equal(1, calendarHttpCalls);
        Assert.Equal(2, providerCreations); // one for the shared calendar load, one for the bars

        // Warm calendar hit on TryLoadOptionsSchedulesAsync must allocate 0 providers and make 0 HTTP calls.
        var cachedSchedules = await service1.TryLoadOptionsSchedulesAsync(CancellationToken.None);
        Assert.NotNull(cachedSchedules);
        Assert.Equal(1, calendarHttpCalls);
        Assert.Equal(2, providerCreations); // one for the shared calendar load, one for the bars

        // Evict only the ADR result key while calendar remains in cache1 and bars remain in LocalFileCandleStore:
        // DownloadAdrAsync must allocate 0 providers (0 HttpClients) and make 0 HTTP calls!
        cache1.Remove($"adr20:AAPL:{DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, VixRegime.NewYorkTimeZone).DateTime):yyyy-MM-dd}");
        var warmStore = await service1.LoadAdrAsync(["AAPL"], CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(first["AAPL"].Adr20Pct, warmStore["AAPL"].Adr20Pct);
        Assert.Equal(1, barHttpCalls);
        Assert.Equal(1, calendarHttpCalls);
        Assert.Equal(2, providerCreations); // one for the shared calendar load, one for the bars

        // Brand-new service instance with an empty MemoryCache: must serve AAPL from LocalFileCandleStore with 0 /v2/stocks/bars calls!
        using var cache2 = new MemoryCache(new MemoryCacheOptions());
        var service2 = new CatalystOptionsAdrService(
            catalog, runtimeFactory, optionsSource, quotes, clock, cache2, NullLogger<CatalystOptionsAdrService>.Instance, store);
        var second = await service2.LoadAdrAsync(["AAPL"], CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Null(second["AAPL"].UnavailableReason);
        Assert.Equal(first["AAPL"].Adr20Pct, second["AAPL"].Adr20Pct);
        Assert.Equal(LatestSession, second["AAPL"].AsOfSessionDate);
        Assert.Equal(1, barHttpCalls); // Zero additional /v2/stocks/bars HTTP requests!
    }

    /// <summary>
    /// Verifies end-to-end that <see cref="WorkspacePulseService"/> persists SPY, QQQ, and sector-fund completed daily bars
    /// into a real <see cref="LocalFileCandleStore"/>, allocates zero providers when the calendar cache and candle store are
    /// warm, and serves a brand-new service instance (empty cache) with zero additional <c>/v2/stocks/bars</c> requests.
    /// </summary>
    [Fact]
    public async Task WorkspacePulseService_PersistsBenchmarksAndSectorsAndServesFreshServiceWithZeroBarRequests()
    {
        var store = new LocalFileCandleStore(Path.Combine(tempRoot, "candles"));
        var rawArchiveDir = Path.Combine(tempRoot, "raw");
        var barHttpCalls = 0;
        var calendarHttpCalls = 0;
        var providerCreations = 0;
        var repoRoot = TestRepository.FindRoot();
        var sectorFile = Path.Combine(repoRoot, "configs", "research", "sector-funds.txt");
        var sectorSymbols = File.ReadAllLines(sectorFile)
            .Select(line => line.Trim().ToUpperInvariant())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var barsMap = new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPY"] = BuildDailyBars("SPY", 80, LatestSession, 500m, 1m),
            ["QQQ"] = BuildDailyBars("QQQ", 80, LatestSession, 400m, 0.8m)
        };
        for (var i = 0; i < sectorSymbols.Length; i++)
        {
            barsMap[sectorSymbols[i]] = BuildDailyBars(sectorSymbols[i], 260, LatestSession, 100m, 0.05m * (i + 1));
        }

        var runtimeFactory = CreateScriptedRuntimeFactory(
            rawArchiveDir,
            barsMap,
            () => Interlocked.Increment(ref barHttpCalls),
            () => Interlocked.Increment(ref calendarHttpCalls),
            () => Interlocked.Increment(ref providerCreations));
        var catalog = CreateCatalog();
        var paths = new ProjectPaths(repoRoot);
        var clock = new FixedClock(Now);
        var vixSource = new StubVixSource(VixCloseSeries.From([new KeyValuePair<DateOnly, decimal>(LatestSession, 16m)]));

        // First service instance: populates SPY, QQQ, and all sector funds into LocalFileCandleStore.
        using var cache1 = new MemoryCache(new MemoryCacheOptions());
        var pulse1Service = new WorkspacePulseService(
            catalog, runtimeFactory, paths, clock, cache1, NullLogger<WorkspacePulseService>.Instance, vixSource, store);
        var pulse1 = await pulse1Service.GetAsync(CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.True(pulse1.Spy.Available);
        Assert.True(pulse1.Qqq.Available);
        Assert.Equal(6, pulse1.TopSectorLeaders.Count);
        Assert.True(barHttpCalls >= 3, $"Expected SPY, QQQ, and sector bar requests on cold start, got {barHttpCalls}.");
        var coldBarCalls = barHttpCalls;
        var coldProviderCreations = providerCreations;

        // Evict the 60s strip cache and per-session memory caches while calendar stays cached and bars stay in LocalFileCandleStore:
        // Rebuilding the full strip must allocate 0 providers and make 0 HTTP calls!
        cache1.Remove("workspace-pulse");
        cache1.Remove($"sector-leaders:{LatestSession:yyyy-MM-dd}");
        var warmRebuild = await pulse1Service.GetAsync(CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.True(warmRebuild.Spy.Available);
        Assert.Equal(pulse1.TopSectors, warmRebuild.TopSectors);
        Assert.Equal(coldProviderCreations, providerCreations);
        Assert.Equal(coldBarCalls, barHttpCalls);

        // Second fresh service instance with an empty MemoryCache: must serve SPY, QQQ, and sectors with ZERO /v2/stocks/bars calls!
        using var cache2 = new MemoryCache(new MemoryCacheOptions());
        var pulse2Service = new WorkspacePulseService(
            catalog, runtimeFactory, paths, clock, cache2, NullLogger<WorkspacePulseService>.Instance, vixSource, store);
        var pulse2 = await pulse2Service.GetAsync(CancellationToken.None);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.True(pulse2.Spy.Available);
        Assert.Equal(pulse1.Spy.LastClose, pulse2.Spy.LastClose);
        Assert.True(pulse2.Qqq.Available);
        Assert.Equal(pulse1.Qqq.LastClose, pulse2.Qqq.LastClose);
        Assert.Equal(pulse1.TopSectors, pulse2.TopSectors);
        Assert.Equal(coldBarCalls, barHttpCalls); // ZERO new /v2/stocks/bars calls!
    }

    /// <summary>Verifies that candle-store I/O errors degrade gracefully to provider fetches instead of crashing Desk reads.</summary>
    [Fact]
    public async Task DeskMarketData_DegradesGracefullyWhenCandleStoreThrowsIoException()
    {
        var schedules = BuildWeekdaySchedules(LatestSession.AddDays(-60), LatestSession.AddDays(2));
        var provider = new CountingMarketDataProvider(
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPY"] = BuildDailyBars("SPY", 25, LatestSession, 500m, 1m)
            });
        var failingStore = new ThrowingCandleStore();

        var loaded = await DeskMarketData.LoadDailyBarsWithStoreAsync(
            () => provider, ["SPY"], Now.AddDays(-65), Now, requiredSessions: 20, schedules, failingStore, "alpaca", CancellationToken.None,
            logger: NullLogger.Instance);
        await DeskMarketData.WhenStoreWritesFinishAsync();
        Assert.Equal(25, loaded["SPY"].Count);
        Assert.Equal(1, provider.BarRequestCount);
    }

    // Builds the production ConfigCatalogService backed by repository configs and a disposable experiment root.
    private ConfigCatalogService CreateCatalog()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifacts = new StrategyArtifactCatalog(repoRoot, Path.Combine(repoRoot, "configs", "strategy-catalog.json"), reader);
        var experiments = new StrategyExperimentArtifactStore(Path.Combine(tempRoot, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
        return new ConfigCatalogService(new ProjectPaths(repoRoot), reader, artifacts, experiments, new StrategyAuthorizationTestRegistry());
    }

    // Builds a PaperRuntimeFactory wired to an offline scripted Alpaca HTTP handler serving /v2/calendar and /v2/stocks/bars.
    private PaperRuntimeFactory CreateScriptedRuntimeFactory(
        string rawArchiveDir,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsBySymbol,
        Action onBarsRequest,
        Action onCalendarRequest,
        Action? onProviderCreated = null)
    {
        var repoRoot = TestRepository.FindRoot();
        var paths = new ProjectPaths(repoRoot);
        var rawWriter = new FileSystemRawArchiveWriter(new RawArchiveOptions(rawArchiveDir));
        var providers = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "test-key", SecretKey = "test-secret" },
            () => FinvizOptions.CreateDefault(),
            () => null,
            createHttpClient: () =>
            {
                onProviderCreated?.Invoke();
                return new HttpClient(new ScriptedAlpacaHandler(barsBySymbol, onBarsRequest, onCalendarRequest));
            });
        return new PaperRuntimeFactory(
            new AlpacaCredentialProvider(new ConfigurationBuilder().Build()),
            paths,
            rawArchiveWriter: rawWriter,
            dataProviders: providers);
    }

    // Builds consecutive weekday UTC-midnight 1d bars ending on lastDate.
    private static IReadOnlyList<OhlcvBar> BuildDailyBars(string symbol, int count, DateOnly lastDate, decimal startClose, decimal step)
    {
        var dates = new List<DateOnly>(count);
        for (var cursor = lastDate; dates.Count < count; cursor = cursor.AddDays(-1))
        {
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(cursor);
        }

        dates.Reverse();
        return dates.Select((date, index) =>
        {
            var close = startClose + (index * step);
            return new OhlcvBar(symbol, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d",
                close - 0.5m, close + 1.0m, close - 1.0m, close, 1_000_000m, "sip", "all");
        }).ToArray();
    }

    // Builds a weekday-only authoritative exchange calendar over [start, end].
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> BuildWeekdaySchedules(DateOnly start, DateOnly end)
    {
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            map[d] = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(d)
                : MarketSessionSchedule.RegularDay(d);
        }

        return map;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class DisposableTestProvider(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsBySymbol,
        Func<CancellationToken, Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>> loadSchedules,
        Action onDispose)
        : IMarketDataProvider, IMarketSessionScheduleProvider, IDisposable
    {
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken) =>
            loadSchedules(cancellationToken);

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var ticker in tickers)
            {
                if (!barsBySymbol.TryGetValue(ticker, out var list)) continue;
                foreach (var bar in list)
                {
                    if (timeframes.Any(tf => tf.Equals(bar.Timeframe, StringComparison.OrdinalIgnoreCase)) &&
                        bar.Timestamp >= start && bar.Timestamp <= end)
                    {
                        yield return bar;
                    }
                }
            }
        }

        public void Dispose() => onDispose();
    }

    private sealed class CountingMarketDataProvider(IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsBySymbol) : IMarketDataProvider
    {
        public int BarRequestCount { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            BarRequestCount++;
            await Task.CompletedTask;
            foreach (var ticker in tickers)
            {
                if (!barsBySymbol.TryGetValue(ticker, out var list)) continue;
                foreach (var bar in list)
                {
                    if (timeframes.Any(tf => tf.Equals(bar.Timeframe, StringComparison.OrdinalIgnoreCase)) &&
                        bar.Timestamp >= start && bar.Timestamp <= end)
                    {
                        yield return bar;
                    }
                }
            }
        }
    }

    private sealed class HourlyOnlyProvider(string symbol, DateOnly lastDate, int sessions) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (!timeframes.Any(tf => tf.Equals("1h", StringComparison.OrdinalIgnoreCase)))
            {
                yield break;
            }

            var dates = new List<DateOnly>(sessions);
            for (var cursor = lastDate; dates.Count < sessions; cursor = cursor.AddDays(-1))
            {
                if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                dates.Add(cursor);
            }

            dates.Reverse();
            foreach (var date in dates)
            {
                for (var hour = 14; hour < 20; hour++)
                {
                    var ts = new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 30)), TimeSpan.Zero);
                    if (ts >= start && ts <= end)
                    {
                        yield return new OhlcvBar(symbol, ts, "1h", 400m, 402m, 399m, 401m, 100_000m, "sip", "all");
                    }
                }
            }
        }
    }

    // Stores half of its first write and then fails, as an interrupted process would.
    private sealed class HalfFirstWriteStore(ICandleStore inner) : ICandleStore
    {
        private int writes;

        public async Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref writes) != 1)
            {
                await inner.UpsertBarsAsync(request, cancellationToken);
                return;
            }

            await inner.UpsertBarsAsync(request with { Bars = request.Bars.Take(request.Bars.Count / 2).ToArray() }, cancellationToken);
            throw new IOException("Simulated interruption.");
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken) =>
            inner.ReadBarsAsync(request, cancellationToken);
    }

    // Never finishes a read until it is cancelled.
    private sealed class HangingReadStore : ICandleStore
    {
        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }

    private sealed class CountingWriteStore(ICandleStore inner) : ICandleStore
    {
        // How many bars each write carried.
        public List<int> Written { get; } = [];

        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
        {
            Written.Add(request.Bars.Count);
            return inner.UpsertBarsAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken) =>
            inner.ReadBarsAsync(request, cancellationToken);
    }

    // Cancels the page request as soon as the write starts and reports whether the write token was cancelled with it.
    private sealed class CancellingWriteStore(CancellationTokenSource request) : ICandleStore
    {
        public bool WriteSawCancellation { get; private set; }

        public Task UpsertBarsAsync(CandleStoreWriteRequest write, CancellationToken cancellationToken)
        {
            request.Cancel();
            WriteSawCancellation = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest read, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OhlcvBar>>([]);
    }

    private sealed class ThrowingCandleStore : ICandleStore
    {
        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Simulated disk full during candle write."));

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<OhlcvBar>>(new IOException("Simulated disk read error."));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubVixSource(VixCloseSeries series) : IVixCloseSource
    {
        public Task<VixCloseSeries> LoadLatestAsync(CancellationToken cancellationToken) => Task.FromResult(series);
    }

    private sealed class StubOptionsSource(DateTimeOffset now) : IOptionsSentimentSource
    {
        public Task<OptionsSentimentSnapshot> GetSnapshotAsync(
            string symbol,
            decimal? spotPriceHint = null,
            decimal? adr20Pct = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OptionsSentimentSnapshot.Unavailable(symbol, now, "Stub offline snapshot"));
    }

    private sealed class ScriptedAlpacaHandler(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsBySymbol,
        Action onBarsRequest,
        Action onCalendarRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/v2/calendar", StringComparison.OrdinalIgnoreCase))
            {
                onCalendarRequest();
                var query = ParseQuery(uri.Query);
                var start = DateOnly.ParseExact(query["start"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var end = DateOnly.ParseExact(query["end"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var days = new List<object>();
                for (var d = start; d <= end; d = d.AddDays(1))
                {
                    if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                    days.Add(new { date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = "09:30", close = "16:00" });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(days), Encoding.UTF8, "application/json")
                });
            }

            if (uri.AbsolutePath.EndsWith("/v2/stocks/bars", StringComparison.OrdinalIgnoreCase))
            {
                onBarsRequest();
                var query = ParseQuery(uri.Query);
                var requested = query["symbols"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var start = DateTimeOffset.Parse(query["start"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                var end = DateTimeOffset.Parse(query["end"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                var payloadBars = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in requested)
                {
                    if (!barsBySymbol.TryGetValue(symbol, out var list)) continue;
                    payloadBars[symbol] = list
                        .Where(bar => bar.Timestamp >= start && bar.Timestamp <= end)
                        .Select(bar => (object)new
                        {
                            t = bar.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                            o = bar.Open,
                            h = bar.High,
                            l = bar.Low,
                            c = bar.Close,
                            v = bar.Volume
                        })
                        .ToArray();
                }

                var json = JsonSerializer.Serialize(new { bars = payloadBars, next_page_token = (string?)null });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                dict[Uri.UnescapeDataString(part[..idx])] = Uri.UnescapeDataString(part[(idx + 1)..]);
            }

            return dict;
        }
    }
}
