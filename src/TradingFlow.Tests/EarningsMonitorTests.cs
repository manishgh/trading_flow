using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Earnings;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

/// <summary>
/// Monitor pass and calendar behaviour: per-stage time limits, parallel analyses, and the previous reported
/// quarter shown next to each event.
/// </summary>
public sealed class EarningsMonitorTests : IDisposable
{
    private readonly string archiveRoot = Path.Combine(Path.GetTempPath(), "trading-flow-earnings-monitor-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// After a restart the monitor reloads 5-minute bars for every tracked stock, which took 14 minutes for about
    /// 900 stocks on 4 Nov 2026. The pass's time limit used to cover that load too, so the news read after it was
    /// cancelled and the whole pass saved nothing. Each stage now has its own limit.
    /// </summary>
    [Fact]
    public async Task A_bar_load_longer_than_the_time_limit_still_saves_the_analysis()
    {
        var options = EarningsMonitorOptions.Default with { RequestTimeout = TimeSpan.FromSeconds(2) };
        var now = DateTimeOffset.UtcNow;
        var repository = new EarningsRepository(Event("finviz:slow", "SLOW", EarningsCalendarDates.ExchangeDate(now), now.AddHours(1), now));
        var provider = new SlowMarketDataProvider(TimeSpan.FromSeconds(4));
        var monitor = CreateMonitor(repository, new CancellationHonouringNewsRepository(), provider, options,
            new EarningsAnalyzer(new IndicatorEngine(new MarketEvidenceProfile("earnings_monitor_test_calendar_v1",
                MarketEvidenceProfile.DefaultLookbackSessions, MarketEvidenceProfile.DefaultMinimumValidSamples,
                "America/New_York", allowStandardWeekdayFallback: true)), options));

        await monitor.RefreshAsync(forceCalendarRefresh: false, CancellationToken.None);

        Assert.True(provider.LoadTook > options.RequestTimeout);
        Assert.Equal("finviz:slow", Assert.Single(repository.Saved).EarningsEventId);
        Assert.NotNull(monitor.GetStatus().LastAnalysisUtc);
    }

    /// <summary>
    /// Analyses now run several at a time. Each released event must still be analysed with its own stock's
    /// bars and its own news (stored under a lower-case ticker), whatever order the parallel workers finish in.
    /// </summary>
    [Fact]
    public async Task Parallel_analyses_use_each_events_own_bars_and_news()
    {
        var options = EarningsMonitorOptions.Default with { MaximumParallelAnalyses = 4 };
        var now = DateTimeOffset.UtcNow;
        var today = EarningsCalendarDates.ExchangeDate(now);
        var lastBar = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero).AddMinutes(-10);
        var events = Enumerable.Range(0, 20)
            .Select(index => Event($"finviz:{index}", $"T{index:00}", today, now.AddHours(-1), now.AddHours(-1), EarningsReleaseWindow.DuringMarket))
            .ToArray();
        // 48 hours of bars at a price unique to each stock, so pre- and post-release bars exist at any time of day.
        static decimal Price(string ticker) => 10m + Int32.Parse(ticker[1..]);
        IEnumerable<OhlcvBar> Bars(string ticker) => Enumerable.Range(0, 576)
            .Select(step => new OhlcvBar(ticker, lastBar.AddMinutes(-5 * step), "5m", Price(ticker), Price(ticker), Price(ticker), Price(ticker), 1000m));
        var news = events.Select(item => new PersistedNewsItem
        {
            Id = $"news:{item.Ticker}", Ticker = item.Ticker.ToLowerInvariant(), Timestamp = now.AddMinutes(-30),
            Headline = $"{item.Ticker} quarterly results released", Provider = "test"
        }).ToArray();
        var repository = new EarningsRepository(events);
        var monitor = CreateMonitor(repository, new CancellationHonouringNewsRepository(news),
            new SlowMarketDataProvider(TimeSpan.Zero, Bars), options, new EarningsAnalyzer(new IndicatorEngine(), options));

        await monitor.RefreshAsync(forceCalendarRefresh: false, CancellationToken.None);

        Assert.Equal(events.Length, repository.Saved.Count);
        foreach (var calendarEvent in events)
        {
            var snapshot = Assert.Single(repository.Saved, item => item.EarningsEventId == calendarEvent.Id);
            Assert.Equal(calendarEvent.Ticker, snapshot.Ticker);
            Assert.Equal($"{calendarEvent.Ticker} quarterly results released", snapshot.NewsHeadline);
            Assert.Equal(Price(calendarEvent.Ticker), snapshot.LatestClose);
            Assert.Equal(Price(calendarEvent.Ticker), snapshot.Ema10);
        }
    }

