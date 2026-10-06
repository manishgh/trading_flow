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
    public string? StockSource { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

/// <summary>Shared public market filters; private scopes require a signed-in operator.</summary>
public sealed class NewsQueryService(INewsFeedRepository news, IWishlistRepository groups,
    IDeskAccountReader account, TimeProvider clock, NewsStockSourceService? stockSources = null)
{
    // Scoped to one request: the Razor membership list uses the same observation as its news filter.
    public NewsStockSelection? StockSelection { get; private set; }
    public static string StockSourceKey(NewsHubQuery query)
    {
        var key = query.StockSource?.Trim().ToLowerInvariant();
        if (query.GroupId.HasValue && query.Scope?.Trim().ToLowerInvariant() is "positions")
            throw new InvalidOperationException("Choose one stock source; the wishlist and scope filters conflict.");
        var legacy = query.GroupId is { } id ? "wishlist:" + id : query.Scope?.Trim().ToLowerInvariant() switch
        { "groups" => "wishlists", "positions" => "positions", _ => "all" };
        if (!String.IsNullOrEmpty(key) && legacy != "all" && key != legacy)
            throw new InvalidOperationException("Choose one stock source; the wishlist and scope filters conflict.");
        return String.IsNullOrEmpty(key) ? legacy : key;
    }
    public async Task<MobileNewsFeedResponse> QueryAsync(NewsHubQuery query, ClaimsPrincipal user, CancellationToken token)
    {
        StockSelection = null;
        query.Scope = query.Scope?.Trim().ToLowerInvariant();
        query.Sentiment = query.Sentiment?.Trim().ToLowerInvariant();
        query.Classification = query.Classification?.Trim().ToLowerInvariant();
        if (query.Scope is not (null or "" or "all" or "groups" or "positions") ||
            query.Sentiment is not (null or "" or "all" or "positive" or "negative" or "neutral") ||
            query.Classification is not (null or "" or "company" or "sec filing" or "fed" or "macro" or "market"))
            throw new InvalidOperationException("Choose a supported news scope, sentiment and evidence classification.");
        if (query.Search?.Length > 200 || query.Provider?.Length > 50 || query.Source?.Length > 200)
            throw new InvalidOperationException("News search or provider/source filter is too long.");
        if (!String.IsNullOrWhiteSpace(query.Ticker)) {
            query.Ticker = query.Ticker.Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(query.Ticker, @"\A[A-Z0-9][A-Z0-9.\-]{0,15}\z"))
                throw new InvalidOperationException("Enter one valid stock ticker.");
        }
        IReadOnlyList<string>? symbols = null;
        var stockSource = StockSourceKey(query);
        if (stockSource.Length > 100) throw new InvalidOperationException("Stock source is too long.");
        var privateScope = stockSource != "all";
        if (privateScope && user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException();
        string? stockDescription = null;
        if ((query.StockSource is not null || stockSources is not null) && stockSource != "all" && stockSource != "positions") {
            if (stockSources is null) throw new InvalidOperationException("Stock sources are unavailable.");
            var selection = await stockSources.ResolveAsync(stockSource, token);
            if (!selection.Available) return new(false, "stored", selection.Description, []);
            StockSelection = selection;
            symbols = selection.Symbols;
            stockDescription = selection.Description;
        } else if (stockSource == "all") { }
        else if (query.GroupId is { } id) {
            var group = await groups.GetByIdAsync(id, token);
            if (group is null) throw new InvalidOperationException("This wishlist no longer exists. Choose another stock source.");
            symbols = group.Items.Where(x => x.Active).Select(x => x.Ticker).Distinct().ToArray();
        } else if (query.Scope == "groups") {
            symbols = (await groups.ListAsync(token)).SelectMany(x => x.Items).Where(x => x.Active).Select(x => x.Ticker).Distinct().ToArray();
        } else if (stockSource == "positions") {
            var portfolio = await account.ReadAsync(token);
            if (!portfolio.Available) return new(false, "stored", portfolio.Detail, []);
            symbols = portfolio.Holdings.Where(x => x.Quantity != 0).Select(x => x.Symbol).Distinct().ToArray();
        }
        if (!String.IsNullOrWhiteSpace(query.Ticker)) {
            var ticker = query.Ticker.Trim().ToUpperInvariant();
            symbols = (symbols ?? [ticker]).Where(x => x.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToArray();
            query.Ticker = ticker;
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
        return new(true, "stored", $"{stockDescription} Rolling {hours}h news feed. Matching records are counted before duplicate articles on each page are combined.".Trim(),
            items, page.TotalMatching, number, size);
    }
}
