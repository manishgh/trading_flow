using System.Net;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class CachedPreparedHistoryConsumerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-08T00:00:00Z");
    private static readonly DateTimeOffset End = Start.AddDays(4);

    /// <summary>Ordinary runs share validated prepared five-minute and daily history while exposing only their requested UTC slice.</summary>
    [Theory]
    [InlineData("5m")]
    [InlineData("1d")]
    public async Task OrdinaryUseCache_ReadsContainedPreparedBarsWithoutCollection(string timeframe)
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var source = CreateSource(http, owner);
        var root = Path.Combine(owner.Root, "normalized");
        var selected = await new CachedMarketDataProvider(source, root, "use_cache")
            .SelectPreparedBarsAsync("AMD", timeframe, Start, End, 10, default);
        var before = await File.ReadAllBytesAsync(selected.ManifestPath);

        var bars = await ReadBarsAsync(new CachedMarketDataProvider(source, root, "use_cache"), timeframe,
            Start.AddDays(1), Start.AddDays(2));

        Assert.Equal(Start.AddDays(1).AddHours(14), Assert.Single(bars).Timestamp);
        Assert.Equal(1, handler.BarRequests);
        Assert.Equal(before, await File.ReadAllBytesAsync(selected.ManifestPath));
    }

    /// <summary>Refresh and a different price basis retain their existing collection meaning instead of borrowing incompatible bars.</summary>
    [Theory]
    [InlineData("refresh", "all")]
    [InlineData("use_cache", "raw")]
    public async Task OrdinaryRefreshOrPriceBasisChange_CollectsItsOwnHistory(string cachePolicy, string basis)
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var root = Path.Combine(owner.Root, "normalized");
        await new CachedMarketDataProvider(CreateSource(http, owner), root, "use_cache")
            .SelectPreparedBarsAsync("AMD", "5m", Start, End, 10, default);

        using var replacementHttp = new HttpClient(handler, disposeHandler: false);
        var bars = await ReadBarsAsync(new CachedMarketDataProvider(CreateSource(replacementHttp, owner, basis), root, cachePolicy),
            "5m", Start, End);

        Assert.Equal(4, bars.Count);
        Assert.All(bars, bar => Assert.Equal(basis, bar.AdjustmentPolicy));
        Assert.Equal(2, handler.BarRequests);
    }

    /// <summary>Normal calendar consumers and day warmup share prepared explicit holiday/closed dates and early-close hours.</summary>
    [Fact]
    public async Task CalendarConsumers_ReusePreparedCoverageAndPreviousCompletedSession()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var firstDate = new DateOnly(2026, 11, 30);
        await cache.SelectPreparedCalendarAsync(firstDate.AddDays(-5), firstDate, default);

        var ordinary = await cache.LoadMarketSessionSchedulesAsync(firstDate.AddDays(-3), firstDate, default);
        var context = await cache.LoadSessionContextCalendarAsync(firstDate, default);
        var profile = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        var effective = BacktestSessionContextResolver.IncludePreviousSession(profile,
            DateTimeOffset.Parse("2026-11-30T14:30:00Z"), context, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        Assert.Equal(4, ordinary.Count);
        Assert.False(context[new(2026, 11, 26)].IsTradingDay);
        Assert.False(context[new(2026, 11, 28)].IsTradingDay);
        Assert.Equal(new TimeOnly(13, 0), context[new(2026, 11, 27)].RegularClose);
        Assert.Equal(3, effective.TimeWindow.WarmupLookbackDays);
        Assert.Equal(1, handler.CalendarRequests);
        Assert.Equal(0, handler.BarRequests);
    }

    /// <summary>A cache that starts on the test date cannot establish prior sessions; the fallback collects authoritative dates once.</summary>
    [Fact]
    public async Task MissingPriorCalendarCoverage_CollectsInsteadOfGuessingPreviousWeekday()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var firstDate = new DateOnly(2026, 11, 30);
        await cache.SelectPreparedCalendarAsync(firstDate, firstDate, default);

        var context = await cache.LoadSessionContextCalendarAsync(firstDate, default);
        var reused = await cache.LoadSessionContextCalendarAsync(firstDate, default);

        Assert.True(context[new(2026, 11, 27)].IsTradingDay);
        Assert.False(context[new(2026, 11, 29)].IsTradingDay);
        Assert.Equal(context.Count, reused.Count);
        Assert.Equal(2, handler.CalendarRequests);
    }

    /// <summary>A short cached prefix useful to the old day strategy cannot masquerade as the ORB twenty-session calendar.</summary>
    [Fact]
    public async Task OpeningVolumeContext_ExtendsShortCalendarOnceAndThenReusesTwentySessions()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var firstDate = new DateOnly(2026, 11, 30);
        await cache.SelectPreparedCalendarAsync(firstDate.AddDays(-5), firstDate, default);
        var context = await cache.LoadSessionContextCalendarAsync(firstDate, default, 20);
        var reused = await cache.LoadSessionContextCalendarAsync(firstDate, default, 20);
        Assert.True(context.Values.Count(session => session.IsTradingDay && session.TradeDate < firstDate) >= 20);
        Assert.Equal(context.Count, reused.Count);
        Assert.Equal(2, handler.CalendarRequests);
        Assert.Equal(0, handler.BarRequests);
    }

    /// <summary>Prepared calendar coverage with twenty sessions is reused directly without downloading calendar or candles again.</summary>
    [Fact]
    public async Task OpeningVolumeContext_ReusesSufficientPreparedCalendar()
    {
        using var owner = new OwnedAlpacaTestArchive();
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var cache = new CachedMarketDataProvider(CreateSource(http, owner), Path.Combine(owner.Root, "normalized"), "use_cache");
        var firstDate = new DateOnly(2026, 11, 30);
        await cache.SelectPreparedCalendarAsync(firstDate.AddDays(-45), firstDate, default);
        var context = await cache.LoadSessionContextCalendarAsync(firstDate, default, 20);
        Assert.True(context.Values.Count(session => session.IsTradingDay && session.TradeDate < firstDate) >= 20);
        Assert.Equal(1, handler.CalendarRequests);
        Assert.Equal(0, handler.BarRequests);
    }

    /// <summary>Uses archive-backed production parsing with in-process synthetic HTTP responses only.</summary>
    private static AlpacaMarketDataProvider CreateSource(HttpClient http, OwnedAlpacaTestArchive owner, string basis = "all") =>
        new(http, AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            owner.Writer, adjustmentPolicy: basis);

    /// <summary>Enumerates the normal market-data contract rather than calling the new prepared reader directly.</summary>
    private static async Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CachedMarketDataProvider cache, string timeframe,
        DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var result = new List<OhlcvBar>();
        await foreach (var bar in cache.GetBarsAsync(["AMD"], [timeframe], startUtc, endUtc, default)) result.Add(bar);
        return result;
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int BarRequests { get; private set; }
        public int CalendarRequests { get; private set; }

        /// <summary>Filters fixed synthetic rows to the requested provider bounds and never opens a network connection.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            string payload;
            if (request.RequestUri.AbsolutePath == "/v2/calendar")
            {
                CalendarRequests++;
                var first = DateOnly.Parse(query["start"]);
                var last = DateOnly.Parse(query["end"]);
                var sessions = Enumerable.Range(0, 55).Select(index => new DateOnly(2026, 10, 1).AddDays(index))
                    .Where(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                    .Select(date => new { date = date.ToString("yyyy-MM-dd"), open = "09:30", close = "16:00" })
                    .Concat(new[] {
                    new { date = "2026-11-25", open = "09:30", close = "16:00" },
                    new { date = "2026-11-27", open = "09:30", close = "13:00" },
                    new { date = "2026-11-30", open = "09:30", close = "16:00" }
                });
                payload = JsonSerializer.Serialize(sessions.Where(session => DateOnly.Parse(session.date) >= first && DateOnly.Parse(session.date) <= last));
            }
            else
            {
                Assert.Equal("/v2/stocks/bars", request.RequestUri.AbsolutePath);
                BarRequests++;
                var first = DateTimeOffset.Parse(query["start"]);
                var last = DateTimeOffset.Parse(query["end"]);
                var rows = Enumerable.Range(0, 4).Select(index => new {
                    t = Start.AddDays(index).AddHours(14), o = 100m, h = 101m, l = 99m, c = 100m, v = 1000
                }).Where(bar => bar.t >= first && bar.t <= last);
                payload = JsonSerializer.Serialize(new { bars = new { AMD = rows }, next_page_token = (string?)null });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(payload) });
        }
    }
}
