using System.Globalization;
using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

public static class EarningsApiEndpoints
{
    public static IEndpointRouteBuilder MapTradingFlowEarningsApi(this IEndpointRouteBuilder endpoints)
    {
        MapGroup(endpoints.MapGroup("/api/earnings"));
        MapGroup(endpoints.MapGroup("/api/mobile/earnings"));
        return endpoints;
    }

    private static void MapGroup(RouteGroupBuilder group)
    {
        group.MapGet("/today-next-business-day", async (
            string? session,
            string? marketCap,
            EarningsMonitor monitor,
            NewsFeedService newsFeed,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!EarningsCalendarFilter.TryParse(session, marketCap, out var filter, out var filterError))
            {
                return Results.BadRequest(filterError);
            }

            var nowUtc = clock.GetUtcNow();
            var operatorToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(nowUtc, EarningsTimeZones.OperatorLocal).DateTime);
            var nextBusinessDate = EarningsCalendarDates.NextWeekday(operatorToday);
            var snapshot = await monitor.GetCalendarAsync(
                operatorToday.AddDays(-1),
                nextBusinessDate.AddDays(1),
                cancellationToken);
            var news = await newsFeed.GetEarningsTimelineAsync(
                snapshot.Items.Select(item => item.Event.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                nowUtc,
                cancellationToken);
            return Results.Ok(MapSnapshot(
                snapshot,
                monitor.GetStatus(),
                operatorToday,
                nextBusinessDate,
                news,
                onlyShortHorizon: true,
                filter));
        });

        group.MapGet("/calendar", async (
            string from,
            string to,
            string? session,
            string? marketCap,
            EarningsMonitor monitor,
            NewsFeedService newsFeed,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
                !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate) ||
                toDate < fromDate)
            {
                return Results.BadRequest("from and to must be valid yyyy-MM-dd dates with to >= from.");
            }

            if (!EarningsCalendarFilter.TryParse(session, marketCap, out var filter, out var filterError))
            {
                return Results.BadRequest(filterError);
            }

            var snapshot = await monitor.GetCalendarAsync(fromDate, toDate, cancellationToken);
            var operatorToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(snapshot.GeneratedAtUtc, EarningsTimeZones.OperatorLocal).DateTime);
            var nowUtc = clock.GetUtcNow();
            var news = await newsFeed.GetEarningsTimelineAsync(
                snapshot.Items.Select(item => item.Event.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                nowUtc,
                cancellationToken);
            return Results.Ok(MapSnapshot(
                snapshot,
                monitor.GetStatus(),
                operatorToday,
                EarningsCalendarDates.NextWeekday(operatorToday),
                news,
                onlyShortHorizon: false,
                filter));
        });

        group.MapGet("/status", (EarningsMonitor monitor) => Results.Ok(monitor.GetStatus()));

        group.MapPost("/refresh", async (
            EarningsMonitor monitor,
            NewsFeedService newsFeed,
            CancellationToken cancellationToken) =>
        {
            await Task.WhenAll(
                monitor.RefreshAsync(forceCalendarRefresh: true, cancellationToken),
                newsFeed.RefreshOnceAsync(cancellationToken));
            return Results.Ok(monitor.GetStatus());
        });
    }

    private static EarningsCalendarResponse MapSnapshot(
        EarningsCalendarSnapshot snapshot,
        EarningsMonitorStatus monitorStatus,
        DateOnly operatorToday,
        DateOnly nextBusinessDate,
        IReadOnlyList<EarningsNewsEvidenceResponse> news,
        bool onlyShortHorizon,
        EarningsCalendarFilter filter)
    {
        var mapped = snapshot.Items
            .Select(item => MapItem(item, operatorToday, nextBusinessDate))
            .Where(item => !onlyShortHorizon || item.DayGroup is "today" or "nextBusinessDay")
            .ToArray();
        var filtered = mapped.Where(item => MatchesFilter(item, filter)).ToArray();
        var items = filtered
            .Where(item => item.ScheduledAtUtc >= snapshot.GeneratedAtUtc)
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenBy(item => item.Ticker, StringComparer.Ordinal)
            .Concat(mapped
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
            monitorStatus.IsRunning,
            (int)monitorStatus.AnalysisInterval.TotalSeconds,
            monitorStatus.LastAnalysisUtc,
            newsWindow.StartUtc,
            newsWindow.EndUtc,
            newsWindow.Label,
            BuildFilterState(mapped, filter),
            news,
            items);
    }

    private static EarningsCalendarItemResponse MapItem(
        EarningsCalendarSnapshotItem item,
        DateOnly operatorToday,
        DateOnly nextBusinessDate)
    {
        var calendarEvent = item.Event;
        var analysis = item.Analysis;
        var resultAssessment = analysis?.ResultAssessment ?? EarningsResultAssessment.Unknown;
        var breakoutAssessment = analysis?.BreakoutAssessment ?? EarningsBreakoutAssessment.InsufficientData;
        var eps = EarningsEpsOutcomeClassifier.Classify(calendarEvent);
        var operatorTime = TimeZoneInfo.ConvertTime(calendarEvent.ScheduledAtUtc, EarningsTimeZones.OperatorLocal);
        var newYork = TimeZoneInfo.ConvertTime(calendarEvent.ScheduledAtUtc, EarningsTimeZones.NewYork);
        var localDate = DateOnly.FromDateTime(operatorTime.DateTime);
        var dayGroup = localDate == operatorToday
            ? "today"
            : localDate == nextBusinessDate
                ? "nextBusinessDay"
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
            calendarEvent.RevenueEstimateMillions,
            calendarEvent.RevenueActualMillions,
            calendarEvent.RevenueSurprisePercent,
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
            MapPreviousEarnings(item.PreviousReportedEvent));
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
        var sessions = new[]
        {
            new EarningsFilterOptionResponse("all", "All sessions", items.Count),
            SessionOption(items, EarningsReleaseWindow.BeforeMarketOpen, "Pre-market"),
            SessionOption(items, EarningsReleaseWindow.DuringMarket, "Market hours"),
            SessionOption(items, EarningsReleaseWindow.AfterMarketClose, "Post-market")
        };
        var caps = new[]
        {
            new EarningsFilterOptionResponse("all", "All caps", items.Count),
            MarketCapOption(items, EarningsMarketCapBand.Under10B, "Under $10B"),
            MarketCapOption(items, EarningsMarketCapBand.From10BTo50B, "$10B-$50B"),
            MarketCapOption(items, EarningsMarketCapBand.From50BTo100B, "$50B-$100B"),
            MarketCapOption(items, EarningsMarketCapBand.AtLeast100B, "$100B+"),
            MarketCapOption(items, EarningsMarketCapBand.Unknown, "Cap unknown")
        };
        return new EarningsFilterStateResponse(
            EarningsCalendarFilter.SessionValue(filter.ReleaseWindow),
            EarningsCalendarFilter.MarketCapValue(filter.MarketCapBand),
            sessions,
            caps);
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

    private static EarningsMarketCapBand ResolveMarketCapBand(decimal? marketCapMillions)
    {
        foreach (var band in Enum.GetValues<EarningsMarketCapBand>().Where(value => value != EarningsMarketCapBand.All))
        {
            if (EarningsCalendarFilter.MatchesMarketCap(marketCapMillions, band))
            {
                return band;
            }
        }

        return EarningsMarketCapBand.Unknown;
    }

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
