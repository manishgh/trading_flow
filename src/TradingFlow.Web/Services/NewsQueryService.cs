using System.Security.Claims;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

public sealed class NewsHubQuery
{
    public int? Hours { get; set; }
    public string? Ticker { get; set; }
    public string? Provider { get; set; }
    public string? Source { get; set; }
    public string? Classification { get; set; }
    public string? Sentiment { get; set; }
    public string? Search { get; set; }
    public bool? LinkedOnly { get; set; }
    public Guid? GroupId { get; set; }
    public string? Scope { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

/// <summary>Shared public market filters; private scopes require a signed-in operator.</summary>
public sealed class NewsQueryService(INewsFeedRepository news, IWishlistRepository groups,
    IDeskAccountReader account, TimeProvider clock)
{
    public async Task<MobileNewsFeedResponse> QueryAsync(NewsHubQuery query, ClaimsPrincipal user, CancellationToken token)
    {
        query.Scope = query.Scope?.Trim().ToLowerInvariant();
        query.Sentiment = query.Sentiment?.Trim().ToLowerInvariant();
        query.Classification = query.Classification?.Trim().ToLowerInvariant();
        if (query.Scope is not (null or "" or "all" or "groups" or "positions") ||
            query.Sentiment is not (null or "" or "all" or "positive" or "negative" or "neutral") ||
            query.Classification is not (null or "" or "company" or "sec filing" or "fed" or "macro" or "market"))
            throw new InvalidOperationException("Choose a supported news scope, sentiment and evidence classification.");
        if (query.Search?.Length > 200 || query.Provider?.Length > 50 || query.Source?.Length > 200)
            throw new InvalidOperationException("News search or provider/source filter is too long.");
        IReadOnlyList<string>? symbols = null;
        var privateScope = query.GroupId.HasValue || query.Scope is "groups" or "positions";
        if (privateScope && user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();
        if (query.GroupId is { } id) {
            var group = await groups.GetByIdAsync(id, token);
            symbols = group?.Items.Where(x => x.Active).Select(x => x.Ticker).Distinct().ToArray() ?? [];
        } else if (query.Scope == "groups") {
            symbols = (await groups.ListAsync(token)).SelectMany(x => x.Items).Where(x => x.Active).Select(x => x.Ticker).Distinct().ToArray();
        } else if (query.Scope == "positions") {
            var portfolio = await account.ReadAsync(token);
            if (!portfolio.Available) return new(false, "stored", portfolio.Detail, []);
            symbols = portfolio.Holdings.Where(x => x.Quantity != 0).Select(x => x.Symbol).Distinct().ToArray();
        }
        var hours = Math.Clamp(query.Hours ?? 4, 1, 72);
        var size = Math.Clamp(query.PageSize ?? 50, 10, 200);
        var number = Math.Clamp(query.Page ?? 1, 1, Int32.MaxValue / size);
        var filter = new NewsFeedQuery(clock.GetUtcNow().AddHours(-hours), query.Ticker,
            symbols, query.Provider, query.Source, query.Classification, query.Sentiment, query.Search,
            query.LinkedOnly == true, (number - 1) * size, size);
        var page = await news.QueryAsync(filter, token);
        var lastPage = Math.Max(1, (int)Math.Ceiling(page.TotalMatching / (double)size));
        if (number > lastPage) {
            number = lastPage;
            page = await news.QueryAsync(filter with { Skip = (number - 1) * size }, token);
        }
        var items = page.Records.GroupBy(x => NewsFeedService.NormalizeHeadlineIdentity(x.Headline), StringComparer.Ordinal)
            .Select(group => {
                var item = group.First();
                return new MobileNewsItem(NewsFeedService.FormatRelatedTickers(group.Select(x => x.Ticker)), item.Timestamp,
                    item.Headline, item.SentimentScore, item.Provider, item.Source, item.Url, item.Summary,
                    NewsEvidenceClassification.Classify(item));
            }).ToArray();
        return new(true, "stored", $"Stored {hours}h market feed. Matching records are counted before duplicate articles on each page are combined.",
            items, page.TotalMatching, number, size);
    }
}
