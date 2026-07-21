using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

public sealed class TradeDeskModel : PageModel
{
    private readonly IWishlistRepository repository;
    private readonly AlpacaManualOrderService manualOrders;
    private readonly ConfigCatalogService catalog;
    private readonly RunConfigWriter configWriter;
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;
    private readonly WishlistDeskService deskService;
    private readonly IRawArchiveWriter rawArchiveWriter;

    public TradeDeskModel(
        IWishlistRepository repository,
        AlpacaManualOrderService manualOrders,
        ConfigCatalogService catalog,
        RunConfigWriter configWriter,
        PaperJobService paperJobs,
        MobileAutomationService automation,
        WishlistDeskService deskService,
        IRawArchiveWriter rawArchiveWriter)
    {
        this.repository = repository;
        this.manualOrders = manualOrders;
        this.catalog = catalog;
        this.configWriter = configWriter;
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.deskService = deskService;
        this.rawArchiveWriter = rawArchiveWriter;
    }

    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? StrategyPath { get; set; }

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
        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyPath = StrategyPath });
    }

    public async Task<IActionResult> OnPostAddTickerAsync(
        Guid targetWishlistId,
        string ticker,
        string? displayName,
        string? notes,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(ticker))
        {
            ErrorMessage = "Ticker is required.";
            return RedirectToPage("/TradeDesk", new { id = targetWishlistId, source = Source, strategyPath = StrategyPath });
        }

        await repository.AddOrUpdateItemAsync(targetWishlistId, ticker, displayName, notes, cancellationToken);
        StatusMessage = $"Added {ticker.Trim().ToUpperInvariant()} to Trade Desk.";
        return RedirectToPage("/TradeDesk", new { id = targetWishlistId, source = Source, strategyPath = StrategyPath });
    }

    public async Task<IActionResult> OnPostImportFinvizAsync(Guid targetWishlistId, string finvizFilter, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(finvizFilter))
        {
            ErrorMessage = "Paste a Finviz screener URL or query first.";
            return RedirectToPage("/TradeDesk", new { id = targetWishlistId, source = Source, strategyPath = StrategyPath });
        }

        var token = Environment.GetEnvironmentVariable("FINVIZ_API_KEY")
            ?? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User);
        if (String.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "FINVIZ_API_KEY is not configured.";
            return RedirectToPage("/TradeDesk", new { id = targetWishlistId, source = Source, strategyPath = StrategyPath });
        }

        try
        {
            using var client = new FinvizClient(
                new HttpClient(),
                FinvizOptions.CreateDefault() with { AuthToken = token },
                rawArchiveWriter);
            var tickers = (await client.GetScreenerTickersAsync(finvizFilter, cancellationToken))
                .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(250)
                .ToArray();
            foreach (var ticker in tickers)
            {
                await repository.AddOrUpdateItemAsync(targetWishlistId, ticker, null, "Imported from Finviz", cancellationToken);
            }

            StatusMessage = tickers.Length == 0 ? "Finviz returned no tickers." : $"Imported {tickers.Length} Finviz ticker(s).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Finviz import failed: {exception.Message}";
        }

        return RedirectToPage("/TradeDesk", new { id = targetWishlistId, source = Source, strategyPath = StrategyPath });
    }

    public async Task<IActionResult> OnPostManualOrderAsync(
        Guid wishlistId,
        string ticker,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await manualOrders.SubmitLimitOrderAsync(ticker, side, quantity, limitPrice, cancellationToken);
            StatusMessage = $"{result.Side.ToUpperInvariant()} order submitted for {result.Quantity} {result.Ticker} at {AlpacaLatestQuote.Format(result.LimitPrice)}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
        }

        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyPath = StrategyPath });
    }

    public IActionResult OnPostRunStrategy(Guid wishlistId, string ticker, string strategyPath)
    {
        try
        {
            var paperConfigPath = SelectedPaperConfigPath ?? ResolvePaperConfigPath();
            var runName = $"tradedesk_{ticker.Trim().ToUpperInvariant()}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
            var strategy = catalog.GetStrategies().FirstOrDefault(x => x.Path.Equals(strategyPath, StringComparison.OrdinalIgnoreCase))?.Definition;
            var newsEnabled = strategy is not null && StrategyUsesNews(strategy);
            var configPath = configWriter.SaveTempConfig(
                paperConfigPath,
                [ticker],
                strategyPath,
                orderExpiration: "day",
                entryOrderType: "limit",
                extendedHours: true,
                screenerFilter: null,
                runName: runName,
                newsEnabled: newsEnabled);
            var job = paperJobs.Start(runName, configPath);
            return RedirectToPage("/PaperJob", new { id = job.JobId });
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyPath });
        }
    }

    public async Task<IActionResult> OnPostCloseTradeAsync(
        Guid wishlistId,
        string closeKind,
        Guid? jobId,
        Guid? sessionId,
        string ticker)
    {
        if (closeKind == "paper_job" && jobId is { } job)
        {
            await paperJobs.ClosePositionAsync(job, ticker);
            StatusMessage = $"Close requested for {ticker}.";
        }
        else if (closeKind == "automation" && sessionId is { } session)
        {
            await automation.CloseAsync(session);
            StatusMessage = $"Automation close requested for {ticker}.";
        }

        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyPath = StrategyPath });
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
        RelatedNews = snapshot.RelatedNews;
        Rows = snapshot.Rows.Where(MatchesFilter).ToArray();
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

    private static bool StrategyUsesNews(TradingFlow.Domain.Strategies.StrategyDefinition strategy)
    {
        return strategy.EntryRules.RequirePositiveNews ||
            strategy.EntryRules.MinNewsSentiment is not null ||
            strategy.EntryRules.VetoNewsSentimentBelow is not null ||
            strategy.EntryRules.MinCatalystPriceMovePct is not null ||
            strategy.EntryRules.MaxCatalystPriceMovePct is not null ||
            strategy.EntryRules.SetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase);
    }
}
