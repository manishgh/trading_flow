using System.Text.RegularExpressions;
using TradingFlow.Alpaca;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Web.Services.Charts;

public sealed record ChartSeriesPage(ChartSeriesSnapshot Snapshot, DateTimeOffset? NextBeforeUtc)
{
    internal Func<ChartFormingCandle>? ReadForming { get; init; }
}

public interface IChartSeriesReader
{
    Task<ChartSeriesPage> ReadAsync(ChartSeriesKey key, DateTimeOffset? beforeUtc, CancellationToken token);

    // A watch may read a separate display option alongside the normal completed series key.
    Task<ChartSeriesPage> ReadWatchedAsync(ChartSeriesKey key, ChartWatchOptions options, CancellationToken token) =>
        ReadAsync(key, null, token);
}

/// <summary>Projects one authorized observational chart without changing provider subscriptions.</summary>
public sealed partial class ChartSeriesService(
    Lazy<IMarketDataProvider> provider,
    ICandleStore candles,
    AlpacaMarketStateStreamService stream,
    StreamingMarketStateOptions streamOptions,
    INewsFeedRepository news,
    IEarningsRepository earnings,
    TimeProvider clock,
    ILogger<ChartSeriesService> logger) : IChartSeriesReader
{
    private readonly ChartHistorySource source = new(provider, candles, clock,
        TimeSpan.FromMinutes(streamOptions.ActiveSessionStalenessMinutes), logger);
    private readonly ChartIndicatorCache indicators = new();
    private const int MaximumBars = 500;
    private const int MaximumMarkers = 100;
    private readonly object projectionGate = new();
    private readonly Dictionary<ChartSeriesKey, ProjectionMemo> projections = [];
    private readonly object markerGate = new();
    private readonly Dictionary<(string Symbol, DateTimeOffset Start, DateTimeOffset? Before), MarkerMemo> markers = [];

    // Keep calculation counts available to offline fanout tests without exposing a public operator route.
    internal ChartIndicatorCacheMetrics MeasureIndicators() => indicators.Measure();

    // The visible key chooses context only. A caller cannot select a feed, URL,
    // arbitrary timeframe, unbounded window or strategy execution scope.
    public static ChartSeriesKey Normalize(ChartSeriesKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var symbol = AlpacaSymbolMapper.ToCanonicalSymbol(key.Symbol);
        var interval = key.Interval?.Trim().ToLowerInvariant();
        var session = key.SessionView?.Trim().ToLowerInvariant();
        var context = key.Context?.Trim().ToLowerInvariant();
        var weekly = interval == "1w";
        if (context == "auto")
        {
            context = weekly ? "weekly" : interval == "1d" ? "swing" : "intraday";
        }
        if (!SymbolPattern().IsMatch(symbol) || interval is not ("1m" or "5m" or "15m" or "1d" or "1w") ||
            session is not ("regular" or "extended") ||
            (weekly && (session != "regular" || context != "weekly" || !String.IsNullOrWhiteSpace(key.Indicators))) ||
            (interval == "1d" && (session != "regular" || context is not ("swing" or "week" or "month" or "year"))) ||
            (interval is not ("1d" or "1w") && context is not ("intraday" or "swing" or "week" or "month" or "year")))
        {
            throw new ArgumentException("Unsupported symbol, interval, session view or chart context.", nameof(key));
        }

        return new ChartSeriesKey(symbol, interval, session, context!, ChartIndicatorProfile.Normalize(key.Indicators, interval, session));
    }

    // Canonical Alpaca equity symbols admit limited class-share punctuation.
    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();

    // History always reads the completed page without attaching a forming observation.
    public Task<ChartSeriesPage> ReadAsync(ChartSeriesKey key, DateTimeOffset? beforeUtc, CancellationToken token) =>
        ReadPageAsync(key, beforeUtc, false, token);

    // The watch keeps all completed source acquisitions and calculation keys identical for either option.
    public Task<ChartSeriesPage> ReadWatchedAsync(ChartSeriesKey key, ChartWatchOptions options, CancellationToken token) =>
        ReadPageAsync(key, null, options.FormingPreview, token);

    // Read a bounded visible page with a stable source acquisition and current canonical versions.
    private async Task<ChartSeriesPage> ReadPageAsync(ChartSeriesKey key, DateTimeOffset? beforeUtc, bool forming,
        CancellationToken token)
    {
        key = Normalize(key);
        var requestedAt = clock.GetUtcNow().ToUniversalTime();
        var cutoff = beforeUtc?.ToUniversalTime() ?? requestedAt;
        if (cutoff > requestedAt || cutoff < requestedAt.AddYears(-10))
        {
            throw new ArgumentException("The chart cursor is outside the supported history window.", nameof(beforeUtc));
        }
        if (key.Interval == "1w")
        {
            return AttachForming(await ReadWeeklyPageAsync(key, cutoff, beforeUtc is not null, requestedAt, token),
                key, null, forming);
        }

        var daily = key.Interval == "1d";
        var studies = ChartIndicatorProfile.Read(key);
        var needsDaily = !daily && (key.Context != "intraday" && beforeUtc is null ||
            studies.Any(study => study.SourceInterval == "1d"));
        var anchor = ChartBarProjector.NewYorkDate(cutoff.AddTicks(-1));
        var historyKey = new ChartHistoryKey(key.Symbol, daily, anchor);
        ChartHistoryBatch history;
        ChartHistoryBatch? dailyHistory;
        try
        {
            var read = source.ReadAsync(key.Symbol, daily, anchor, token);
            var readDaily = needsDaily ? ReadDailyContextAsync(key.Symbol, anchor, token) : Task.FromResult<ChartHistoryBatch?>(null);
            await Task.WhenAll((Task)read, readDaily);
            history = await read;
            dailyHistory = await readDaily;
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Chart sources unavailable for {Symbol} {Interval}.", key.Symbol, key.Interval);
            return AttachForming(Unavailable(key, requestedAt), key, null, forming);
        }
        if (history.Sessions.Count == 0)
        {
            return AttachForming(Unavailable(key, requestedAt), key, null, forming);
        }

        var projector = history.Projector;
        var canonical = daily || beforeUtc is not null ? null : await stream.ReadChartCanonicalAsync(
            key.Symbol, ChartBarProjector.At(history.Sessions[0], TimeOnly.MinValue), cutoff, requestedAt, token);
        var asOf = clock.GetUtcNow().ToUniversalTime();
        var projection = ReadProjection(key with { Indicators = "" }, history, dailyHistory, canonical, projector,
            cutoff, asOf, beforeUtc is not null);
        var visible = projection.Visible;
        if (visible.Count == 0)
        {
            return AttachForming(Unavailable(key, asOf, "There are no completed, compatible SIP candles in this window."),
                key, canonical, forming);
        }

        var last = visible[^1];
        var zones = projection.Zones;
        var levels = projection.Levels;
        var calculated = await ReadIndicatorsAsync(key, historyKey, history, dailyHistory, canonical, studies, visible, beforeUtc, asOf, token);
        var markerRows = await ReadMarkersAsync(key.Symbol, visible[0].Bar.Timestamp, beforeUtc, cutoff, asOf, token);
        var (status, reason, liveThrough) = Classify(key, projector, history, canonical, requestedAt, beforeUtc is not null, last);
        var reconstructed = visible.Any(bar => bar.Bar.KnownAtUtc is null || bar.AvailableAtUtc > cutoff) ||
            levels.Any(level => level.AvailableAtUtc > cutoff);
        var coverage = projection.IsCovered ? "complete" : "partial";
        var notes = BuildNotes(history, projection.BasisBreaks, key, zones.Count, levels.Count, reconstructed, projector, requestedAt);
        var page = new ChartSeriesPage(new ChartSeriesSnapshot(
                key, asOf, status, reason,
                new ChartSeriesEvidence("alpaca", "sip", "all", coverage,
                    liveThrough ?? history.CoveredThroughUtc, last.EndUtc,
                    canonical?.Bars.MaxBy(bar => bar.KnownAtUtc)?.KnownAtUtc,
                    canonical is null ? null : $"{canonical.OwnershipFence}:{canonical.SubscriptionGeneration}",
                    reconstructed, notes, projection.BasisBreaks.Where(point => point >= visible[0].Bar.Timestamp && point < cutoff).Order().ToArray()),
                visible.Select(ToCandle).ToArray(), zones, levels.Select(level => ToLevel(level, last.Bar.Close)).ToArray(), markerRows,
                Indicators: calculated),
            beforeUtc is null || visible[0].Bar.Timestamp < cutoff ? visible[0].Bar.Timestamp : null);
        return AttachForming(page, key, canonical, forming);
    }

    // Keep the source copy request-local and read the fence again immediately before the separate watch frame.
    private ChartSeriesPage AttachForming(ChartSeriesPage page, ChartSeriesKey key, ChartCanonicalSession? canonical,
        bool forming)
    {
        if (!forming) return page;
        return page with { ReadForming = key.Interval is "1m" or "5m" or "15m"
            ? () => stream.ReadChartForming(key, canonical) : () => new("unsupported") };
    }

    // Higher-context prices use the same existing bounded daily entry. A failed daily read leaves intraday candles available.
    private async Task<ChartHistoryBatch?> ReadDailyContextAsync(string symbol, DateOnly anchor, CancellationToken token)
    {
        try { return await source.ReadAsync(symbol, true, anchor, token); }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Daily chart context unavailable for {Symbol}.", symbol);
            return null;
        }
    }

    // Numerical work shares the canonical history revision across viewers and is skipped entirely for candle-only views.
    private async Task<IReadOnlyList<ChartIndicatorSeries>> ReadIndicatorsAsync(ChartSeriesKey key, ChartHistoryKey historyKey,
        ChartHistoryBatch history, ChartHistoryBatch? dailyHistory, ChartCanonicalSession? canonical, ChartStudy[] studies,
        IReadOnlyList<ChartCompletedBar> visible, DateTimeOffset? beforeUtc, DateTimeOffset asOf, CancellationToken token)
    {
        if (studies.Length == 0) return [];
        var crossDaily = !historyKey.Daily && studies.Any(study => study.SourceInterval == "1d") ? dailyHistory : null;
        var first = visible[0];
        var last = visible[^1];
        var prior = studies.Any(study => study.Name == "rvol") && !historyKey.Daily
            ? history.Projector.PriorTradingDates(first.SessionDate, MarketEvidenceProfile.DefaultLookbackSessions) : [];
        var warm = prior.Count > 0 ? source.WarmMinuteHistory(key.Symbol, prior[0], last.SessionDate) : [];
        var revision = ChartInputRevision.Combine(new[] { history.Revision, ChartInputRevision.Canonical(canonical),
            crossDaily?.Revision ?? "-" }.Concat(warm.Select(batch => batch.Revision)));
        var view = $"{revision}:{first.Bar.Timestamp.UtcTicks}:{last.EndUtc.UtcTicks}:{visible.Count}:{beforeUtc?.UtcTicks}";
        var profile = $"{key.Interval}:{key.SessionView}:{key.Indicators}";
        var calculated = await indicators.ReadAsync(new ChartCalculationKey(historyKey, profile, view),
            workToken => ChartStudyProjection.Calculate(key, studies, history, crossDaily, Merge(history, canonical, key.Symbol),
                visible, warm, last.EndUtc, beforeUtc ?? asOf, asOf, revision, workToken), source.IsActive, token);
        return calculated ?? studies.Select(study =>
            study.Unavailable("Chart indicators are busy. Retry this selection shortly.", "retry")).ToArray();
    }

    // Reuse exact completed inputs within the same close boundary; status and
    // freshness are evaluated outside this memo. Historical cursor pages do not displace recent views.
    private ProjectedChart ReadProjection(
        ChartSeriesKey key, ChartHistoryBatch history, ChartHistoryBatch? dailyHistory,
        ChartCanonicalSession? canonical, ChartBarProjector projector,
        DateTimeOffset cutoff, DateTimeOffset asOf, bool paged)
    {
        if (paged)
        {
            return Project(key, history, dailyHistory, canonical, projector, cutoff, asOf, paged);
        }
        var minute = cutoff.UtcTicks / TimeSpan.TicksPerMinute;
        Lazy<ProjectedChart> result;
        lock (projectionGate)
        {
            if (projections.TryGetValue(key, out var previous) && ReferenceEquals(previous.History, history) &&
                ReferenceEquals(previous.DailyHistory, dailyHistory) && previous.Minute == minute &&
                SameCanonicalInputs(previous.Canonical, canonical))
            {
                result = previous.Result;
            }
            else
            {
                if (projections.Count >= 32 && !projections.ContainsKey(key))
                {
                    projections.Remove(projections.MinBy(item => item.Value.Used).Key);
                }
                result = new Lazy<ProjectedChart>(() => Project(key, history, dailyHistory, canonical, projector, cutoff, asOf, paged));
            }
            projections[key] = new ProjectionMemo(history, dailyHistory, canonical, minute, asOf, result);
        }
        return result.Value;
    }

    // Preserve a revision even when its candle prices match an older accepted version.
    private static bool SameCanonicalInputs(ChartCanonicalSession? left, ChartCanonicalSession? right) =>
        left is null ? right is null : right is not null && left.OwnershipFence == right.OwnershipFence &&
        left.SubscriptionGeneration == right.SubscriptionGeneration && left.NoTradeProofAtUtc == right.NoTradeProofAtUtc &&
        left.Bars.SequenceEqual(right.Bars) && left.NoTradeMinutes.SetEquals(right.NoTradeMinutes);

    // Derive all interval values from one reconciled input without carrying chart clocks into availability.
    private static ProjectedChart Project(
        ChartSeriesKey key, ChartHistoryBatch history, ChartHistoryBatch? dailyHistory,
        ChartCanonicalSession? canonical, ChartBarProjector projector,
        DateTimeOffset cutoff, DateTimeOffset asOf, bool paged)
    {
        if (key.Interval == "1w")
        {
            return ProjectWeekly(key.Symbol, history, cutoff, asOf);
        }
        var merged = Merge(history, canonical, key.Symbol);
        var daily = key.Interval == "1d";
        var projected = daily
            ? projector.Daily(merged.Bars.Values, merged.BasisBreaks, cutoff, asOf)
            : projector.Intraday(merged.Bars, merged.Quiet, merged.BasisBreaks,
                key.Interval, key.SessionView == "extended", cutoff, asOf);
        var visible = SelectVisible(projected, key, history.Sessions);
        if (visible.Count == 0)
        {
            return new ProjectedChart(merged.BasisBreaks, visible, [], [], false);
        }

        IReadOnlyList<ChartZone> zones = paged ? [] :
            BuildZones(key, daily, projected, projector, merged, history.Sessions, dailyHistory,
                cutoff, asOf, visible[^1].Bar.Close);
        // Anchor display overlays to the latest shown exchange session. The real cutoff still governs eligibility.
        var session = visible[^1].SessionDate;
        var levels = daily
            ? ChartContextLevels.Daily(projected, history.Sessions, session)
            : ChartContextLevels.Intraday(projector, history.Sessions, session, merged.Bars, merged.Quiet,
                merged.BasisBreaks, visible, cutoff, asOf);
        var covered = HasViewportCoverage(key, history, projector, merged, projected, visible, cutoff, asOf);
        return new ProjectedChart(merged.BasisBreaks, visible, zones, levels, covered);
    }

    // Return current bands with fixed warm-up independent of display interval, zoom or window size.
    private static IReadOnlyList<ChartZone> BuildZones(
        ChartSeriesKey key, bool daily, IReadOnlyList<ChartCompletedBar> projected, ChartBarProjector projector,
        MergedChartSource merged, IReadOnlyList<DateOnly> sessions, ChartHistoryBatch? dailyHistory,
        DateTimeOffset cutoff, DateTimeOffset asOf, decimal close)
    {
        var dailyContext = key.Context != "intraday";
        if (!daily && dailyContext && (dailyHistory is null || merged.BasisBreaks.Any(point => point < cutoff)))
            return [];
        var zoneBars = daily ? projected : dailyContext
            ? dailyHistory!.Projector.Daily(dailyHistory.Bars.Values, dailyHistory.BasisBreaks, cutoff, asOf)
            : projector.Intraday(merged.Bars, merged.Quiet, merged.BasisBreaks, "15m", false, cutoff, asOf);
        if (!daily && dailyContext) sessions = dailyHistory!.Sessions;
        var activeCount = key.Context switch
        {
            "week" => 5,
            "month" => 21,
            "year" => 252,
            "swing" => 64,
            _ => 6
        };
        var eligibleSessions = dailyContext
            ? sessions.Where(date => (dailyHistory?.Projector ?? projector).Regular(date)?.EndUtc <= cutoff)
            : sessions;
        var active = eligibleSessions.TakeLast(activeCount).ToHashSet();
        var bars = ChartSupportResistance.Calculate($"{key.Symbol}:{(dailyContext ? "1d" : "15m")}:{key.Context}:sip:all",
            zoneBars, active, asOf, close);
        return bars.Select(band => new ChartZone(band.Id, band.Type, band.Lower, band.Upper, band.PivotCount,
            band.SourceTimeframe, band.FirstPivotUtc, band.LatestConfirmationUtc, band.EffectiveAtUtc,
            band.SourceDates, band.IsPriceInBand, CurrentOnly: true)).ToArray();
    }

    // Join canonical 1m versions only through matching overlap. When the proof
    // fails, live prices remain visible with an explicit basis boundary.
    private static MergedChartSource Merge(ChartHistoryBatch history, ChartCanonicalSession? canonical, string symbol)
    {
        var bars = new Dictionary<DateTimeOffset, ChartSourceBar>(history.Bars);
        var quiet = new Dictionary<DateTimeOffset, DateTimeOffset>(history.Quiet);
        var breaks = history.BasisBreaks.ToHashSet();
        // Legacy versions without a receipt can still appear via bounded
        // reconstruction. The request clock cannot serve as a live acceptance clock.
        var incoming = canonical?.Bars.Where(bar => bar.KnownAtUtc is not null &&
            ChartHistorySource.Compatible(bar, symbol, "1m")).ToArray() ?? [];
        if (incoming.Length > 0)
        {
            var first = incoming[0].Timestamp;
            var overlap = incoming.Where(bar => bars.ContainsKey(bar.Timestamp)).ToArray();
            var verified = overlap.Length > 0 && overlap.All(bar => ChartHistorySource.SamePrices(bar, bars[bar.Timestamp].Bar));
            if (bars.Count > 0 && !verified)
            {
                if (bars.Keys.Any(utc => utc < first))
                {
                    breaks.Add(first);
                }
                var end = incoming[^1].Timestamp;
                var after = bars.Keys.Where(utc => utc > end).Order().FirstOrDefault();
                if (after != default)
                {
                    breaks.Add(after);
                }

                // When source prices disagree, an archive bar between live
                // bars cannot lend its price or proof to that live segment.
                var retained = bars.Keys.Concat(quiet.Keys).Where(stamp => stamp >= first && stamp <= end).Distinct().ToArray();
                foreach (var stamp in retained)
                {
                    bars.Remove(stamp);
                    quiet.Remove(stamp);
                }
            }
            foreach (var bar in incoming)
            {
                bars[bar.Timestamp] = ChartHistorySource.Source(bar, "canonical_stream", bar.KnownAtUtc!.Value, TimeSpan.FromMinutes(1));
                quiet.Remove(bar.Timestamp);
            }
        }

        if (canonical?.NoTradeProofAtUtc is { } proof)
        {
            foreach (var timestamp in canonical.NoTradeMinutes.Where(stamp => !bars.ContainsKey(stamp)))
            {
                quiet.TryAdd(timestamp, proof);
            }
        }
        return new MergedChartSource(bars, quiet, breaks);
    }

    // Window sizes follow the selected context, with a single-session 1m page.
    private static IReadOnlyList<ChartCompletedBar> SelectVisible(
        IReadOnlyList<ChartCompletedBar> bars, ChartSeriesKey key, IReadOnlyList<DateOnly> sessions)
    {
        var lastObserved = bars.LastOrDefault()?.SessionDate;
        if (lastObserved is null)
        {
            return [];
        }
        var visibleSessions = sessions.Where(date => date <= lastObserved).TakeLast(ViewportDays(key)).ToHashSet();
        return bars.Where(bar => visibleSessions.Contains(bar.SessionDate)).TakeLast(MaximumBars).ToArray();
    }

    // Use the same session-count contract for pagination and coverage.
    private static int ViewportDays(ChartSeriesKey key) => key.Interval != "1d" ? key.Interval == "1m" ? 1 : 6 : key.Context switch
    {
        "week" => 5,
        "month" => 21,
        "year" => 252,
        "swing" => 126,
        _ => 126
    };

    // Separate source ownership, active segment freshness and daily completion timing.
    private (string Status, string? Reason, DateTimeOffset? LiveThrough) Classify(
        ChartSeriesKey key, ChartBarProjector projector, ChartHistoryBatch history,
        ChartCanonicalSession? canonical, DateTimeOffset now, bool paged, ChartCompletedBar last)
    {
        if (paged)
        {
            return ("history_only", "This page shows earlier completed history.", null);
        }
        var segment = projector.Segment(now);
        var grace = TimeSpan.FromMinutes(streamOptions.ActiveSessionStalenessMinutes);
        if (key.Interval == "1d")
        {
            var due = history.Sessions.Select(projector.Regular).OfType<ChartTimeRange>()
                .Where(range => range.EndUtc + grace <= now).LastOrDefault();
            return due is not null && last.EndUtc < due.EndUtc
                ? ("delayed", "The latest scheduled daily close has no verified SIP candle yet.", null)
                : ("history_only", "Daily candles are the provider's completed SIP daily series.", null);
        }
        if (segment is null || (key.SessionView == "regular" && segment != "regular"))
        {
            return ("closed", "The selected exchange segment is closed.", null);
        }
        if (canonical is null)
        {
            return ("history_only", "This symbol has no active candle stream. Stored and retrieved history is shown.", null);
        }
        var newestBar = canonical.Bars.LastOrDefault(bar => bar.KnownAtUtc is not null && bar.Timestamp <= now);
        var barThrough = newestBar?.Timestamp.AddMinutes(1);
        var through = barThrough;
        if (canonical.NoTradeMinutes.Count > 0)
        {
            var quiet = canonical.NoTradeMinutes.Max().AddMinutes(1);
            through = through is null || quiet > through ? quiet : through;
        }
        if (canonical.NeedsRepair)
        {
            return ("repairing", "Canonical minute coverage is being repaired.", through);
        }
        if (through is null || now - through > grace)
        {
            return ("delayed", "The owned minute stream has no recent completed or verified quiet interval.", through);
        }
        return ("streaming", barThrough is null || through > barThrough
            ? "The latest covered interval had no qualifying trades." : null, through);
    }

    // Coalesce bounded stored marker reads across chart pollers. A subscriber
    // disconnect cancels only its wait, and cannot orphan the shared query.
    private Task<IReadOnlyList<ChartMarker>> ReadMarkersAsync(
        string symbol, DateTimeOffset start, DateTimeOffset? before, DateTimeOffset cutoff, DateTimeOffset asOf, CancellationToken token)
    {
        var key = (symbol, start, before);
        Task<IReadOnlyList<ChartMarker>> read;
        lock (markerGate)
        {
            if (markers.TryGetValue(key, out var previous) && (!previous.Read.IsCompleted || previous.Expires > asOf))
            {
                read = previous.Read;
            }
            else
            {
                if (markers.Count >= 32 && !markers.ContainsKey(key))
                {
                    var oldest = markers.Where(item => item.Value.Read.IsCompleted).MinBy(item => item.Value.Expires);
                    if (oldest.Value is null)
                    {
                        return Task.FromResult<IReadOnlyList<ChartMarker>>([]);
                    }
                    markers.Remove(oldest.Key);
                }
                read = LoadMarkersAsync(symbol, start, cutoff, asOf);
                markers[key] = new MarkerMemo(read, asOf.AddSeconds(30));
            }
        }

        return read.WaitAsync(token);
    }

    // Mark known records in the viewport even if their event falls between candles.
    private async Task<IReadOnlyList<ChartMarker>> LoadMarkersAsync(
        string symbol, DateTimeOffset start, DateTimeOffset cutoff, DateTimeOffset asOf)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            var articlesTask = news.QueryAsync(new NewsFeedQuery(start, Ticker: symbol, UntilUtc: cutoff,
                Take: MaximumMarkers, StockLinkedOnly: true), budget.Token);
            var earningsTask = earnings.GetCalendarAsync(
                ChartBarProjector.NewYorkDate(start), ChartBarProjector.NewYorkDate(cutoff), [symbol], budget.Token);
            await Task.WhenAll(articlesTask, earningsTask);
            var articles = (await articlesTask).Records.Where(item => item.Timestamp >= start && item.Timestamp <= cutoff && item.IngestedAt <= asOf)
                .Select(item => new ChartMarker($"news:{item.Provider}:{item.Id}", "news", item.Timestamp, item.IngestedAt,
                    LimitText(item.Headline, 240)!, LimitText(item.Summary, 300), item.Provider, SafeUrl(item.Url)));
            var releases = (await earningsTask).Where(item => item.ScheduledAtUtc >= start && item.ScheduledAtUtc <= cutoff &&
                    item.FirstSeenAtUtc <= asOf)
                .Select(item => new ChartMarker($"earnings:{item.Id}", "earnings", item.ScheduledAtUtc, item.FirstSeenAtUtc,
                    item.IsScheduleEstimate ? "Estimated earnings schedule" : "Earnings",
                    item.ResultFirstSeenAtUtc is { } first && first <= asOf ? $"Results first observed {first:O}" : null,
                    item.Provider, SafeUrl(item.SourceUrl), item.IsScheduleEstimate));
            return articles.Concat(releases).OrderByDescending(marker => marker.EventAtUtc).Take(MaximumMarkers)
                .OrderBy(marker => marker.EventAtUtc).ThenBy(marker => marker.Id, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Stored chart markers unavailable for {Symbol}.", symbol);
            return [];
        }
    }

    // Keep provider links and text constrained independently of HTML rendering.
    private static string? SafeUrl(string? url) =>
        url is { Length: <= 1000 } && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        parsed.Scheme is "https" or "http" ? parsed.AbsoluteUri : null;

    // Avoid letting third-party headlines consume the bounded message budget.
    private static string? LimitText(string? value, int length) =>
        value is not null && value.Length > length ? value[..length] : value;

    // A viewport is complete only if its expected edges and internal windows are actually proved.
    private static bool HasViewportCoverage(ChartSeriesKey key, ChartHistoryBatch history, ChartBarProjector projector,
        MergedChartSource source, IReadOnlyList<ChartCompletedBar> projected, IReadOnlyList<ChartCompletedBar> visible,
        DateTimeOffset cutoff, DateTimeOffset asOf)
    {
        if (source.BasisBreaks.Any(point => point >= visible[0].Bar.Timestamp && point < visible[^1].EndUtc) ||
            visible[^1].Continuity != visible[0].Continuity)
        {
            return false;
        }
        var count = ViewportDays(key);
        if (key.Interval == "1d")
        {
            var expected = history.Sessions.Where(date => projector.Regular(date)?.EndUtc <= cutoff).TakeLast(count);
            var present = projected.Select(bar => bar.SessionDate).ToHashSet();
            return expected.All(present.Contains);
        }

        var extended = key.SessionView == "extended";
        var sessions = history.Sessions.Where(date => extended
                ? ChartBarProjector.At(date, new TimeOnly(4, 0)) < cutoff
                : projector.Regular(date)?.StartUtc < cutoff)
            .TakeLast(count).Concat(visible.Select(bar => bar.SessionDate)).Distinct().ToArray();
        return projector.IsIntradayCovered(sessions, source.Bars, source.Quiet, source.BasisBreaks,
            key.Interval, extended, cutoff, asOf);
    }

    // Mark reconstructed availability and explain intentionally suppressed overlays.
    private static IReadOnlyList<string> BuildNotes(
        ChartHistoryBatch history, IReadOnlySet<DateTimeOffset> basisBreaks, ChartSeriesKey key, int zones, int levels,
        bool reconstructed, ChartBarProjector projector, DateTimeOffset now)
    {
        var notes = new List<string>();
        if (history.Limitation is { } limitation) notes.Add(limitation);
        if (basisBreaks.Count > 0) notes.Add("Price-basis join could not be verified. Overlays stop at the break.");
        if (reconstructed) notes.Add("Reconstructed history uses later acquisitions or revisions. Some original source receipts are unavailable.");
        if (zones > 0) notes.Add("Zones show current structure. Their past geometry is unverified.");
        if (zones == 0) notes.Add("No eligible confirmed zones. A zone needs 120 covered source bars and two completed right-side bars.");
        if (levels == 0) notes.Add("Reference levels are unavailable without complete compatible inputs.");
        if (projector.Segment(now) is not null)
            notes.Add(key.Interval == "1d" ? "The current daily candle remains pending until the verified exchange close." : "The current interval is pending close.");
        return notes;
    }

    // Apply presentation-only wire field names to immutable completed evidence.
    private static ChartCandle ToCandle(ChartCompletedBar item) =>
        new(item.Bar.Timestamp, item.EndUtc, item.SessionDate, item.Segment, item.Bar.Open, item.Bar.High,
            item.Bar.Low, item.Bar.Close, item.Bar.Volume, item.SourceKind, item.Bar.KnownAtUtc, item.RetrievedAtUtc, item.AvailableAtUtc);

    // Keep the stated signed distance relative to the selected completed close.
    private static ChartLevel ToLevel(ChartReferenceLevel level, decimal close) =>
        new(level.Key, level.Label, level.Value, level.Value > 0 ? 100m * (close / level.Value - 1m) : null,
            level.AvailableAtUtc, level.SourceTimeframe, level.SourceDate,
            level.Points?.Select(point => new ChartLevelPoint(point.StartUtc, point.Value, point.AvailableAtUtc)).ToArray());

    // Explicitly represent no verified data with an empty series, never synthetic bars.
    private static ChartSeriesPage Unavailable(ChartSeriesKey key, DateTimeOffset now, string reason = "Verified SIP chart history is unavailable for this window.") =>
        new(new ChartSeriesSnapshot(key, now, "unavailable", reason,
            new ChartSeriesEvidence("alpaca", "sip", "all", "unavailable", null, null, null, null, false, [], []),
            [], [], [], [], Indicators: ChartIndicatorProfile.Read(key).Select(study => study.Unavailable(reason)).ToArray()), null);

    private sealed record ProjectionMemo(
        ChartHistoryBatch History, ChartHistoryBatch? DailyHistory, ChartCanonicalSession? Canonical,
        long Minute, DateTimeOffset Used, Lazy<ProjectedChart> Result);

    private sealed record ProjectedChart(
        IReadOnlySet<DateTimeOffset> BasisBreaks, IReadOnlyList<ChartCompletedBar> Visible, IReadOnlyList<ChartZone> Zones,
        IReadOnlyList<ChartReferenceLevel> Levels, bool IsCovered, ChartWeeklyProjection? Weekly = null);

    private sealed record MarkerMemo(Task<IReadOnlyList<ChartMarker>> Read, DateTimeOffset Expires);

}
