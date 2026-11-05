using System.Globalization;
using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class EarningsApiEndpoints
{
    public static IEndpointRouteBuilder MapTradingFlowEarningsApi(this IEndpointRouteBuilder endpoints)
    {
        MapGroup(endpoints.MapGroup("/api/earnings"));
        MapGroup(endpoints.MapGroup("/api/v1/earnings"));
        return endpoints;
    }

    private static async Task<IResult> ApplyViewAsync(EarningsCalendarResponse response, EarningsViewQuery query,
        System.Security.Claims.ClaimsPrincipal user, EarningsViewService views, CancellationToken token)
    {
        try { return Results.Ok(await views.FilterAsync(response, query, user, token)); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (InvalidOperationException error) { return Results.BadRequest(error.Message); }
    }

    private static void MapGroup(RouteGroupBuilder group)
    {
        group.MapGet("/today-next-business-day", async (
            string? session,
            string? marketCap,
            string? scope,
            EarningsMonitor monitor,
            FinvizSectorDirectory sectors,
            NewsFeedService newsFeed,
            TimeProvider clock,
            [Microsoft.AspNetCore.Http.AsParameters] EarningsViewQuery viewQuery,
            System.Security.Claims.ClaimsPrincipal user,
            EarningsViewService views,
            CancellationToken cancellationToken) =>
        {
            if (!EarningsCalendarFilter.TryParse(session, marketCap, out var filter, out var filterError))
            {
                return Results.BadRequest(filterError);
            }
            if (!TryParseDateScope(scope, out var dateScope, out var scopeError))
            {
                return Results.BadRequest(scopeError);
            }

            var nowUtc = clock.GetUtcNow();
            var exchangeToday = EarningsCalendarDates.ExchangeDate(nowUtc);
            var nextBusinessDate = EarningsCalendarDates.NextWeekday(exchangeToday);
            var snapshot = await monitor.GetCalendarAsync(
                exchangeToday,
                nextBusinessDate.AddDays(1),
                cancellationToken);
            var news = await newsFeed.GetEarningsTimelineAsync(
                snapshot.Items.Select(item => item.Event.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                nowUtc,
                cancellationToken);
            return await ApplyViewAsync(MapSnapshot(
                snapshot,
                monitor.GetStatus(),
                nowUtc,
                news,
                onlyShortHorizon: true,
                filter,
                dateScope,
                sectors.Find), viewQuery, user, views, cancellationToken);
        }).AllowAnonymous();

        group.MapGet("/calendar", async (
            string from,
            string to,
            string? session,
            string? marketCap,
            EarningsMonitor monitor,
            FinvizSectorDirectory sectors,
            NewsFeedService newsFeed,
            TimeProvider clock,
            [Microsoft.AspNetCore.Http.AsParameters] EarningsViewQuery viewQuery,
            System.Security.Claims.ClaimsPrincipal user,
            EarningsViewService views,
            CancellationToken cancellationToken) =>
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
                !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate) ||
                toDate < fromDate || toDate.DayNumber - fromDate.DayNumber > 34)
            {
                return Results.BadRequest("Choose valid yyyy-MM-dd dates covering at most 35 days, with to >= from.");
            }

            if (!EarningsCalendarFilter.TryParse(session, marketCap, out var filter, out var filterError))
            {
                return Results.BadRequest(filterError);
            }

            var snapshot = await monitor.GetCalendarAsync(fromDate, toDate, cancellationToken);
            var nowUtc = clock.GetUtcNow();
            var news = await newsFeed.GetEarningsTimelineAsync(
                snapshot.Items.Select(item => item.Event.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                nowUtc,
                cancellationToken);
            return await ApplyViewAsync(MapSnapshot(
                snapshot,
                monitor.GetStatus(),
                nowUtc,
                news,
                onlyShortHorizon: false,
                filter,
                "custom",
                sectors.Find), viewQuery, user, views, cancellationToken);
        }).AllowAnonymous();

        group.MapGet("/status", (EarningsMonitor monitor) => Results.Ok(monitor.GetStatus()))
            .AllowAnonymous();

        // Read-only diagnostic: what the live monitor would have shown for one stored event over a past window.
        group.MapGet("/replay", async (
            string ticker,
            string date,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int? stepMinutes,
            EarningsMonitor monitor,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var step = stepMinutes ?? 5;
            if (String.IsNullOrWhiteSpace(ticker) ||
                !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reportDate) ||
                toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(3) || toUtc > clock.GetUtcNow() || step is < 5 or > 60)
            {
                return Results.BadRequest("Give a ticker, its yyyy-MM-dd report date, and a past window of at most 3 days with a 5-60 minute step.");
            }

            try
            {
                var snapshots = await monitor.ReplayAsync(ticker.Trim().ToUpperInvariant(), reportDate, fromUtc, toUtc,
                    TimeSpan.FromMinutes(step), cancellationToken);
                return Results.Ok(snapshots.Select(item => new
                {
                    item.AnalyzedAtUtc,
                    Label = EarningsAssessmentLabel.Format(item.ResultAssessment, item.BreakoutAssessment),
                    item.LatestCompletedBarAtUtc,
                    item.LatestClose,
                    item.PreReleaseReferenceHigh,
                    item.EventReturnPercent,
                    item.SlotRelativeVolume,
                    item.Reason
                }));
            }
            catch (KeyNotFoundException error)
            {
                return Results.NotFound(error.Message);
            }
        }).AllowAnonymous();

        group.MapPost("/refresh", async (
            EarningsMonitor monitor,
            NewsFeedService newsFeed,
            CancellationToken cancellationToken) =>
        {
            await Task.WhenAll(
                monitor.RefreshAsync(forceCalendarRefresh: true, cancellationToken),
                newsFeed.RefreshOnceAsync(cancellationToken));
            return Results.Ok(monitor.GetStatus());
        }).RequireAuthorization();
    }

    internal static EarningsCalendarResponse MapSnapshot(
        EarningsCalendarSnapshot snapshot,
        EarningsMonitorStatus monitorStatus,
        DateTimeOffset requestAtUtc,
        IReadOnlyList<EarningsNewsEvidenceResponse> news,
        bool onlyShortHorizon,
        EarningsCalendarFilter filter,
        string dateScope,
        Func<string, string?> sectorOf)
    {
        var exchangeToday = EarningsCalendarDates.ExchangeDate(requestAtUtc);
        var nextBusinessDate = EarningsCalendarDates.NextWeekday(exchangeToday);
        var mapped = snapshot.Items
            .Select(item => MapItem(item, exchangeToday, nextBusinessDate, sectorOf(item.Event.Ticker)))
            .Where(item => !onlyShortHorizon || MatchesDateScope(item, dateScope))
            .ToArray();
        var filtered = mapped.Where(item => MatchesFilter(item, filter)).ToArray();
        var items = filtered
            .Where(item => item.ScheduledAtUtc >= snapshot.GeneratedAtUtc)
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenBy(item => item.Ticker, StringComparer.Ordinal)
            .Concat(filtered
                .Where(item => item.ScheduledAtUtc < snapshot.GeneratedAtUtc)
                .OrderByDescending(item => item.ScheduledAtUtc)
                .ThenBy(item => item.Ticker, StringComparer.Ordinal))
            .ToArray();
        var newsWindow = EarningsNewsWindowPolicy.Resolve(snapshot.GeneratedAtUtc);
        return new EarningsCalendarResponse(
            snapshot.GeneratedAtUtc,
            EarningsTimeZones.OperatorLocal.Id,
            EarningsTimeZones.NewYork.Id,
            nextBusinessDate,
            nextBusinessDate.ToString("dddd, MMM d", CultureInfo.InvariantCulture),
            dateScope,
            monitorStatus.IsRunning,
            (int)monitorStatus.AnalysisInterval.TotalSeconds,
            monitorStatus.LastAnalysisUtc,
            newsWindow.StartUtc,
            newsWindow.EndUtc,
            newsWindow.Label,
            BuildFilterState(mapped, filter),
            FilterNews(news, items),
            items);
    }

    private static EarningsCalendarItemResponse MapItem(
        EarningsCalendarSnapshotItem item,
        DateOnly exchangeToday,
        DateOnly nextBusinessDate,
        string? sector)
    {
        var calendarEvent = item.Event;
        var analysis = item.Analysis;
        var resultAssessment = analysis?.ResultAssessment ?? EarningsResultAssessment.Unknown;
        var breakoutAssessment = analysis?.BreakoutAssessment ?? EarningsBreakoutAssessment.InsufficientData;
        var providerEps = EarningsEpsOutcomeClassifier.Classify(calendarEvent);
        var eps = EarningsEpsOutcomeClassifier.Classify(
            analysis?.EffectiveEpsEstimate ?? providerEps.Estimate,
            analysis?.EffectiveEpsActual ?? providerEps.Actual,
            analysis?.EffectiveEpsSurprisePercent ?? providerEps.SurprisePercent);
        var revenueEstimate = analysis?.EffectiveRevenueEstimateMillions ?? calendarEvent.RevenueEstimateMillions;
        var revenueActual = analysis?.EffectiveRevenueActualMillions ?? calendarEvent.RevenueActualMillions;
        var revenueSurprise = analysis?.EffectiveRevenueSurprisePercent ?? calendarEvent.RevenueSurprisePercent;
        var newYork = TimeZoneInfo.ConvertTime(calendarEvent.ScheduledAtUtc, EarningsTimeZones.NewYork);
        var reportDate = calendarEvent.ReportDateExchange;
        // The Earnings page lists each reporting day on its own: the previous day's after-close reports stay on
        // that day even though they trade today. The Desk's earnings source and the monitor keep the trading-session rule.
        var dayGroup = reportDate == exchangeToday ? "today"
            : reportDate == nextBusinessDate ? "nextBusinessDay"
            : "other";
        return new EarningsCalendarItemResponse(
            calendarEvent.Id,
            calendarEvent.Ticker,
            calendarEvent.CompanyName,
            dayGroup,
            calendarEvent.ScheduledAtUtc,
            calendarEvent.ScheduledAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            newYork.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
            calendarEvent.ReleaseWindow.ToString(),
            FormatReleaseWindow(calendarEvent.ReleaseWindow),
            calendarEvent.IsScheduleEstimate,
            calendarEvent.MarketCapMillions,
            EarningsCalendarFilter.MarketCapValue(ResolveMarketCapBand(calendarEvent.MarketCapMillions)),
            eps.Estimate,
            eps.Actual,
            eps.SurprisePercent,
            eps.Outcome.ToString(),
            EarningsEpsOutcomeClassifier.Format(eps.Outcome),
            revenueEstimate,
            revenueActual,
            revenueSurprise,
            analysis?.ResultDataSource ?? calendarEvent.Provider,
            calendarEvent.OneDayPriceReactionPercent,
            calendarEvent.Provider,
            calendarEvent.SourceUrl,
            calendarEvent.ProviderReceivedAtUtc,
            calendarEvent.ResultFirstSeenAtUtc,
            resultAssessment.ToString(),
            FormatBreakoutAssessment(breakoutAssessment),
            EarningsAssessmentLabel.Format(resultAssessment, breakoutAssessment),
            analysis?.Reason ?? "Analysis has not run yet.",
            analysis?.ResultNewsPublishedAtUtc,
            analysis?.NewsHeadline,
            analysis?.NewsUrl,
            analysis?.NewsProvider,
            analysis?.NewsSentiment,
            analysis?.LatestCompletedBarAtUtc,
            analysis?.LatestCompletedBarAtUtc is { } latestBar
                ? EarningsTimeZones.ClassifyUsEquitySession(latestBar)
                : null,
            analysis?.PreReleaseReferenceHigh,
            analysis?.PreReleaseReferenceClose,
            analysis?.LatestClose,
            analysis?.EventReturnPercent,
            analysis?.Ema10,
            analysis?.Ema20,
            analysis?.MacdHistogram,
            analysis?.SlotRelativeVolume,
            MapPreviousEarnings(item.PreviousReportedEvent),
            sector,
            analysis?.PreReleaseReferenceAtUtc,
            analysis?.PostReleaseHigh,
            analysis?.PostReleaseHighAtUtc,
            item.LatestTrade?.Price,
            item.LatestTrade?.TradedAtUtc,
            PercentChange(analysis?.PreReleaseReferenceClose, item.LatestTrade?.Price),
            ResultSeenBeforeSlot(calendarEvent, analysis));
    }

    /// <summary>Percent change from a reference price to a later price, or null when either is missing or the reference is 0.</summary>
    private static decimal? PercentChange(decimal? reference, decimal? later) =>
        reference is { } from && from != 0m && later is { } to ? (to - from) / from * 100m : null;

    /// <summary>
    /// True when the first sign of the result, the provider showing it or its news headline, came before the
    /// release window the provider's slot names begins, so the result came out in an earlier session. Sony on
    /// 5 Nov 2026 was listed for after Thursday's close but was out by 03:56 New York that morning.
    /// </summary>
    private static bool ResultSeenBeforeSlot(EarningsCalendarEvent calendarEvent, EarningsAnalysisSnapshot? analysis)
    {
        var firstSign = new[] { calendarEvent.ResultFirstSeenAtUtc, analysis?.ResultNewsPublishedAtUtc }.Min();
        return firstSign < EarningsCalendarDates.ReleaseWindowStartUtc(calendarEvent);
    }

    private static bool TryParseDateScope(string? value, out string scope, out string? error)
    {
        scope = String.IsNullOrWhiteSpace(value) ? "both" : value.Trim();
        if (scope is "both" or "today" or "nextBusinessDay")
        {
            error = null;
            return true;
        }

        error = "scope must be one of: both, today, nextBusinessDay.";
        return false;
    }

    private static bool MatchesDateScope(EarningsCalendarItemResponse item, string dateScope) => dateScope switch
    {
        "today" => item.DayGroup == "today",
        "nextBusinessDay" => item.DayGroup == "nextBusinessDay",
        _ => item.DayGroup is "today" or "nextBusinessDay"
    };

    private static IReadOnlyList<EarningsNewsEvidenceResponse> FilterNews(
        IReadOnlyList<EarningsNewsEvidenceResponse> news,
        IReadOnlyList<EarningsCalendarItemResponse> items)
    {
        var tickers = items.Select(item => item.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return news.Where(item =>
                item.Tickers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(tickers.Contains))
            .ToArray();
    }

    private static PreviousEarningsResultResponse? MapPreviousEarnings(EarningsCalendarEvent? previous)
    {
        if (previous is null)
        {
            return null;
        }

        var eps = EarningsEpsOutcomeClassifier.Classify(previous);
        return new PreviousEarningsResultResponse(
            previous.ReportDateExchange,
            previous.ScheduledAtUtc,
            FormatReleaseWindow(previous.ReleaseWindow),
            eps.Estimate,
            eps.Actual,
            eps.SurprisePercent,
            eps.Outcome.ToString(),
            EarningsEpsOutcomeClassifier.Format(eps.Outcome),
            previous.RevenueEstimateMillions,
            previous.RevenueActualMillions,
            previous.RevenueSurprisePercent,
            previous.OneDayPriceReactionPercent,
            previous.Provider,
            previous.SourceUrl,
            previous.ProviderReceivedAtUtc);
    }

    private static bool MatchesFilter(EarningsCalendarItemResponse item, EarningsCalendarFilter filter)
    {
        var releaseMatches = filter.ReleaseWindow is null ||
            item.ReleaseWindow.Equals(filter.ReleaseWindow.ToString(), StringComparison.OrdinalIgnoreCase);
        return releaseMatches && EarningsCalendarFilter.MatchesMarketCap(
            item.MarketCapMillions,
            filter.MarketCapBand);
    }

    private static EarningsFilterStateResponse BuildFilterState(
        IReadOnlyList<EarningsCalendarItemResponse> items,
        EarningsCalendarFilter filter)
    {
        // For session filter counts, apply the current market cap filter.
        var itemsForSessions = items
            .Where(item => EarningsCalendarFilter.MatchesMarketCap(item.MarketCapMillions, filter.MarketCapBand))
            .ToArray();

        // For market cap filter counts, apply the current session filter.
        var itemsForCaps = items
            .Where(item => filter.ReleaseWindow is null || 
                           item.ReleaseWindow.Equals(filter.ReleaseWindow.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var sessions = new[]
        {
            new EarningsFilterOptionResponse("all", "All sessions", itemsForSessions.Length),
            SessionOption(itemsForSessions, EarningsReleaseWindow.BeforeMarketOpen, "Pre-market"),
            SessionOption(itemsForSessions, EarningsReleaseWindow.DuringMarket, "Market hours"),
            SessionOption(itemsForSessions, EarningsReleaseWindow.AfterMarketClose, "Post-market")
        };
        var caps = new[]
        {
            new EarningsFilterOptionResponse("all", "All caps", itemsForCaps.Length),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.Under2B, "Under $2B"),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.AtLeast2B, "$2B+"),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.AtLeast10B, "$10B+"),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.AtLeast50B, "$50B+"),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.AtLeast100B, "$100B+"),
            MarketCapOption(itemsForCaps, EarningsMarketCapBand.Unknown, "Cap unknown")
        };
        return new EarningsFilterStateResponse(
            EarningsCalendarFilter.SessionValue(filter.ReleaseWindow),
            EarningsCalendarFilter.MarketCapValue(filter.MarketCapBand),
            sessions,
            caps,
            SectorOptions(items.Where(item => MatchesFilter(item, filter)).ToArray()));
    }

    private static EarningsFilterOptionResponse SessionOption(
        IReadOnlyList<EarningsCalendarItemResponse> items,
        EarningsReleaseWindow releaseWindow,
        string label) => new(
            EarningsCalendarFilter.SessionValue(releaseWindow),
            label,
            items.Count(item => item.ReleaseWindow.Equals(releaseWindow.ToString(), StringComparison.OrdinalIgnoreCase)));

    private static EarningsFilterOptionResponse MarketCapOption(
        IReadOnlyList<EarningsCalendarItemResponse> items,
        EarningsMarketCapBand band,
        string label) => new(
            EarningsCalendarFilter.MarketCapValue(band),
            label,
            items.Count(item => EarningsCalendarFilter.MatchesMarketCap(item.MarketCapMillions, band)));

    // Thresholds overlap, so a row is labelled with the largest one it reaches.
    private static EarningsMarketCapBand ResolveMarketCapBand(decimal? marketCapMillions) =>
        new[] { EarningsMarketCapBand.AtLeast100B, EarningsMarketCapBand.AtLeast50B, EarningsMarketCapBand.AtLeast10B,
                EarningsMarketCapBand.AtLeast2B, EarningsMarketCapBand.Under2B }
            .FirstOrDefault(band => EarningsCalendarFilter.MatchesMarketCap(marketCapMillions, band), EarningsMarketCapBand.Unknown);

    // Sector counts follow the session and market-cap filters; rows without a Finviz sector count as unknown.
    private static IReadOnlyList<EarningsFilterOptionResponse> SectorOptions(IReadOnlyList<EarningsCalendarItemResponse> items) =>
        new[] { new EarningsFilterOptionResponse("all", "All sectors", items.Count) }
            .Concat(items.GroupBy(item => item.Sector ?? EarningsViewService.UnknownSector, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key == EarningsViewService.UnknownSector).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new EarningsFilterOptionResponse(group.Key,
                    group.Key == EarningsViewService.UnknownSector ? "Sector unknown" : group.Key, group.Count())))
            .ToArray();

    private static string FormatReleaseWindow(EarningsReleaseWindow window) => window switch
    {
        EarningsReleaseWindow.BeforeMarketOpen => "Before market open",
        EarningsReleaseWindow.DuringMarket => "During market hours",
        EarningsReleaseWindow.AfterMarketClose => "After market close",
        _ => "Time not confirmed"
    };

    private static string FormatBreakoutAssessment(EarningsBreakoutAssessment assessment) => assessment switch
    {
        EarningsBreakoutAssessment.AwaitingRelease => "AwaitingEarningsRelease",
        EarningsBreakoutAssessment.InsufficientData => "BreakoutDataInsufficient",
        EarningsBreakoutAssessment.Possible => "PossibleBreakout",
        _ => "BreakoutNotConfirmed"
    };
}