    /// <summary>
    /// Each calendar row shows the stock's latest earlier report that carries a result. Reports without any
    /// result, reports on or after the row's date, and other stocks are skipped; on the same report date the
    /// later scheduled time wins, and tickers match regardless of case.
    /// </summary>
    [Fact]
    public async Task Calendar_shows_each_events_latest_earlier_report_with_a_result()
    {
        var now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        var reportDate = new DateOnly(2026, 11, 4);
        DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc);
        EarningsCalendarEvent Reported(string id, string ticker, DateOnly date, string scheduledUtc)
        {
            var item = Event(id, ticker, date, At(scheduledUtc), now);
            item.EpsActual = 1m;
            return item;
        }

        var current = Event("now:abc", "ABC", reportDate, now, now);
        var noPrior = Event("now:xyz", "XYZ", reportDate, now, now);
        var expected = Reported("q2:abc:late", "abc", new DateOnly(2026, 8, 5), "2026-08-05T20:30:00Z");
        var repository = new EarningsRepository(
            current,
            noPrior,
            Reported("q2:abc:early", "ABC", new DateOnly(2026, 8, 5), "2026-08-05T12:00:00Z"),
            expected,
            Event("q2:abc:no-result", "ABC", new DateOnly(2026, 8, 6), At("2026-08-06T12:00:00Z"), now),
            Reported("q1:abc", "ABC", new DateOnly(2026, 5, 6), "2026-05-06T12:00:00Z"),
            Reported("same-day:abc", "ABC", reportDate, "2026-11-04T08:00:00Z"),
            Reported("q2:other", "OTHER", new DateOnly(2026, 8, 7), "2026-08-07T12:00:00Z"));
        var options = EarningsMonitorOptions.Default;
        var monitor = CreateMonitor(repository, new CancellationHonouringNewsRepository(),
            new SlowMarketDataProvider(TimeSpan.Zero), options, new EarningsAnalyzer(new IndicatorEngine(), options));

        var snapshot = await monitor.GetCalendarAsync(reportDate, reportDate, CancellationToken.None);

        Assert.Equal(["now:abc", "now:xyz", "same-day:abc"], snapshot.Items.Select(item => item.Event.Id));
        Assert.Equal("q2:abc:late", snapshot.Items.Single(item => item.Event.Id == "now:abc").PreviousReportedEvent?.Id);
        Assert.Null(snapshot.Items.Single(item => item.Event.Id == "now:xyz").PreviousReportedEvent);
        Assert.Equal("q2:abc:late", snapshot.Items.Single(item => item.Event.Id == "same-day:abc").PreviousReportedEvent?.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
    }

