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
    private readonly FinvizSectorDirectory sectors;
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
        FinvizSectorDirectory sectors,
        IEarningsRepository earnings,
        INewsFeedRepository news,
        EarningsMarketStateLoader marketState,
        EarningsAnalyzer analyzer,
        EarningsMonitorOptions options,
        TimeProvider clock,
        ILogger<EarningsMonitor> logger)
    {
        this.finviz = finviz;
        this.sectors = sectors;
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

    /// <summary>
    /// One monitor pass: refreshes the Finviz calendar when due, loads 5-minute bars for every tracked
    /// stock, runs the shared analyzer on completed bars and news, and saves analyses that changed.
    /// Logs each stage's duration so a slow pass shows whether bars, news, analysis or saving cost the time.
    /// </summary>
    public async Task RefreshAsync(bool forceCalendarRefresh, CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            var nowUtc = clock.GetUtcNow();
            var exchangeToday = EarningsCalendarDates.ExchangeDate(nowUtc);
            // Analysed reports run from the previous weekday, so last session's after-close reports stay analysed
            // through today's session, to the next weekday.
            var firstReportDate = EarningsCalendarDates.PreviousWeekday(exchangeToday);
            var lastReportDate = EarningsCalendarDates.NextWeekday(exchangeToday);
            analyzer.UpdateMarketSessionSchedules(await LoadSessionSchedulesAsync(exchangeToday, lastReportDate, timeout.Token));
            await RefreshCalendarIfDueAsync(forceCalendarRefresh, firstReportDate, lastReportDate, nowUtc, timeout.Token);

            var trackedEvents = await GetTrackedEventsAsync(firstReportDate, lastReportDate, exchangeToday, timeout.Token);
            trackedEventCount = trackedEvents.Count;
            if (trackedEvents.Count == 0)
            {
                await marketState.ReleaseUntrackedAsync([], cancellationToken);
            }
            else
            {
                await TryRefreshPriorResultsAsync(trackedEvents, nowUtc, cancellationToken);
                await AnalyzeAndSaveAsync(trackedEvents, nowUtc, cancellationToken);
            }

            lastAnalysisUtc = nowUtc;
            lastError = null;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Loads the exchange calendar from the oldest day an analysis can look back to (the bar lookback plus
    /// ten days of slack) through the day after the last analysed day.
    /// </summary>
    private Task<IReadOnlyDictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule>> LoadSessionSchedulesAsync(
        DateOnly firstAnalysedDate,
        DateOnly lastAnalysedDate,
        CancellationToken cancellationToken) =>
        marketState.LoadMarketSessionSchedulesAsync(
            firstAnalysedDate.AddDays(-options.SubDailyLookbackDays - 10),
            lastAnalysedDate.AddDays(1),
            cancellationToken);

    /// <summary>
    /// Replaces the stored Finviz calendar when forced or when the refresh interval has passed, then refreshes
    /// the display-only sector labels. The window starts <see cref="EarningsMonitorOptions.RecentResultLookbackDays"/>
    /// before the first analysed report date: the provider posts after-close actuals well after the report date.
    /// </summary>
    private async Task RefreshCalendarIfDueAsync(
        bool force,
        DateOnly firstReportDate,
        DateOnly lastReportDate,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!force && lastCalendarRefreshUtc is { } lastRefresh && nowUtc - lastRefresh < options.CalendarRefreshInterval)
        {
            return;
        }

        var from = firstReportDate.AddDays(-options.RecentResultLookbackDays);
        var to = lastReportDate;
        var eventCount = await ReplaceFinvizWindowAsync(from, to, cancellationToken);
        lastCalendarRefreshUtc = nowUtc;
        try
        {
            await sectors.RefreshIfStaleAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sector labels are display-only; the calendar and analysis continue without them.
            logger.LogWarning(exception, "Finviz sector directory refresh failed; previous sectors stay in use.");
        }

        logger.LogInformation(
            "Refreshed {EventCount} Finviz earnings events for {FromDate} through {ToDate}.",
            eventCount,
            from,
            to);
    }

    /// <summary>Downloads the Finviz earnings calendar for the dates and replaces the stored window; returns the event count.</summary>
    private async Task<int> ReplaceFinvizWindowAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var providerEvents = await finviz.GetEarningsCalendarAsync(from, to, cancellationToken);
        await earnings.ReplaceProviderWindowAsync("finviz", from, to, providerEvents, cancellationToken);
        return providerEvents.Count;
    }

    /// <summary>
    /// Returns the events to analyse: stored reports in the analysed dates that are still ahead or whose trading
    /// session is today, so last session's after-close reports stay analysed today.
    /// </summary>
    private async Task<List<EarningsCalendarEvent>> GetTrackedEventsAsync(
        DateOnly firstReportDate,
        DateOnly lastReportDate,
        DateOnly exchangeToday,
        CancellationToken cancellationToken) =>
        (await earnings.GetCalendarAsync(
            firstReportDate,
            lastReportDate,
            tickers: null,
            cancellationToken))
        .Where(item => item.ReportDateExchange >= exchangeToday ||
            EarningsCalendarDates.IsTradingSession(item.ReportDateExchange, item.ReleaseWindow, exchangeToday))
        .ToList();

    /// <summary>
    /// Loads bars for every tracked stock, releases stocks no longer tracked from memory, reads news and the
    /// latest saved analyses, runs the analyzer and saves analyses that changed. Logs each stage's duration.
    /// The bar load has no time limit; one fresh limit then covers the news read, the analyses and the saves
    /// together. Loading bars after a restart can take longer than the limit on its own (14 minutes for about
    /// 900 stocks on 4 Nov 2026), and sharing one limit with it cancelled the news read so the pass saved nothing.
    /// </summary>
    private async Task AnalyzeAndSaveAsync(
        IReadOnlyList<EarningsCalendarEvent> trackedEvents,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var symbols = trackedEvents
            .Select(item => item.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stage = System.Diagnostics.Stopwatch.StartNew();
        var barsByTicker = await marketState.LoadAsync(symbols, nowUtc, cancellationToken);
        // Stocks whose earnings window has ended leave memory; their bar files stay on disk.
        var releasedCount = await marketState.ReleaseUntrackedAsync(symbols, cancellationToken);
        var loadTime = stage.Elapsed;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        stage.Restart();
        var recentNews = await news.GetRecentForTickersAsync(nowUtc.AddDays(-3), 5000, symbols, timeout.Token);
        var latest = await earnings.GetLatestAnalysesAsync(trackedEvents.Select(item => item.Id).ToArray(), timeout.Token);
        var newsTime = stage.Elapsed;
        stage.Restart();
        var analyses = AnalyzeAll(trackedEvents, barsByTicker, recentNews, nowUtc, timeout.Token);
        var analysisTime = stage.Elapsed;
        stage.Restart();
        var savedCount = await SaveChangedAnalysesAsync(analyses, latest, timeout.Token);

        logger.LogInformation(
            "Earnings pass for {EventCount} events and {TickerCount} stocks: bar load {LoadMs} ms (released {ReleasedCount} finished stocks from memory), news read {NewsMs} ms, " +
            "analysis {AnalysisMs} ms, saved {SavedCount} changed analyses in {SaveMs} ms.",
            trackedEvents.Count,
            symbols.Length,
            (long)loadTime.TotalMilliseconds,
            releasedCount,
            (long)newsTime.TotalMilliseconds,
            (long)analysisTime.TotalMilliseconds,
            savedCount,
            (long)stage.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Saves each analysis that is new, differs from the latest saved one in anything shown, or is older than
    /// the heartbeat; returns how many were saved.
    /// </summary>
    private async Task<int> SaveChangedAnalysesAsync(
        IReadOnlyList<EarningsAnalysisSnapshot> analyses,
        IReadOnlyDictionary<string, EarningsAnalysisSnapshot> latest,
        CancellationToken cancellationToken)
    {
        var savedCount = 0;
        foreach (var snapshot in analyses)
        {
            if (!latest.TryGetValue(snapshot.EarningsEventId, out var previous) ||
                ShouldPersist(previous, snapshot, options.AnalysisSnapshotHeartbeat))
            {
                await earnings.UpsertAnalysisAsync(snapshot, cancellationToken);
                savedCount++;
            }
        }

        return savedCount;
    }

    /// <summary>
    /// Re-runs the shared analyzer for one stored event at fixed steps. Each step sees only completed
    /// bars and news available at that time, so it shows what the live monitor would have reported.
    /// A separate analyzer keeps the live monitor's session calendar untouched.
    /// </summary>
    public async Task<IReadOnlyList<EarningsAnalysisSnapshot>> ReplayAsync(
        string ticker,
        DateOnly reportDateExchange,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan step,
        CancellationToken cancellationToken)
    {
        var calendarEvent = (await earnings.GetCalendarAsync(reportDateExchange, reportDateExchange, [ticker], cancellationToken))
            .FirstOrDefault() ?? throw new KeyNotFoundException($"No stored {ticker} earnings event on {reportDateExchange:yyyy-MM-dd}.");
        var replay = new EarningsAnalyzer(new TradingFlow.Engine.Indicators.IndicatorEngine(), options);
        replay.UpdateMarketSessionSchedules(await LoadSessionSchedulesAsync(
            EarningsCalendarDates.ExchangeDate(fromUtc),
            EarningsCalendarDates.ExchangeDate(toUtc),
            cancellationToken));
        var bars = (await marketState.LoadAsync([calendarEvent.Ticker], toUtc, cancellationToken))
            .GetValueOrDefault(calendarEvent.Ticker) ?? [];
        var tickerNews = await news.GetRecentForTickersAsync(fromUtc.AddDays(-3), 5000, [calendarEvent.Ticker], cancellationToken);
        var snapshots = new List<EarningsAnalysisSnapshot>();
        for (var at = fromUtc; at <= toUtc; at += step)
        {
            snapshots.Add(replay.Analyze(calendarEvent, bars, tickerNews, at));
        }

        return snapshots;
    }

    /// <summary>
    /// Returns the stored calendar for the dates with each event's latest analysis and the stock's latest
    /// earlier report that carries a result, shown as the previous quarter on the Earnings page and the Desk.
    /// </summary>
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
        // Newest first per stock, so each event takes the first one dated before it. This used to filter and
        // sort the whole candidate list again for every event on every page load.
        var reportedByTicker = priorCandidates
            .Where(HasReportedResult)
            .OrderByDescending(candidate => candidate.ReportDateExchange)
            .ThenByDescending(candidate => candidate.ScheduledAtUtc)
            .ToLookup(candidate => candidate.Ticker, StringComparer.OrdinalIgnoreCase);
        return new EarningsCalendarSnapshot(
            clock.GetUtcNow(),
            fromExchangeDate,
            toExchangeDate,
            events.Select(item => new EarningsCalendarSnapshotItem(
                item,
                analyses.GetValueOrDefault(item.Id),
                reportedByTicker[item.Ticker]
                    .FirstOrDefault(candidate => candidate.ReportDateExchange < item.ReportDateExchange))).ToArray());
    }

    /// <summary>
    /// Runs the shared analyzer for every tracked event, up to <see cref="EarningsMonitorOptions.MaximumParallelAnalyses"/>
    /// at once, and returns the snapshots in event order. Analyze never waits, so the earlier semaphore and
    /// Task.WhenAll loop ran every event one after another on one thread: 137 s for 1,023 events on
    /// 4 Nov 2026. The analyzer, indicator engine and session profile hold no state that analyses change.
    /// </summary>
    private EarningsAnalysisSnapshot[] AnalyzeAll(
        IReadOnlyList<EarningsCalendarEvent> events,
        IReadOnlyDictionary<string, IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>> barsByTicker,
        IReadOnlyList<PersistedNewsItem> recentNews,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var newsByTicker = recentNews.ToLookup(item => item.Ticker, StringComparer.OrdinalIgnoreCase);
        var analyses = new EarningsAnalysisSnapshot[events.Count];
        Parallel.For(
            0,
            events.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumParallelAnalyses,
                CancellationToken = cancellationToken
            },
            index => analyses[index] = analyzer.Analyze(
                events[index],
                barsByTicker.GetValueOrDefault(events[index].Ticker) ?? [],
                newsByTicker[events[index].Ticker].ToArray(),
                nowUtc));
        return analyses;
    }

    /// <summary>
    /// At most once per <see cref="EarningsMonitorOptions.PriorResultRefreshInterval"/>, downloads the Finviz calendar
    /// around the previous quarter of the tracked reports so each card can compare with it. A failure or time-out
    /// is logged and the pass continues.
    /// </summary>
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
            var eventCount = await ReplaceFinvizWindowAsync(from, to, timeout.Token);
            logger.LogInformation(
                "Refreshed {EventCount} historical Finviz earnings events for prior-result comparison from {FromDate} through {ToDate}.",
                eventCount,
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

    /// <summary>True when the event carries any reported EPS or revenue actual or surprise.</summary>
    private static bool HasReportedResult(EarningsCalendarEvent calendarEvent) =>
        calendarEvent.EpsActual.HasValue ||
        calendarEvent.ReportedEpsActual.HasValue ||
        calendarEvent.EpsSurprisePercent.HasValue ||
        calendarEvent.ReportedEpsSurprisePercent.HasValue ||
        calendarEvent.RevenueActualMillions.HasValue ||
        calendarEvent.RevenueSurprisePercent.HasValue;

    /// <summary>True when the new analysis differs from the saved one in anything shown, or the saved one is older than the heartbeat.</summary>
    private static bool ShouldPersist(
        EarningsAnalysisSnapshot previous,
        EarningsAnalysisSnapshot current,
        TimeSpan heartbeat)
    {
        return previous.ResultAssessment != current.ResultAssessment ||
            previous.BreakoutAssessment != current.BreakoutAssessment ||
            previous.ResultDataSource != current.ResultDataSource ||
            previous.EffectiveEpsEstimate != current.EffectiveEpsEstimate ||
            previous.EffectiveEpsActual != current.EffectiveEpsActual ||
            previous.EffectiveEpsSurprisePercent != current.EffectiveEpsSurprisePercent ||
            previous.EffectiveRevenueEstimateMillions != current.EffectiveRevenueEstimateMillions ||
            previous.EffectiveRevenueActualMillions != current.EffectiveRevenueActualMillions ||
            previous.EffectiveRevenueSurprisePercent != current.EffectiveRevenueSurprisePercent ||
            previous.LatestCompletedBarAtUtc != current.LatestCompletedBarAtUtc ||
            previous.ResultNewsPublishedAtUtc != current.ResultNewsPublishedAtUtc ||
            previous.NewsHeadline != current.NewsHeadline ||
            previous.EventReturnPercent != current.EventReturnPercent ||
            previous.Reason != current.Reason ||
            previous.NewsUrl != current.NewsUrl ||
            previous.NewsProvider != current.NewsProvider ||
            previous.NewsSentiment != current.NewsSentiment ||
            previous.Ema10 != current.Ema10 ||
            previous.Ema20 != current.Ema20 ||
            previous.MacdHistogram != current.MacdHistogram ||
            previous.SlotRelativeVolume != current.SlotRelativeVolume ||
            current.AnalyzedAtUtc - previous.AnalyzedAtUtc >= heartbeat;
    }
}
