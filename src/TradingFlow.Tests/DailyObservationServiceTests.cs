using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Application.Preparation;
using TradingFlow.Contracts.Preparation;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class DailyObservationServiceTests
{
    private static readonly TimeZoneInfo Exchange = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ColdReadIsImmediateAndDeduplicated_ThenUsesSharedIndicators()
    {
        var fixture = new Fixture();
        fixture.Provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = fixture.Create();
        var first = service.GetAsync(" aapl ", CancellationToken.None);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal("loading", (await first).State);
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 20; i++) Assert.Equal("loading", (await service.GetAsync("AAPL", CancellationToken.None)).State);
        Assert.Equal(1, fixture.Provider.Calls);
        fixture.Provider.Release.SetResult();
        var result = await Ready(service);
        Assert.Equal("ready", result.State);
        Assert.Equal(1, fixture.Provider.Calls);
        var schedules = fixture.Calendar.Last!.Sessions.ToDictionary(x => x.Date,
            x => new MarketSessionSchedule(x.Date, x.TradingDay,
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.OpenUtc, Exchange).DateTime),
                TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.CloseUtc, Exchange).DateTime)));
        var expected = new IndicatorEngine(MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules))
            .Compute(fixture.Provider.LastBars);
        Assert.Equal(expected[^1], result.Snapshot);
        Assert.NotNull(result.Snapshot!.RelativeVolume);
        Assert.Equal(Now, result.ObservedAtUtc);
        var bars = fixture.Provider.LastBars;
        Assert.Equal((bars[^1].Open / bars[^2].Close - 1m) * 100m, result.CompletedSessionGapPercent);
    }

    [Theory]
    [InlineData("feed")]
    [InlineData("adjustment")]
    [InlineData("coverage")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("latest")]
    [InlineData("ohlc")]
    public async Task InvalidOrMissingEvidenceNeverBecomesReady(string defect)
    {
        var fixture = new Fixture();
        fixture.Provider.Transform = bars => defect switch
        {
            "feed" => bars.Select(x => x with { DataFeed = "iex" }).ToArray(),
            "adjustment" => bars.Select(x => x with { AdjustmentPolicy = "raw" }).ToArray(),
            "coverage" => bars.Select(x => x with { CoverageVerifiedThroughUtc = null }).ToArray(),
            "future" => bars.Select(x => x with { KnownAtUtc = Now.AddMinutes(1) }).ToArray(),
            "duplicate" => bars.Concat([bars[^1]]).ToArray(),
            "gap" => bars.Where((_, i) => i != bars.Length - 10).ToArray(),
            "latest" => bars[..^1],
            "ohlc" => bars.Select(x => x with { Low = x.High + 1 }).ToArray(),
            _ => bars
        };
        await using var service = fixture.Create();
        var result = await Ready(service);
        Assert.Equal("unavailable", result.State);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task LaterListingRetainsRealPartialTechnicalsWithoutScoringOrFabricatingHistory()
    {
        var fixture = new Fixture();
        fixture.Provider.Transform = bars => bars.TakeLast(30).ToArray();
        await using var service = fixture.Create();
        var result = await Ready(service);
        Assert.Equal("insufficient", result.State);
        Assert.Contains("30/260", result.Detail);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot.Ema200);
    }

    [Fact]
    public async Task IncompleteCalendarFailsBeforeProviderRequest()
    {
        var fixture = new Fixture();
        fixture.Calendar.OmitDate = new DateOnly(2026, 9, 7);
        await using var service = fixture.Create();
        var result = await Ready(service);
        Assert.Equal("unavailable", result.State);
        Assert.Equal(0, fixture.Provider.Calls);
    }

    [Fact]
    public async Task OpenSessionIsExcludedAndCachedResultExpiresAtActualEarlyClose()
    {
        var fixture = new Fixture(new DateTimeOffset(2026, 11, 27, 17, 59, 0, TimeSpan.Zero));
        fixture.Provider.IncludeOpenSession = true;
        await using var service = fixture.Create(new(ReadyLifetime: TimeSpan.FromHours(12)));
        var first = await Ready(service);
        Assert.Equal("ready", first.State);
        Assert.Equal(new DateOnly(2026, 11, 25), Date(first.Snapshot!.Timestamp));
        fixture.Clock.Now = new(2026, 11, 27, 18, 1, 0, TimeSpan.Zero);
        Assert.Equal("loading", (await service.GetAsync("AAPL", CancellationToken.None)).State);
        var second = await Ready(service);
        Assert.Equal(new DateOnly(2026, 11, 27), Date(second.Snapshot!.Timestamp));
        Assert.Equal(2, fixture.Provider.Calls);
    }

    [Fact]
    public async Task QueueCacheAndConcurrencyAreBoundedAndClientCancellationDoesNotCancelSharedLoad()
    {
        var fixture = new Fixture();
        fixture.Provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = fixture.Create(new(ConcurrentLoads: 1, CachedSymbols: 3));
        await service.GetAsync("AAPL", CancellationToken.None);
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.GetAsync("MSFT", CancellationToken.None);
        await service.GetAsync("GOOG", CancellationToken.None);
        var busy = await service.GetAsync("TSLA", CancellationToken.None);
        Assert.Contains("capacity", busy.Detail);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync("AAPL", cancelled.Token));
        Assert.Equal(1, fixture.Provider.Calls);
        fixture.Provider.Release.SetResult();
        Assert.Equal("ready", (await Ready(service)).State);
        Assert.Equal("ready", (await Ready(service, "MSFT")).State);
        Assert.Equal("ready", (await Ready(service, "GOOG")).State);
        Assert.Equal(3, fixture.Provider.Calls);
        Assert.Equal(1, fixture.Provider.MaximumActive);
    }

    [Fact]
    public async Task ProviderTimeoutIsVisibleAndRetriedAfterBoundedFailureLifetime()
    {
        var fixture = new Fixture();
        fixture.Provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = fixture.Create(new(LoadTimeout: TimeSpan.FromMilliseconds(50)));
        var failed = await Ready(service);
        Assert.Equal("unavailable", failed.State);
        Assert.Contains("timed out", failed.Detail);
        fixture.Provider.Release.SetResult();
        Assert.Equal("unavailable", (await service.GetAsync("AAPL", CancellationToken.None)).State);
        fixture.Clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("ready", (await Ready(service)).State);
        Assert.Equal(2, fixture.Provider.Calls);
    }

    [Fact]
    public async Task DisposalCancelsPendingProviderAndStopsNewWork()
    {
        var fixture = new Fixture();
        fixture.Provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Create();
        await service.GetAsync("AAPL", CancellationToken.None);
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        Assert.Equal(0, fixture.Provider.Active);
        Assert.Equal("unavailable", (await service.GetAsync("MSFT", CancellationToken.None)).State);
    }

    private static async Task<DailyObservationResult> Ready(DailyObservationService service, string symbol = "AAPL")
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            var result = await service.GetAsync(symbol, CancellationToken.None);
            if (result.State != "loading") return result;
            await Task.Delay(5);
        }
        throw new TimeoutException("Daily observation test did not settle.");
    }

    private sealed class Fixture
    {
        public MutableClock Clock { get; }
        public FakeCalendar Calendar { get; }
        public FakeProvider Provider { get; }
        public Fixture(DateTimeOffset? now = null)
        {
            Clock = new(now ?? Now);
            Calendar = new(Clock);
            Provider = new(Calendar);
        }
        public DailyObservationService Create(DailyObservationOptions? options = null) =>
            new(new(() => Provider), Calendar, Clock, NullLogger<DailyObservationService>.Instance, options);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeCalendar(MutableClock clock) : IPreparationCalendar
    {
        public DateOnly? OmitDate { get; set; }
        public PreparationCalendar? Last { get; private set; }
        public Task<PreparationCalendar> LoadAsync(DateOnly start, DateOnly end, CancellationToken token)
        {
            var sessions = Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(i => start.AddDays(i))
                .Where(d => d != OmitDate).Select(Session).ToArray();
            Last = new(clock.Now, "alpaca-calendar", sessions);
            return Task.FromResult(Last);
        }
        public static PreparationSession Session(DateOnly date)
        {
            var trading = date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
                date != new DateOnly(2026, 9, 7) && date != new DateOnly(2026, 11, 26);
            return new(date, trading, Utc(date, 9, 30), Utc(date, date == new DateOnly(2026, 11, 27) ? 13 : 16, 0));
        }
    }

    private sealed class FakeProvider(FakeCalendar calendar) : IMarketDataProvider
    {
        private int calls;
        private int active;
        public int Calls => calls;
        public int Active => active;
        public int MaximumActive { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; set; }
        public bool IncludeOpenSession { get; set; }
        public Func<OhlcvBar[], OhlcvBar[]> Transform { get; set; } = bars => bars;
        public OhlcvBar[] LastBars { get; private set; } = [];
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref active));
            Started.TrySetResult();
            try
            {
                if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
                LastBars = Transform(calendar.Last!.Sessions.Where(x => x.TradingDay && x.Date >= Date(start) &&
                    (x.CloseUtc <= end || IncludeOpenSession)).Select((x, i) => new OhlcvBar(tickers.Single(),
                        Utc(x.Date, 0, 0), "1d", 100 + i, 102 + i, 99 + i, 101 + i, 1_000_000 + i * 1000,
                        "sip", "all", KnownAtUtc: null, CoverageVerifiedThroughUtc: end)).ToArray());
                foreach (var bar in LastBars)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return bar;
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private static DateOnly Date(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Exchange).DateTime);
    private static DateTimeOffset Utc(DateOnly date, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new(hour, minute), DateTimeKind.Unspecified), Exchange));
}
