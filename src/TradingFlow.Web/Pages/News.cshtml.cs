using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
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
    NewsFeedService news,
    IWishlistRepository wishlists) : PageModel
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
    [BindProperty(SupportsGet = true)] public bool LinkedOnly { get; set; }

    /// <summary>
    /// Set by the Clear control so the page knows the operator just reset the filters
    /// and can put focus back on the first one instead of dropping it on the body.
    /// </summary>
    [BindProperty(SupportsGet = true)] public bool Cleared { get; set; }

    public IReadOnlyList<MobileNewsItem> Items { get; private set; } = [];
    public IReadOnlyList<string> Providers { get; private set; } = [];
    public IReadOnlyList<Wishlist> Groups { get; private set; } = [];
    public int TotalBeforeFilters { get; private set; }
    public string FeedDescription { get; private set; } = String.Empty;

    /// <summary>False when the feed itself is unavailable, as opposed to merely empty.</summary>
    public bool FeedEnabled { get; private set; } = true;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Hours = Math.Clamp(Hours, 1, 72);
        Groups = await wishlists.ListAsync(cancellationToken);

        var feed = await news.GetRollingAsync(Hours, NormalizeTicker(Ticker), cancellationToken);
        FeedDescription = feed.Message ?? String.Empty;
        FeedEnabled = feed.Enabled;
        var all = feed.Items;
        TotalBeforeFilters = all.Count;

        // Provider options come from what actually arrived, so the list never offers
        // a filter that would return nothing.
        Providers = all
            .Select(item => item.Provider)
            .Where(provider => !String.IsNullOrWhiteSpace(provider))
            .Select(provider => provider!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var groupTickers = await ResolveGroupTickersAsync(cancellationToken);

        Items = all
            .Where(MatchesProvider)
            .Where(MatchesSentiment)
            .Where(MatchesSearch)
            .Where(item => !LinkedOnly || !String.IsNullOrWhiteSpace(item.Url))
            .Where(item => groupTickers is null || MentionsAny(item, groupTickers))
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
    }

    private async Task<HashSet<string>?> ResolveGroupTickersAsync(CancellationToken cancellationToken)
    {
        if (GroupId is not { } id)
        {
            return null;
        }

        var group = await wishlists.GetByIdAsync(id, cancellationToken);
        if (group is null)
        {
            return null;
        }

        return group.Items
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool MentionsAny(MobileNewsItem item, HashSet<string> tickers)
    {
        // A story can be tagged with several symbols; the feed joins them with commas.
        return item.Ticker
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tickers.Contains);
    }

    private bool MatchesProvider(MobileNewsItem item) =>
        String.IsNullOrWhiteSpace(Provider) ||
        String.Equals(item.Provider?.Trim(), Provider.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool MatchesSentiment(MobileNewsItem item) => Sentiment?.ToLowerInvariant() switch
    {
        "positive" => item.SentimentScore > 0.15m,
        "negative" => item.SentimentScore < -0.15m,
        "neutral" => item.SentimentScore is >= -0.15m and <= 0.15m,
        _ => true
    };

    private bool MatchesSearch(MobileNewsItem item)
    {
        if (String.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        var term = Search.Trim();
        return item.Headline.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (item.Summary?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
            item.Ticker.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeTicker(string? value) =>
        String.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <summary>Label for a sentiment score, so colour is never the only signal.</summary>
    public static string SentimentLabel(decimal score) =>
        score > 0.15m ? "Positive" : score < -0.15m ? "Negative" : "Neutral";

    /// <summary>Semantic class for a sentiment score.</summary>
    public static string SentimentClass(decimal score) =>
        score > 0.15m ? "value-good" : score < -0.15m ? "value-bad" : "value-flat";
}
