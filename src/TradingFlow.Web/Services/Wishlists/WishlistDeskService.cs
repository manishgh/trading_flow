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

    public Task<WishlistDeskSnapshot> BuildAsync(
        Wishlist? wishlist,
        string quoteFeed,
        TimeSpan signalWindow,
        TimeSpan newsWindow,
        CancellationToken cancellationToken)
    {
        return wishlist is null
            ? Task.FromResult(new WishlistDeskSnapshot([], [], [], [], 0m))
            : BuildAsync(DeskUniverse.ForWishlist(wishlist), quoteFeed, signalWindow, newsWindow, cancellationToken);
    }

    /// <summary>
    /// Builds the desk view over any resolved universe: one wishlist, every
    /// wishlist, or a screener result.
    /// </summary>
    /// <remarks>
    /// Open positions are read first when the universe takes held positions, so a
    /// holding on no list still gets a quote, news and a row. Signals are read for
    /// the one wishlist when the universe is a single list, and across all lists
    /// otherwise, then narrowed to the universe's symbols.
    /// </remarks>
    public async Task<WishlistDeskSnapshot> BuildAsync(
        DeskUniverse universe,
        string quoteFeed,
        TimeSpan signalWindow,
        TimeSpan newsWindow,
        CancellationToken cancellationToken)
    {
        var runningTradesTask = RunningTradesBuilder.BuildAsync(paperJobs, automation);
        if (universe.IncludesHeldPositions)
        {
            universe = universe.WithHeldPositions((await runningTradesTask).Select(trade => trade.Ticker));
        }

        if (universe.Members.Count == 0)
        {
            var allTrades = await runningTradesTask;
            return new WishlistDeskSnapshot([], [], [], [], 0m)
            {
                Portfolio = DeskPortfolioSummary.From(allTrades)
            };
        }

        var tickerSet = universe.Tickers;
        var quotesTask = quoteService.GetLatestQuotesAsync(tickerSet.ToArray(), quoteFeed, cancellationToken);
        var since = DateTimeOffset.UtcNow.Subtract(signalWindow);
        var recentSignalsTask = universe.WishlistId is { } wishlistId
            ? wishlists.GetSignalsAsync(wishlistId, ticker: null, since, limit: 100, cancellationToken)
            : wishlists.GetSignalsAsync(wishlistId: null, ticker: null, since, limit: 500, cancellationToken);
        var relatedNewsTask = LoadRelatedNewsAsync(tickerSet, newsWindow, cancellationToken);

        await Task.WhenAll(quotesTask, runningTradesTask, recentSignalsTask, relatedNewsTask);

        var quotes = await quotesTask;
        var everyTrade = await runningTradesTask;
        var runningTrades = everyTrade
            .Where(trade => tickerSet.Contains(trade.Ticker))
            .ToArray();
        var recentSignals = (await recentSignalsTask)
            .Where(signal => tickerSet.Contains(signal.Ticker))
            .ToArray();
        var relatedNews = await relatedNewsTask;

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

        var rows = universe.Members.Select(member =>
        {
            var quote = quotes.TryGetValue(member.Ticker, out var quoteValue)
                ? quoteValue
                : new AlpacaLatestQuote(member.Ticker, null, null, null, null, null);
            tradeByTicker.TryGetValue(member.Ticker, out var trade);
            latestSignalByTicker.TryGetValue(member.Ticker, out var signal);
            latestNewsByTicker.TryGetValue(member.Ticker, out var news);
            return new WishlistDeskRow(member.Item, quote, trade, signal, news)
            {
                Lists = member.Lists,
                IsPositionOnly = member.IsPositionOnly
            };
        }).ToArray();

        return new WishlistDeskSnapshot(rows, recentSignals, relatedNews, runningTrades, runningTrades.Sum(trade => trade.UnrealizedPl))
        {
            Portfolio = DeskPortfolioSummary.From(everyTrade)
        };
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
    decimal TotalPl)
{
    /// <summary>
    /// Totals over every open position in the environment, not only the ones in
    /// this view. <see cref="TotalPl"/> stays scoped to the view.
    /// </summary>
    public DeskPortfolioSummary Portfolio { get; init; } = DeskPortfolioSummary.Empty;
}

