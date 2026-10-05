using System.Security.Claims;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

public sealed class EarningsViewQuery
{
    public string? Search { get; set; }
    public string? Schedule { get; set; }
    public string? Result { get; set; }
    public decimal? MinMarketCap { get; set; }
    public decimal? MaxMarketCap { get; set; }
    public decimal? MinEventReturn { get; set; }
    public decimal? MaxEventReturn { get; set; }
    public decimal? MinPriceReaction { get; set; }
    public decimal? MaxPriceReaction { get; set; }
    public decimal? MinEpsEstimate { get; set; }
    public decimal? MaxEpsEstimate { get; set; }
    public decimal? MinEpsActual { get; set; }
    public decimal? MaxEpsActual { get; set; }
    public decimal? MinRevenueEstimate { get; set; }
    public decimal? MaxRevenueEstimate { get; set; }
    public decimal? MinRevenueActual { get; set; }
    public decimal? MaxRevenueActual { get; set; }
    public string? Provider { get; set; }
    public string? NewsProvider { get; set; }
    public string? Day { get; set; }
    public string? Breakout { get; set; }
    public string? Assessment { get; set; }
    public decimal? MinEpsSurprise { get; set; }
    public decimal? MaxEpsSurprise { get; set; }
    public decimal? MinSurprise { get; set; }
    public decimal? MinRevenueSurprise { get; set; }
    public decimal? MaxRevenueSurprise { get; set; }
    public decimal? MinRvol { get; set; }
    public decimal? MaxRvol { get; set; }
    public decimal? MinSentiment { get; set; }
    public decimal? MaxSentiment { get; set; }
    public Guid? GroupId { get; set; }
    public bool? OnlyGroups { get; set; }
    public bool? OnlyHeld { get; set; }
    public bool? HasNews { get; set; }
    public string? Sort { get; set; }
}

