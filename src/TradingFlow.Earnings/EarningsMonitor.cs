using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.News;
using TradingFlow.Finviz;

namespace TradingFlow.Earnings;

/// <summary>
/// Maintains the complete upcoming earnings calendar and continuously refreshes advisory
/// post-release analysis. Provider, repository, and indicator work remain outside UI projects.
/// </summary>
public sealed class EarningsMonitor
{
    private readonly FinvizClient finviz;
    private readonly IEarningsRepository earnings;
    private readonly INewsFeedRepository news;
    private readonly EarningsMarketStateLoader marketState;
    private readonly EarningsAnalyzer analyzer;
    private readonly EarningsMonitorOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<EarningsMonitor> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private DateTimeOffset? lastCalendarRefreshUtc;
    private DateTimeOffset? lastAnalysisUtc;
    private DateTimeOffset? lastPriorResultRefreshUtc;
    private int trackedEventCount;
    private string? lastError;
    private volatile bool isRunning;

    public EarningsMonitor(
        FinvizClient finviz,
        IEarningsRepository earnings,
        INewsFeedRepository news,
        EarningsMarketStateLoader marketState,
        EarningsAnalyzer analyzer,
        EarningsMonitorOptions options,
        TimeProvider clock,
        ILogger<EarningsMonitor> logger)
    {
        this.finviz = finviz;
        this.earnings = earnings;
        this.news = news;
        this.marketState = marketState;
        this.analyzer = analyzer;
        this.options = options;
        this.clock = clock;
        this.logger = logger;
    }

    public EarningsMonitorStatus GetStatus() => new(
        isRunning,
        options.AnalysisInterval,
        lastCalendarRefreshUtc,
        lastAnalysisUtc,
        trackedEventCount,
        lastError);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        isRunning = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(forceCalendarRefresh: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                    logger.LogError(exception, "Earnings monitor iteration failed.");
                }

