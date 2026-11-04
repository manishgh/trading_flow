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
/// After a restart the monitor reloads 5-minute bars for every tracked stock, which took 14 minutes for about
/// 900 stocks on 4 Nov 2026. The pass's time limit used to cover that load too, so the news read after it was
/// cancelled and the whole pass saved nothing. Each stage now has its own limit.
/// </summary>
public sealed class EarningsMonitorTimeLimitTests : IDisposable
{
    private readonly string archiveRoot = Path.Combine(Path.GetTempPath(), "trading-flow-earnings-monitor-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_bar_load_longer_than_the_time_limit_still_saves_the_analysis()
    {
        var options = EarningsMonitorOptions.Default with { RequestTimeout = TimeSpan.FromSeconds(2) };
        var today = EarningsCalendarDates.ExchangeDate(DateTimeOffset.UtcNow);
        var repository = new EarningsRepository(new EarningsCalendarEvent
        {
            Id = "finviz:slow", Ticker = "SLOW", CompanyName = "Slow Load Inc", ReportDateExchange = today,
            ScheduledAtUtc = DateTimeOffset.UtcNow.AddHours(1), ReleaseWindow = EarningsReleaseWindow.AfterMarketClose,
            Provider = "finviz", SourceUrl = "https://example.invalid/calendar", SourceArtifactSha256 = new('a', 64),
            ProviderReceivedAtUtc = DateTimeOffset.UtcNow, FirstSeenAtUtc = DateTimeOffset.UtcNow, LastSeenAtUtc = DateTimeOffset.UtcNow
        });
        var finviz = new FinvizClient(new HttpClient(new EmptyFinvizHandler()),
            new FinvizOptions(new Uri("https://elite.finviz.com"), "test-token"),
            new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot)));
        var provider = new SlowMarketDataProvider(TimeSpan.FromSeconds(4));
        var monitor = new EarningsMonitor(
            finviz,
            new FinvizSectorDirectory(finviz, TimeProvider.System),
            repository,
            new CancellationHonouringNewsRepository(),
            new EarningsMarketStateLoader(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, options),
            new EarningsAnalyzer(new IndicatorEngine(new MarketEvidenceProfile("earnings_monitor_test_calendar_v1",
                MarketEvidenceProfile.DefaultLookbackSessions, MarketEvidenceProfile.DefaultMinimumValidSamples,
                "America/New_York", allowStandardWeekdayFallback: true)), options),
            options,
            TimeProvider.System,
            NullLogger<EarningsMonitor>.Instance);

        await monitor.RefreshAsync(forceCalendarRefresh: false, CancellationToken.None);

        Assert.True(provider.LoadTook > options.RequestTimeout);
        Assert.Equal("finviz:slow", Assert.Single(repository.Saved).EarningsEventId);
        Assert.NotNull(monitor.GetStatus().LastAnalysisUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, recursive: true);
    }

    private sealed class EarningsRepository(EarningsCalendarEvent calendarEvent) : IEarningsRepository
    {
        public List<EarningsAnalysisSnapshot> Saved { get; } = [];

        public Task ReplaceProviderWindowAsync(string provider, DateOnly fromExchangeDate, DateOnly toExchangeDate,
            IReadOnlyCollection<EarningsCalendarEvent> events, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryApplyNewsResultAsync(string ticker, DateTimeOffset observedAtUtc, EarningsNewsResult result,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EarningsCalendarEvent>> GetCalendarAsync(DateOnly fromExchangeDate, DateOnly toExchangeDate,
            IReadOnlyCollection<string>? tickers, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EarningsCalendarEvent>>([calendarEvent]);

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
    private sealed class CancellationHonouringNewsRepository : INewsFeedRepository
    {
        public Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentAsync(DateTimeOffset windowStart, int limit, string? ticker,
            CancellationToken cancellationToken) => Read(cancellationToken);

        public Task<IReadOnlyList<PersistedNewsItem>> GetRecentForTickersAsync(DateTimeOffset windowStart, int limit,
            IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Read(cancellationToken);

        public Task<IReadOnlyList<PersistedNewsItem>> GetIngestedSinceForTickersAsync(DateTimeOffset ingestedSinceUtc, int limit,
            IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Read(cancellationToken);

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.CompletedTask;

        private static Task<IReadOnlyList<PersistedNewsItem>> Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PersistedNewsItem>>([]);
        }
    }

    private sealed class SlowMarketDataProvider(TimeSpan delay) : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public TimeSpan LoadTook { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> intervals,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var started = DateTimeOffset.UtcNow;
            await Task.Delay(delay, cancellationToken);
            LoadTook = DateTimeOffset.UtcNow - started;
            yield break;
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
