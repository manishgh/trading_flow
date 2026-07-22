using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

public sealed class TradeDeskModel : PageModel
{
    private readonly IWishlistRepository repository;
    private readonly ConfigCatalogService catalog;
    private readonly WishlistDeskService deskService;
    private readonly OperationalStatusService operationalStatus;

    public TradeDeskModel(
        IWishlistRepository repository,
        ConfigCatalogService catalog,
        WishlistDeskService deskService,
        OperationalStatusService operationalStatus)
    {
        this.repository = repository;
        this.catalog = catalog;
        this.deskService = deskService;
        this.operationalStatus = operationalStatus;
    }

    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? StrategyPath { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ticker { get; set; }

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public string? SelectedStrategyPath { get; private set; }
    public string? SelectedPaperConfigPath { get; private set; }
    public IReadOnlyList<WishlistDeskRow> Rows { get; private set; } = [];
    public IReadOnlyList<WishlistSignal> RecentSignals { get; private set; } = [];
    public IReadOnlyList<MobileNewsItem> RelatedNews { get; private set; } = [];
    public IReadOnlyList<MobileRunningTrade> RunningTrades { get; private set; } = [];
    public decimal TotalPl { get; private set; }
    public WishlistDeskRow? SelectedRow { get; private set; }
    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "PAPER", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "disconnected", "No quote has been received.", "UNKNOWN", "not connected",
        "attention", "Broker state has not loaded.", "blocked", "Admission state has not loaded.");

    public static IReadOnlyList<(string Key, string Label)> Filters { get; } =
    [
        ("all", "All"),
        ("trade", "In Trade"),
        ("signal", "Signals"),
        ("stockpulse", "Stock Pulse"),
        ("news", "News")
    ];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSetObservedAsync(Guid wishlistId, bool isObserved, CancellationToken cancellationToken)
    {
        await repository.SetObservedAsync(wishlistId, isObserved, cancellationToken);
        StatusMessage = isObserved ? "Wishlist observer started." : "Wishlist observer paused.";
        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyPath = StrategyPath, ticker = Ticker });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Strategies = catalog.GetStrategies();
        SelectedStrategyPath = ResolveStrategyPath();
        SelectedPaperConfigPath = ResolvePaperConfigPath();
        Wishlists = await repository.ListAsync(cancellationToken);
        SelectedWishlist = Id.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == Id.Value)
            : Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault) ?? Wishlists.FirstOrDefault();
        Id = SelectedWishlist?.Id;

        var quoteFeed = ResolveQuoteFeed();
        var snapshot = await deskService.BuildAsync(
            SelectedWishlist,
            quoteFeed,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromHours(4),
            cancellationToken);

        RunningTrades = snapshot.RunningTrades;
        TotalPl = snapshot.TotalPl;
        RecentSignals = snapshot.RecentSignals;
        RelatedNews = snapshot.RelatedNews
            .GroupBy(NewsIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Timestamp)
                .First() with
                {
                    Ticker = String.Join(", ", group
                        .SelectMany(item => SplitTickers(item.Ticker))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(ticker => ticker))
                })
            .OrderByDescending(item => item.Timestamp)
            .Take(20)
            .ToArray();
        Rows = snapshot.Rows.Where(MatchesFilter).ToArray();
        SelectedRow = String.IsNullOrWhiteSpace(Ticker)
            ? null
            : Rows.FirstOrDefault(row => row.Ticker.Equals(Ticker, StringComparison.OrdinalIgnoreCase));
        Ticker = SelectedRow?.Ticker;
        OperationalStatus = await operationalStatus.GetAsync(
            quoteFeed,
            snapshot.Rows.Select(row => row.Quote.Timestamp),
            cancellationToken);
    }

    private static string NewsIdentity(MobileNewsItem item)
    {
        return !String.IsNullOrWhiteSpace(item.Url)
            ? item.Url.Trim()
            : $"{item.Headline.Trim()}|{item.Timestamp:O}";
    }

    private static IEnumerable<string> SplitTickers(string? value)
    {
        return (value ?? String.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ticker => ticker.ToUpperInvariant());
    }

    private bool MatchesFilter(WishlistDeskRow row)
    {
        return Source?.ToLowerInvariant() switch
        {
            "trade" => row.HasTrade,
            "signal" => row.HasSignal,
            "stockpulse" => row.Trade?.Source.Equals("stockpulse", StringComparison.OrdinalIgnoreCase) == true,
            "news" => row.HasNews,
            _ => true
        };
    }

    private string ResolvePaperConfigPath()
    {
        var configs = catalog.GetPaperConfigs();
        return configs.FirstOrDefault(config => config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase))?.Path
            ?? configs.FirstOrDefault()?.Path
            ?? String.Empty;
    }

    private string ResolveStrategyPath()
    {
        if (!String.IsNullOrWhiteSpace(StrategyPath) &&
            Strategies.Any(strategy => strategy.Path.Equals(StrategyPath, StringComparison.OrdinalIgnoreCase)))
        {
            return StrategyPath;
        }

        return Strategies.FirstOrDefault(strategy => strategy.Definition.StrategyId.Contains("intraday", StringComparison.OrdinalIgnoreCase))?.Path
            ?? Strategies.FirstOrDefault()?.Path
            ?? String.Empty;
    }

    private string ResolveQuoteFeed()
    {
        var selected = catalog.GetPaperConfigs().FirstOrDefault(config => config.Path.Equals(SelectedPaperConfigPath, StringComparison.OrdinalIgnoreCase));
        return selected?.Config.Providers.Alpaca.DataFeed ?? "sip";
    }

}