/// <summary>
/// One market-grid row. Every member below is a projection of data the desk
/// snapshot already holds — quote, persisted signal, tracked trade, matched news.
/// Nothing here calls a provider or re-derives an engine decision.
/// </summary>
/// <remarks>
/// Two TradingView-parity columns were investigated and deliberately left out
/// because no honest server-side source exists on this path:
/// <list type="bullet">
/// <item><description>
/// <b>Change / Change %</b> needs a prior close. The only previous-close values in
/// the system are <c>CandidateRecord.PreviousClose</c>, written by the market-state
/// candidate discovery pipeline for Finviz-screened symbols, and the daily bars a
/// backtest spills through <c>ICandleStore</c> under a specific scope/run name.
/// Neither covers an arbitrary wishlist ticker, and reaching a close for one would
/// mean a new market-data request per symbol on every desk render.
/// </description></item>
/// <item><description>
/// <b>Volume / RVOL</b> needs indicator state. <c>IndicatorSnapshot</c> is computed
/// inside <see cref="WishlistObserverService"/> and discarded after evaluation; only
/// alerted rows keep a copy inside <c>WishlistSignal.SnapshotJson</c>. A column fed
/// from that would be blank for every watching row, so it would read as missing data
/// rather than as an absent signal.
/// </description></item>
/// </list>
/// </remarks>
public sealed record WishlistDeskRow(
    WishlistItem Item,
    AlpacaLatestQuote Quote,
    MobileRunningTrade? Trade,
    WishlistSignal? LatestSignal,
    MobileNewsItem? LatestNews)
{
    public string Ticker => Item.Ticker;

    /// <summary>Wishlists holding the symbol, in name order. Empty for a screener hit on no list.</summary>
    public IReadOnlyList<string> Lists { get; init; } = [];

    /// <summary>True when the row exists only because a position is open in a symbol on no list.</summary>
    public bool IsPositionOnly { get; init; }

    /// <summary>Quantity times entry price of the tracked position, or null with none open.</summary>
    public decimal? PositionCost => Trade is null ? null : Math.Abs(Trade.Quantity) * Trade.EntryPrice;

    /// <summary>Quantity times current price of the tracked position, or null with none open.</summary>
    public decimal? PositionValue => Trade is null ? null : Math.Abs(Trade.Quantity) * Trade.CurrentPrice;

    public string DisplayName => String.IsNullOrWhiteSpace(Item.DisplayName) ? Item.Ticker : Item.DisplayName!;

    public bool HasQuote => Quote.MidPrice is not null;

    public bool HasTrade => Trade is not null;

    public bool HasSignal => LatestSignal is not null;

    public bool HasNews => LatestNews is not null;

    public string EligibilityLabel => LatestSignal is null ? "Watching" : "Observed setup";

    public string EligibilityReason => LatestSignal?.Reason ?? "Waiting for observational VWAP/EMA/MACD/volume conditions.";

    /// <summary>Inside mid, rendered as the grid's Last column.</summary>
    public decimal? LastPrice => Quote.MidPrice;

    /// <summary>Last formatted with the same precision rule as bid and ask.</summary>
    public string DisplayLast => Quote.DisplayPrice;

    /// <summary>Quoted size on the bid, straight from the Alpaca quote payload.</summary>
    public decimal? BidSize => Quote.BidSize;

    /// <summary>Quoted size on the ask, straight from the Alpaca quote payload.</summary>
    public decimal? AskSize => Quote.AskSize;

    /// <summary>
    /// Displayed depth at the inside market. Sorting the Size column by the two
    /// sides added together ranks rows by how much can actually trade at the touch.
    /// </summary>
    public decimal? TopOfBookSize => BidSize is { } bid && AskSize is { } ask
        ? bid + ask
        : BidSize ?? AskSize;

    /// <summary>
    /// Inside spread in basis points, or null when either side is missing. This is
    /// the single definition used by the grid, the sort, and the spread filter.
    /// </summary>
    public decimal? SpreadBps
    {
        get
        {
            if (Quote.BidPrice is not > 0m || Quote.AskPrice is not > 0m)
            {
                return null;
            }

            var mid = (Quote.BidPrice.Value + Quote.AskPrice.Value) / 2m;
            return mid <= 0m ? null : (Quote.AskPrice.Value - Quote.BidPrice.Value) / mid * 10_000m;
        }
    }

    /// <summary>Exchange timestamp of the quote backing this row.</summary>
    public DateTimeOffset? QuoteTimestamp => Quote.Timestamp;

    /// <summary>Name of the latest persisted setup, spaced out for reading.</summary>
    public string SetupLabel => LatestSignal is null
        ? "No setup"
        : LatestSignal.SignalType.Replace('_', ' ');

    /// <summary>When the latest persisted setup was detected.</summary>
    public DateTimeOffset? SetupDetectedAtUtc => LatestSignal?.DetectedAtUtc;

    /// <summary>Headline of the most recent story matched to this ticker.</summary>
    public string NewsHeadline => LatestNews?.Headline ?? "No matched story";

    /// <summary>Timestamp of the most recent story matched to this ticker.</summary>
    public DateTimeOffset? NewsTimestamp => LatestNews?.Timestamp;
}
