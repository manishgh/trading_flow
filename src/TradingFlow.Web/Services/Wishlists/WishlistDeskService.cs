using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services.Wishlists;

/// <summary>
/// Builds the read model used by the Trade Desk and mobile wishlist screens.
/// Signal generation stays in <see cref="WishlistObserverService"/>; this service
/// only joins persisted signals, quotes, open trades, and related news into a
/// per-ticker operator view.
/// </summary>
public sealed class WishlistDeskService
{
    private readonly IWishlistRepository wishlists;
    private readonly AlpacaQuoteService quoteService;
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;
    private readonly NewsFeedService newsFeed;

    public WishlistDeskService(
        IWishlistRepository wishlists,
        AlpacaQuoteService quoteService,
        PaperJobService paperJobs,
        MobileAutomationService automation,
        NewsFeedService newsFeed)
    {
        this.wishlists = wishlists;
        this.quoteService = quoteService;
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.newsFeed = newsFeed;
    }

    public async Task<WishlistDeskSnapshot> BuildAsync(
        Wishlist? wishlist,
        string quoteFeed,
        TimeSpan signalWindow,
        TimeSpan newsWindow,
        CancellationToken cancellationToken)
    {
        if (wishlist is null)
        {
            return new WishlistDeskSnapshot([], [], [], [], 0m);
        }

        var activeItems = wishlist.Items
            .Where(item => item.Active)
            .OrderBy(item => item.Ticker)
            .ToArray();
        var tickerSet = activeItems
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var quotes = await quoteService.GetLatestQuotesAsync(tickerSet.ToArray(), quoteFeed, cancellationToken);
        var runningTrades = (await RunningTradesBuilder.BuildAsync(paperJobs, automation))
            .Where(trade => tickerSet.Contains(trade.Ticker))
            .ToArray();
        var recentSignals = await wishlists.GetSignalsAsync(
            wishlist.Id,
            ticker: null,
            DateTimeOffset.UtcNow.Subtract(signalWindow),
            limit: 100,
            cancellationToken);
        var relatedNews = await LoadRelatedNewsAsync(tickerSet, newsWindow, cancellationToken);

        var latestSignalByTicker = recentSignals
            .GroupBy(signal => signal.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(signal => signal.DetectedAtUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        var latestNewsByTicker = relatedNews
            .SelectMany(news => SplitTickerDisplay(news.Ticker).Select(ticker => (ticker, news)))
            .GroupBy(item => item.ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.news.Timestamp).First().news,
                StringComparer.OrdinalIgnoreCase);
        var tradeByTicker = runningTrades
            .GroupBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(trade => trade.UpdatedAtUtc ?? DateTimeOffset.MinValue).First(),
                StringComparer.OrdinalIgnoreCase);

        var rows = activeItems.Select(item =>
        {
            var quote = quotes.TryGetValue(item.Ticker, out var quoteValue)
                ? quoteValue
                : new AlpacaLatestQuote(item.Ticker, null, null, null, null, null);
            tradeByTicker.TryGetValue(item.Ticker, out var trade);
            latestSignalByTicker.TryGetValue(item.Ticker, out var signal);
            latestNewsByTicker.TryGetValue(item.Ticker, out var news);
            return new WishlistDeskRow(item, quote, trade, signal, news);
        }).ToArray();

        return new WishlistDeskSnapshot(rows, recentSignals, relatedNews, runningTrades, runningTrades.Sum(trade => trade.UnrealizedPl));
    }

    private async Task<IReadOnlyList<MobileNewsItem>> LoadRelatedNewsAsync(
        IReadOnlySet<string> tickerSet,
        TimeSpan newsWindow,
        CancellationToken cancellationToken)
    {
        if (tickerSet.Count == 0)
        {
            return [];
        }

        var hours = Math.Max(1, (int)Math.Ceiling(newsWindow.TotalHours));
        var feed = await newsFeed.GetRollingAsync(hours, null, cancellationToken);
        var since = DateTimeOffset.UtcNow.Subtract(newsWindow);
        return feed.Items
            .Where(item => item.Timestamp >= since)
            .Where(item => SplitTickerDisplay(item.Ticker).Any(tickerSet.Contains))
            .OrderByDescending(item => item.Timestamp)
            .Take(80)
            .ToArray();
    }

    private static IEnumerable<string> SplitTickerDisplay(string tickerDisplay)
    {
        return tickerDisplay
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0);
    }
}

public sealed record WishlistDeskSnapshot(
    IReadOnlyList<WishlistDeskRow> Rows,
    IReadOnlyList<WishlistSignal> RecentSignals,
    IReadOnlyList<MobileNewsItem> RelatedNews,
    IReadOnlyList<MobileRunningTrade> RunningTrades,
    decimal TotalPl);

public sealed record WishlistDeskRow(
    WishlistItem Item,
    AlpacaLatestQuote Quote,
    MobileRunningTrade? Trade,
    WishlistSignal? LatestSignal,
    MobileNewsItem? LatestNews)
{
    public string Ticker => Item.Ticker;

    public string DisplayName => String.IsNullOrWhiteSpace(Item.DisplayName) ? Item.Ticker : Item.DisplayName!;

    public bool HasQuote => Quote.MidPrice is not null;

    public bool HasTrade => Trade is not null;

    public bool HasSignal => LatestSignal is not null;

    public bool HasNews => LatestNews is not null;

    public string EligibilityLabel => LatestSignal is null ? "Watching" : "Eligible";

    public string EligibilityReason => LatestSignal?.Reason ?? "Waiting for VWAP/EMA/MACD/volume conditions.";
}