                await Task.Delay(options.AnalysisInterval, clock, cancellationToken);
            }
        }
        finally
        {
            isRunning = false;
        }
    }

    public async Task RefreshAsync(bool forceCalendarRefresh, CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            var nowUtc = clock.GetUtcNow();
            var exchangeToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(nowUtc, EarningsTimeZones.NewYork).DateTime);
            var from = exchangeToday;
            var to = EarningsCalendarDates.NextWeekday(exchangeToday);

            if (forceCalendarRefresh ||
                lastCalendarRefreshUtc is null ||
                nowUtc - lastCalendarRefreshUtc >= options.CalendarRefreshInterval)
            {
                var providerEvents = await finviz.GetEarningsCalendarAsync(from, to, timeout.Token);
                await earnings.ReplaceProviderWindowAsync("finviz", from, to, providerEvents, timeout.Token);
                lastCalendarRefreshUtc = nowUtc;
                logger.LogInformation(
                    "Refreshed {EventCount} Finviz earnings events for {FromDate} through {ToDate}.",
                    providerEvents.Count,
                    from,
                    to);
            }

            var trackedEvents = await earnings.GetCalendarAsync(from, to, tickers: null, timeout.Token);
            trackedEventCount = trackedEvents.Count;
            if (trackedEvents.Count == 0)
            {
                lastAnalysisUtc = nowUtc;
                lastError = null;
                return;
            }

            await TryRefreshPriorResultsAsync(trackedEvents, nowUtc, cancellationToken);

            var symbols = trackedEvents
                .Select(item => item.Ticker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var barsByTicker = await marketState.LoadAsync(symbols, nowUtc, timeout.Token);

            var recentNews = await news.GetRecentForTickersAsync(
                nowUtc.AddDays(-3),
                5000,
                symbols,
                timeout.Token);
            var latest = await earnings.GetLatestAnalysesAsync(
                trackedEvents.Select(item => item.Id).ToArray(),
                timeout.Token);
            using var concurrency = new SemaphoreSlim(options.MaximumParallelAnalyses);
            var analyses = await Task.WhenAll(trackedEvents.Select(async calendarEvent =>
            {
                await concurrency.WaitAsync(timeout.Token);
                try
                {
                    return analyzer.Analyze(
                        calendarEvent,
                        barsByTicker.GetValueOrDefault(calendarEvent.Ticker) ?? Array.Empty<TradingFlow.Domain.Market.OhlcvBar>(),
                        recentNews.Where(item => item.Ticker.Equals(calendarEvent.Ticker, StringComparison.OrdinalIgnoreCase)).ToArray(),
                        nowUtc);
                }
                finally
                {
                    concurrency.Release();
                }
            }));

            foreach (var snapshot in analyses)
            {
                if (!latest.TryGetValue(snapshot.EarningsEventId, out var previous) ||
                    ShouldPersist(previous, snapshot, options.AnalysisSnapshotHeartbeat))
                {
                    await earnings.UpsertAnalysisAsync(snapshot, timeout.Token);
                }
            }

            lastAnalysisUtc = nowUtc;
            lastError = null;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task<EarningsCalendarSnapshot> GetCalendarAsync(
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        CancellationToken cancellationToken)
    {
        var events = await earnings.GetCalendarAsync(
            fromExchangeDate,
            toExchangeDate,
            tickers: null,
            cancellationToken);
        var analyses = await earnings.GetLatestAnalysesAsync(
            events.Select(item => item.Id).ToArray(),
            cancellationToken);
        var priorCandidates = events.Count == 0
            ? Array.Empty<EarningsCalendarEvent>()
            : await earnings.GetCalendarAsync(
                fromExchangeDate.AddDays(-options.PriorResultMaximumDaysAgo - 30),
                toExchangeDate.AddDays(-1),
                events.Select(item => item.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                cancellationToken);
        return new EarningsCalendarSnapshot(
            clock.GetUtcNow(),
            fromExchangeDate,
            toExchangeDate,
            events.Select(item => new EarningsCalendarSnapshotItem(
                item,
                analyses.GetValueOrDefault(item.Id),
                priorCandidates
                    .Where(candidate =>
                        candidate.Ticker.Equals(item.Ticker, StringComparison.OrdinalIgnoreCase) &&
                        candidate.ReportDateExchange < item.ReportDateExchange &&
                        HasReportedResult(candidate))
                    .OrderByDescending(candidate => candidate.ReportDateExchange)
                    .ThenByDescending(candidate => candidate.ScheduledAtUtc)
                    .FirstOrDefault())).ToArray());
    }

    private async Task TryRefreshPriorResultsAsync(
        IReadOnlyList<EarningsCalendarEvent> trackedEvents,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (lastPriorResultRefreshUtc is { } lastRefresh &&
            nowUtc - lastRefresh < options.PriorResultRefreshInterval)
        {
            return;
        }

        // Quarterly reports normally land near thirteen weeks apart. This bounded provider
        // window covers that date drift while avoiding an unbounded historical calendar scan.
        var from = trackedEvents.Min(item => item.ReportDateExchange)
            .AddDays(-options.PriorResultMaximumDaysAgo);
        var to = trackedEvents.Max(item => item.ReportDateExchange)
            .AddDays(-options.PriorResultMinimumDaysAgo);
        if (to < from)
        {
            return;
        }

        lastPriorResultRefreshUtc = nowUtc;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.PriorResultRequestTimeout);
            var providerEvents = await finviz.GetEarningsCalendarAsync(from, to, timeout.Token);
            await earnings.ReplaceProviderWindowAsync("finviz", from, to, providerEvents, timeout.Token);
            logger.LogInformation(
                "Refreshed {EventCount} historical Finviz earnings events for prior-result comparison from {FromDate} through {ToDate}.",
                providerEvents.Count,
                from,
                to);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Historical earnings refresh timed out after {Timeout}; current earnings monitoring will continue.",
                options.PriorResultRequestTimeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Historical earnings refresh failed; current earnings monitoring will continue.");
        }
    }

    private static bool HasReportedResult(EarningsCalendarEvent calendarEvent) =>
        calendarEvent.EpsActual.HasValue ||
        calendarEvent.ReportedEpsActual.HasValue ||
        calendarEvent.EpsSurprisePercent.HasValue ||
        calendarEvent.ReportedEpsSurprisePercent.HasValue ||
        calendarEvent.RevenueActualMillions.HasValue ||
        calendarEvent.RevenueSurprisePercent.HasValue;

    private static bool ShouldPersist(
        EarningsAnalysisSnapshot previous,
        EarningsAnalysisSnapshot current,
        TimeSpan heartbeat)
    {
        return previous.ResultAssessment != current.ResultAssessment ||
            previous.BreakoutAssessment != current.BreakoutAssessment ||
            previous.LatestCompletedBarAtUtc != current.LatestCompletedBarAtUtc ||
            previous.ResultNewsPublishedAtUtc != current.ResultNewsPublishedAtUtc ||
            current.AnalyzedAtUtc - previous.AnalyzedAtUtc >= heartbeat;
    }
}