public sealed class EarningsViewService(IWishlistRepository groups, IDeskAccountReader account)
{
    public async Task<EarningsCalendarResponse> FilterAsync(EarningsCalendarResponse response, EarningsViewQuery query,
        ClaimsPrincipal user, CancellationToken token)
    {
        query.Assessment = query.Assessment?.Trim().ToLowerInvariant();
        query.Day = query.Day?.Trim().ToLowerInvariant();
        query.Breakout = query.Breakout?.Trim().ToLowerInvariant();
        query.Sort = query.Sort?.Trim().ToLowerInvariant();
        query.Schedule = query.Schedule?.Trim().ToLowerInvariant(); query.Result = query.Result?.Trim().ToLowerInvariant();
        if (query.Search?.Length > 200 || query.Schedule is not (null or "" or "all" or "estimated" or "reported") ||
            query.Result is not (null or "" or "all" or "observed" or "pending"))
            throw new InvalidOperationException("Use a ticker/company search of at most 200 characters and supported schedule/result states.");
        if (query.Assessment is not (null or "" or "all" or "beat" or "miss" or "inline" or "met" or "pending") ||
            query.Day is not (null or "" or "all" or "today" or "nextbusinessday" or "other") ||
            query.Breakout is not (null or "" or "all" or "awaitingearningsrelease" or "breakoutdatainsufficient" or "possiblebreakout" or "breakoutnotconfirmed") ||
            query.Sort is not (null or "" or "time" or "cap" or "surprise" or "move" or "rvol" or "revenue"))
            throw new InvalidOperationException("Choose supported earnings filters and sort order.");
        foreach (var pair in new[] { (query.MinEpsSurprise, query.MaxEpsSurprise), (query.MinRevenueSurprise, query.MaxRevenueSurprise),
                     (query.MinRvol, query.MaxRvol), (query.MinSentiment, query.MaxSentiment),
                     (query.MinMarketCap,query.MaxMarketCap),(query.MinEventReturn,query.MaxEventReturn),(query.MinPriceReaction,query.MaxPriceReaction),
                     (query.MinEpsEstimate,query.MaxEpsEstimate),(query.MinEpsActual,query.MaxEpsActual),
                     (query.MinRevenueEstimate,query.MaxRevenueEstimate),(query.MinRevenueActual,query.MaxRevenueActual) })
            if (pair.Item1 > pair.Item2) throw new InvalidOperationException("Minimum filters must not exceed maximum filters.");
        if (query.MinRvol < 0 || query.MaxRvol < 0 || query.MinSurprise < 0 ||
            query.MinSentiment < -1 || query.MinSentiment > 1 || query.MaxSentiment < -1 || query.MaxSentiment > 1)
            throw new InvalidOperationException("Volume and absolute surprise must be nonnegative; sentiment is between −1 and 1.");
        if (query.MinMarketCap < 0 || query.MaxMarketCap < 0 || query.MinRevenueEstimate < 0 || query.MaxRevenueEstimate < 0 ||
            query.MinRevenueActual < 0 || query.MaxRevenueActual < 0) throw new InvalidOperationException("Cap and revenue amounts in USD millions cannot be negative.");
        HashSet<string>? symbols = null;
        if (query.GroupId.HasValue || query.OnlyGroups == true || query.OnlyHeld == true) {
            if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();
            if (query.GroupId is { } id) {
                var group = await groups.GetByIdAsync(id, token);
                symbols = (group?.Items ?? []).Where(x => x.Active).Select(x => x.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
            } else if (query.OnlyGroups == true)
                symbols = (await groups.ListAsync(token)).SelectMany(x => x.Items).Where(x => x.Active).Select(x => x.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (query.OnlyHeld == true) {
                var portfolio = await account.ReadAsync(token);
                if (!portfolio.Available) throw new InvalidOperationException(portfolio.Detail);
                var held = portfolio.Holdings.Where(x => x.Quantity != 0).Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (symbols is null) symbols = held; else symbols.IntersectWith(held);
            }
        }
        var rows = response.Items.Where(x => symbols is null || symbols.Contains(x.Ticker))
            .Where(x => String.IsNullOrWhiteSpace(query.Search) || x.Ticker.Contains(query.Search.Trim(),StringComparison.OrdinalIgnoreCase) || (x.CompanyName ?? "").Contains(query.Search.Trim(),StringComparison.OrdinalIgnoreCase))
            .Where(x => query.Schedule is null or "" or "all" || (query.Schedule == "estimated" ? x.IsScheduleEstimate : !x.IsScheduleEstimate))
            .Where(x => query.Result is null or "" or "all" || (query.Result == "observed" ? HasObservedResult(x) : !HasObservedResult(x)))
            .Where(x => Range(x.MarketCapMillions,query.MinMarketCap,query.MaxMarketCap) && Range(x.EventReturnPercent,query.MinEventReturn,query.MaxEventReturn) &&
                Range(x.ProviderOneDayPriceReactionPercent,query.MinPriceReaction,query.MaxPriceReaction) && Range(x.EpsEstimate,query.MinEpsEstimate,query.MaxEpsEstimate) &&
                Range(x.EpsActual,query.MinEpsActual,query.MaxEpsActual) && Range(x.RevenueEstimateMillions,query.MinRevenueEstimate,query.MaxRevenueEstimate) &&
                Range(x.RevenueActualMillions,query.MinRevenueActual,query.MaxRevenueActual))
            .Where(x => Matches(query.Provider, x.Provider) && Matches(query.NewsProvider, x.NewsProvider))
            .Where(x => Matches(query.Day, x.DayGroup) && Matches(query.Breakout, x.BreakoutAssessment))
            .Where(x => query.Assessment == "pending" ? x.EpsActual is null : Matches(query.Assessment == "inline" ? "Met" : query.Assessment, x.EpsOutcome))
            .Where(x => query.HasNews != true || !String.IsNullOrWhiteSpace(x.NewsHeadline))
            .Where(x => Range(x.EpsSurprisePercent, query.MinEpsSurprise, query.MaxEpsSurprise) &&
                Range(x.RevenueSurprisePercent, query.MinRevenueSurprise, query.MaxRevenueSurprise) &&
                Range(x.SlotRelativeVolume, query.MinRvol, query.MaxRvol) && Range(x.NewsSentiment, query.MinSentiment, query.MaxSentiment))
            .Where(x => query.MinSurprise is null || x.EpsSurprisePercent is { } surprise && Math.Abs(surprise) >= query.MinSurprise);
        rows = query.Sort switch {
            "cap" => rows.OrderByDescending(x => x.MarketCapMillions),
            "surprise" => rows.OrderByDescending(x => x.EpsSurprisePercent is { } surprise ? Math.Abs(surprise) : (decimal?)null),
            "move" => rows.OrderByDescending(x => x.EventReturnPercent),
            "rvol" => rows.OrderByDescending(x => x.SlotRelativeVolume),
            "revenue" => rows.OrderByDescending(x => x.RevenueSurprisePercent),
            "time" => rows.OrderBy(x => x.ScheduledAtUtc),
            _ => rows
        };
        var ordered = rows is IOrderedEnumerable<EarningsCalendarItemResponse> sorted
            ? sorted.ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToArray() : rows.ToArray();
        return response with { Items = ordered.Take(2000).ToArray(), TotalMatching = ordered.Length, Truncated = ordered.Length > 2000 };
    }
    private static bool Matches(string? filter, string? value) => String.IsNullOrWhiteSpace(filter) || filter == "all" ||
        String.Equals(filter.Trim(), value, StringComparison.OrdinalIgnoreCase);
    private static bool Range(decimal? value, decimal? min, decimal? max) => min is null && max is null ||
        value is { } known && (min is null || known >= min) && (max is null || known <= max);
    private static bool HasObservedResult(EarningsCalendarItemResponse row) => row.EpsActual is not null || row.RevenueActualMillions is not null || row.ResultFirstSeenAtUtc is not null;
}