    /// <summary>Builds a monitor over the given fakes with a Finviz client whose calendar is always empty.</summary>
    private EarningsMonitor CreateMonitor(
        EarningsRepository repository,
        INewsFeedRepository news,
        SlowMarketDataProvider provider,
        EarningsMonitorOptions options,
        EarningsAnalyzer analyzer)
    {
        var finviz = new FinvizClient(new HttpClient(new EmptyFinvizHandler()),
            new FinvizOptions(new Uri("https://elite.finviz.com"), "test-token"),
            new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot)));
        return new EarningsMonitor(
            finviz,
            new FinvizSectorDirectory(finviz, TimeProvider.System),
            repository,
            news,
            new EarningsMarketStateLoader(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, options),
            analyzer,
            options,
            TimeProvider.System,
            NullLogger<EarningsMonitor>.Instance);
    }

    /// <summary>A Finviz calendar event with no reported result.</summary>
    private static EarningsCalendarEvent Event(
        string id,
        string ticker,
        DateOnly reportDate,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset receivedAtUtc,
        EarningsReleaseWindow releaseWindow = EarningsReleaseWindow.AfterMarketClose) => new()
    {
        Id = id, Ticker = ticker, CompanyName = $"{ticker} Inc", ReportDateExchange = reportDate,
        ScheduledAtUtc = scheduledAtUtc, ReleaseWindow = releaseWindow,
        Provider = "finviz", SourceUrl = "https://example.invalid/calendar", SourceArtifactSha256 = new('a', 64),
        ProviderReceivedAtUtc = receivedAtUtc, FirstSeenAtUtc = receivedAtUtc.AddDays(-2), LastSeenAtUtc = receivedAtUtc
    };

    /// <summary>In-memory earnings store: calendar reads honour the date range and ticker filter like SQLite.</summary>
    private sealed class EarningsRepository(params EarningsCalendarEvent[] calendarEvents) : IEarningsRepository
    {
        public List<EarningsAnalysisSnapshot> Saved { get; } = [];

        public Task ReplaceProviderWindowAsync(string provider, DateOnly fromExchangeDate, DateOnly toExchangeDate,
            IReadOnlyCollection<EarningsCalendarEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryApplyNewsResultAsync(string ticker, DateTimeOffset observedAtUtc, EarningsNewsResult result,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EarningsCalendarEvent>> GetCalendarAsync(DateOnly fromExchangeDate, DateOnly toExchangeDate,
            IReadOnlyCollection<string>? tickers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EarningsCalendarEvent>>(calendarEvents
                .Where(item => item.ReportDateExchange >= fromExchangeDate && item.ReportDateExchange <= toExchangeDate)
                .Where(item => tickers is null || tickers.Contains(item.Ticker, StringComparer.OrdinalIgnoreCase))
                .ToArray());

        public Task UpsertAnalysisAsync(EarningsAnalysisSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, EarningsAnalysisSnapshot>> GetLatestAnalysesAsync(
            IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyDictionary<string, EarningsAnalysisSnapshot>>(new Dictionary<string, EarningsAnalysisSnapshot>());
        }
    }

    /// <summary>Like the SQLite repository, a read with an expired time limit throws instead of returning.</summary>
    private sealed class CancellationHonouringNewsRepository(IReadOnlyList<PersistedNewsItem>? items = null) : INewsFeedRepository
    {
        public Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentAsync(DateTimeOffset windowStart, int limit, string? ticker,
            CancellationToken cancellationToken) => Read(cancellationToken);

        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentForTickersAsync(DateTimeOffset windowStart, int limit,
            IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Read(cancellationToken);

        public Task<IReadOnlyList<PersistedNewsItem>> GetIngestedSinceForTickersAsync(DateTimeOffset ingestedSinceUtc, int limit,
            IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Read(cancellationToken);

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.CompletedTask;

        private Task<IReadOnlyList<PersistedNewsItem>> Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(items ?? []);
        }
    }

    /// <summary>Waits before answering; returns the bars <paramref name="barsFor"/> builds for each requested stock, or none.</summary>
    private sealed class SlowMarketDataProvider(TimeSpan delay, Func<string, IEnumerable<OhlcvBar>>? barsFor = null)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public TimeSpan LoadTook { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> intervals,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            await Task.Delay(delay, cancellationToken);
            LoadTook = DateTimeOffset.UtcNow - started;
            foreach (var bar in tickers.SelectMany(ticker => barsFor?.Invoke(ticker) ?? []))
            {
                if (bar.Timestamp >= start && bar.Timestamp <= end)
                {
                    yield return bar;
                }
            }
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
            for (var date = startDateInclusive; date <= endDateInclusive; date = date.AddDays(1))
                schedules[date] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? MarketSessionSchedule.Closed(date)
                    : MarketSessionSchedule.RegularDay(date);
            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(schedules);
        }
    }

    /// <summary>An empty earnings calendar page; every other Finviz request fails, as an unreachable export would.</summary>
    private sealed class EmptyFinvizHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/api/calendar/earnings", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[],"page":1,"pageSize":50,"totalItemsCount":0,"totalPages":1}""", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
