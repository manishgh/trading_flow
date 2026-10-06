using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

/// <summary>
/// Filterable news timeline.
/// </summary>
/// <remarks>
/// Every filter here reads a field the news record already carried. The dimensions
/// existed in the data long before this screen; they simply had no UI.
/// </remarks>
public sealed class NewsModel(
    NewsQueryService news,
    NewsStockSourceService stockSources) : PageModel
{
    /// <summary>Sentiment bands. Thresholds match the labels the feed already emits.</summary>
    public static IReadOnlyList<(string Key, string Label)> SentimentBands { get; } =
    [
        ("all", "All sentiment"),
        ("positive", "Positive"),
        ("neutral", "Neutral"),
        ("negative", "Negative")
    ];

    public static IReadOnlyList<(int Hours, string Label)> Windows { get; } =
    [
        (1, "Last hour"),
        (4, "Last 4 hours"),
        (12, "Last 12 hours"),
        (24, "Last 24 hours"),
        (72, "Last 3 days")
    ];

    [BindProperty(SupportsGet = true)] public int Hours { get; set; } = 4;
    [BindProperty(SupportsGet = true)] public string? Ticker { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Provider { get; set; }
    [BindProperty(SupportsGet = true)] public string Sentiment { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public Guid? GroupId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Source { get; set; }
    [BindProperty(SupportsGet = true)] public string? Classification { get; set; }
    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    [BindProperty(SupportsGet = true)] public string? StockSource { get; set; }
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "timeline";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 50;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalBeforeFilters / (double)PageSize));
    [BindProperty(SupportsGet = true)] public bool LinkedOnly { get; set; }

    /// <summary>
    /// Set by the Clear control so the page knows the operator just reset the filters
    /// and can put focus back on the first one instead of dropping it on the body.
    /// </summary>
    [BindProperty(SupportsGet = true)] public bool Cleared { get; set; }

    public IReadOnlyList<MobileNewsItem> Items { get; private set; } = [];
    public IReadOnlyList<string> Providers { get; private set; } = [];
    public IReadOnlyList<TradingFlow.Web.Services.Desk.DeskSource> StockSources { get; private set; } = [];
    public int TotalBeforeFilters { get; private set; }
    public string FeedDescription { get; private set; } = String.Empty;

    /// <summary>False when the feed itself is unavailable, as opposed to merely empty.</summary>
    public bool FeedEnabled { get; private set; } = true;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        // Reject failed conversions of bound query values without treating unrelated
        // page-model validation (for example, unset result fields) as an invalid request.
        var typedFilters = new[] { nameof(GroupId), nameof(Hours), nameof(PageNumber), nameof(PageSize), nameof(LinkedOnly), nameof(Cleared) };
        if (ModelState.Any(x => typedFilters.Contains(x.Key, StringComparer.OrdinalIgnoreCase) && x.Value?.Errors.Count > 0))
            return BadRequest("Choose valid news filters and stock-source identifiers.");
        Hours = Math.Clamp(Hours, 1, 72);
        PageSize = Math.Clamp(PageSize, 10, 200);
        PageNumber = Math.Clamp(PageNumber, 1, Int32.MaxValue / PageSize);
        if (View is not ("timeline" or "ticker")) return BadRequest("Choose timeline or ticker view.");
        MobileNewsFeedResponse feed;
        try { feed = await news.QueryAsync(new NewsHubQuery { Hours = Hours, Ticker = Ticker,
            GroupId = GroupId, Scope = Scope, StockSource = StockSource, Provider = Provider, Source = Source,
            Classification = Classification, Sentiment = Sentiment, Search = Search,
            LinkedOnly = LinkedOnly, Page = PageNumber, PageSize = PageSize }, User, cancellationToken); }
        catch (InvalidOperationException error) { return BadRequest(error.Message); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        StockSource = NewsQueryService.StockSourceKey(new() { GroupId = GroupId, Scope = Scope, StockSource = StockSource });
        if (User.Identity?.IsAuthenticated == true) StockSources = await stockSources.CatalogAsync(cancellationToken);
        GroupId = null;
        Scope = null;
        FeedDescription = feed.Message ?? String.Empty;
        FeedEnabled = feed.Enabled;
        PageNumber = feed.Page;
        TotalBeforeFilters = feed.TotalMatching ?? feed.Items.Count;
        Providers = feed.Items.Select(x => x.Provider ?? "").Distinct().ToArray();
        Items = View == "ticker" ? feed.Items.OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Timestamp).ToArray() : feed.Items;
        return Page();
    }

    /// <summary>Label for a sentiment score, so colour is never the only signal.</summary>
    public static string SentimentLabel(decimal score) =>
        score > 0.15m ? "Positive" : score < -0.15m ? "Negative" : "Neutral";

    /// <summary>Semantic class for a sentiment score.</summary>
    public static string SentimentClass(decimal score) =>
        score > 0.15m ? "value-good" : score < -0.15m ? "value-bad" : "value-flat";
}
