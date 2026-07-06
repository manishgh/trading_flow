using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IWishlistRepository wishlists;
    private readonly PaperJobService paperJobs;
    private readonly BacktestJobService backtestJobs;
    private readonly OptimizationJobService optimizationJobs;
    private readonly MobileAutomationService automation;
    private readonly ConfigCatalogService catalog;
    private readonly NewsFeedService newsFeed;

    public IndexModel(
        IWishlistRepository wishlists,
        PaperJobService paperJobs,
        BacktestJobService backtestJobs,
        OptimizationJobService optimizationJobs,
        MobileAutomationService automation,
        ConfigCatalogService catalog,
        NewsFeedService newsFeed)
    {
        this.wishlists = wishlists;
        this.paperJobs = paperJobs;
        this.backtestJobs = backtestJobs;
        this.optimizationJobs = optimizationJobs;
        this.automation = automation;
        this.catalog = catalog;
        this.newsFeed = newsFeed;
    }

    public DashboardTile RunningTrades { get; private set; } = DashboardTile.Empty;
    public DashboardTile Wishlists { get; private set; } = DashboardTile.Empty;
    public DashboardTile Signals { get; private set; } = DashboardTile.Empty;
    public DashboardTile PaperRuns { get; private set; } = DashboardTile.Empty;
    public DashboardTile Research { get; private set; } = DashboardTile.Empty;
    public IReadOnlyList<MobileRunningTrade> OpenTrades { get; private set; } = [];
    public IReadOnlyList<WishlistSignal> RecentSignals { get; private set; } = [];
    public IReadOnlyList<MobileNewsItem> RecentNews { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var allWishlists = await wishlists.ListAsync(cancellationToken);
        var openTrades = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        var signals = await wishlists.GetSignalsAsync(null, null, DateTimeOffset.UtcNow.AddMinutes(-20), 8, cancellationToken);
        var news = await newsFeed.GetRollingAsync(1, null, cancellationToken);

        var activeWishlistCount = allWishlists.Count(wishlist => wishlist.IsObserved);
        var activeTickerCount = allWishlists
            .SelectMany(wishlist => wishlist.Items)
            .Count(item => item.Active);
        var paperSnapshots = paperJobs.List();
        var backtestSnapshots = backtestJobs.List();
        var optimizationSnapshots = optimizationJobs.List();
        var activePaperRuns = paperSnapshots.Count(job => job.Status is "queued" or "running" or "starting");
        var activeResearchRuns =
            backtestSnapshots.Count(job => job.Status is "queued" or "running" or "starting") +
            optimizationSnapshots.Count(job => job.Status is "queued" or "running" or "starting" or "cancelling");
        var stockPulseOpen = openTrades.Count(trade => trade.Source.Equals("stockpulse", StringComparison.OrdinalIgnoreCase));

        RunningTrades = new DashboardTile(
            "Running Trades",
            openTrades.Count.ToString(),
            openTrades.Sum(trade => trade.UnrealizedPl).ToString("C2"),
            "Open positions across wishlist, Stock Pulse, and manual runs.",
            "/TradeDesk?source=all",
            "Open Trade Desk",
            openTrades.Sum(trade => trade.UnrealizedPl) >= 0 ? "good" : "bad");
        Wishlists = new DashboardTile(
            "Wishlists",
            $"{activeWishlistCount}/{allWishlists.Count}",
            $"{activeTickerCount} active tickers",
            "Observed ticker groups drive paper runs and breakout monitoring.",
            "/TradeDesk",
            "Open Trade Desk",
            "accent");
        Signals = new DashboardTile(
            "Signals + News",
            (signals.Count + news.Items.Count).ToString(),
            $"{signals.Count} tech / {news.Items.Count} news",
            "Rolling 20-minute technical signals plus 1-hour news context.",
            "/TradeDesk?source=signal",
            "Review signals",
            signals.Count > 0 ? "warn" : "neutral");
        PaperRuns = new DashboardTile(
            "Paper Runs",
            activePaperRuns.ToString(),
            $"{stockPulseOpen} Stock Pulse open",
            "Forward tests and alert-driven paper positions currently active.",
            "/Paper",
            "Start paper run",
            activePaperRuns > 0 ? "good" : "neutral");
        Research = new DashboardTile(
            "Backtest Lab",
            activeResearchRuns.ToString(),
            $"{catalog.GetStrategies().Count} promoted strategies",
            "Backtests and optimization jobs for promoted strategy evidence.",
            "/Backtests",
            "Run research",
            activeResearchRuns > 0 ? "warn" : "neutral");

        OpenTrades = openTrades.Take(8).ToArray();
        RecentSignals = signals;
        RecentNews = news.Items.Take(8).ToArray();
    }
}

public sealed record DashboardTile(
    string Title,
    string Value,
    string Detail,
    string Description,
    string Url,
    string Action,
    string Tone)
{
    public static DashboardTile Empty { get; } = new(String.Empty, String.Empty, String.Empty, String.Empty, "/", String.Empty, "neutral");
}
