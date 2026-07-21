using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed record WishlistMarketRow(
    WishlistItem Item,
    AlpacaLatestQuote Quote,
    bool HasQuote)
{
    public string Ticker => Item.Ticker;

    public string DisplayName => String.IsNullOrWhiteSpace(Item.DisplayName) ? Item.Ticker : Item.DisplayName!;
}

public sealed class WishlistsModel : PageModel
{
    private readonly IWishlistRepository repository;
    private readonly AlpacaQuoteService quoteService;
    private readonly AlpacaManualOrderService manualOrders;
    private readonly ConfigCatalogService catalog;
    private readonly RunConfigWriter configWriter;
    private readonly PaperJobService paperJobs;
    private readonly NewsFeedService newsFeed;
    private readonly IRawArchiveWriter rawArchiveWriter;

    public WishlistsModel(
        IWishlistRepository repository,
        AlpacaQuoteService quoteService,
        AlpacaManualOrderService manualOrders,
        ConfigCatalogService catalog,
        RunConfigWriter configWriter,
        PaperJobService paperJobs,
        NewsFeedService newsFeed,
        IRawArchiveWriter rawArchiveWriter)
    {
        this.repository = repository;
        this.quoteService = quoteService;
        this.manualOrders = manualOrders;
        this.catalog = catalog;
        this.configWriter = configWriter;
        this.paperJobs = paperJobs;
        this.newsFeed = newsFeed;
        this.rawArchiveWriter = rawArchiveWriter;
    }

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public IReadOnlyList<WishlistSignal> Signals { get; private set; } = [];
    public IReadOnlyList<MobileNewsItem> RelatedNews { get; private set; } = [];
    public IReadOnlyList<WishlistMarketRow> MarketRows { get; private set; } = [];
    public IReadOnlyList<RunConfigSummary> PaperConfigs { get; private set; } = [];
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public string? SelectedPaperConfigPath { get; private set; }
    public string? SelectedStrategyPath { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(Guid? id, string? paperConfigPath, string? strategyPath, CancellationToken cancellationToken)
    {
        await LoadAsync(id, paperConfigPath, strategyPath, cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveWishlistAsync(
        Guid? wishlistId,
        string wishlistName,
        string? description,
        bool isDefault,
        bool includeExtendedHours,
        bool isObserved,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(wishlistName))
        {
            ErrorMessage = "Wishlist name is required.";
            return RedirectToPage("/Wishlists", new { id = wishlistId });
        }

        var saved = await repository.SaveAsync(new Wishlist
        {
            Id = wishlistId ?? Guid.Empty,
            Name = wishlistName,
            Description = description,
            IsDefault = isDefault,
            IncludeExtendedHours = includeExtendedHours,
            IsObserved = isObserved
        }, cancellationToken);
        StatusMessage = $"Saved wishlist {wishlistName.Trim()}.";
        return RedirectToPage("/Wishlists", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostCreateWishlistAsync(
        string wishlistName,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(wishlistName))
        {
            ErrorMessage = "Type a wishlist name first.";
            return RedirectToPage("/Wishlists");
        }

        var existing = await repository.GetByNameAsync(wishlistName, cancellationToken);
        if (existing is not null)
        {
            StatusMessage = $"Opened existing wishlist {existing.Name}.";
            return RedirectToPage("/Wishlists", new { id = existing.Id });
        }

        var saved = await repository.SaveAsync(new Wishlist
        {
            Name = wishlistName.Trim(),
            Description = "Custom wishlist",
            IncludeExtendedHours = true,
            IsDefault = false,
            IsObserved = false
        }, cancellationToken);
        StatusMessage = $"Created wishlist {saved.Name}.";
        return RedirectToPage("/Wishlists", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostSetObservedAsync(Guid wishlistId, bool isObserved, CancellationToken cancellationToken)
    {
        await repository.SetObservedAsync(wishlistId, isObserved, cancellationToken);
        StatusMessage = isObserved ? "Wishlist observer started." : "Wishlist observer paused.";
        return RedirectToPage("/Wishlists", new { id = wishlistId });
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
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        await repository.AddOrUpdateItemAsync(targetWishlistId, ticker, displayName, notes, cancellationToken);
        StatusMessage = $"Added {ticker.Trim().ToUpperInvariant()} to wishlist.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostImportFinvizAsync(
        Guid targetWishlistId,
        string finvizFilter,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(finvizFilter))
        {
            ErrorMessage = "Paste a Finviz screener URL or query first.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        var token = ResolveFinvizToken();
        if (String.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "FINVIZ_API_KEY is not configured.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
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
                await repository.AddOrUpdateItemAsync(targetWishlistId, ticker, displayName: null, notes: "Imported from Finviz", cancellationToken);
            }

            StatusMessage = tickers.Length == 0
                ? "Finviz returned no tickers."
                : $"Imported {tickers.Length} Finviz ticker(s).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Finviz import failed: {exception.Message}";
        }

        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostDeleteWishlistAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(wishlistId, cancellationToken);
        StatusMessage = "Wishlist deleted.";
        return RedirectToPage("/Wishlists");
    }

    public async Task<IActionResult> OnPostAcknowledgeSignalAsync(Guid signalId, Guid? wishlistId, CancellationToken cancellationToken)
    {
        await repository.AcknowledgeSignalAsync(signalId, cancellationToken);
        return RedirectToPage("/Wishlists", new { id = wishlistId });
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
            StatusMessage = $"{result.Side.ToUpperInvariant()} order submitted for {result.Quantity} {result.Ticker} at {AlpacaLatestQuote.Format(result.LimitPrice)}. Order {result.OrderId}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
        }

        return RedirectToPage("/Wishlists", new { id = wishlistId });
    }

    public IActionResult OnPostRunStrategy(
        Guid wishlistId,
        string ticker,
        string paperConfigPath,
        string strategyPath)
    {
        try
        {
            var runName = $"wishlist_{ticker.Trim().ToUpperInvariant()}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
            var strategy = catalog.GetStrategies().FirstOrDefault(x => x.Path.Equals(strategyPath, StringComparison.OrdinalIgnoreCase))?.Definition;
            var newsEnabled = strategy is not null && StrategyUsesNews(strategy);
            var configPath = configWriter.SaveTempConfig(
                paperConfigPath,
                new[] { ticker },
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
            return RedirectToPage("/Wishlists", new { id = wishlistId });
        }
    }

    private async Task LoadAsync(Guid? id, string? paperConfigPath, string? strategyPath, CancellationToken cancellationToken)
    {
        PaperConfigs = catalog.GetPaperConfigs();
        Strategies = catalog.GetStrategies();
        SelectedPaperConfigPath = ResolvePaperConfigPath(paperConfigPath);
        SelectedStrategyPath = ResolveStrategyPath(strategyPath);

        Wishlists = await repository.ListAsync(cancellationToken);
        SelectedWishlist = id.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == id.Value)
            : Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault) ?? Wishlists.FirstOrDefault();

        var activeItems = SelectedWishlist?.Items
            .Where(item => item.Active)
            .OrderBy(item => item.Ticker)
            .ToArray() ?? [];
        var feed = ResolveQuoteFeed();
        var quotes = await quoteService.GetLatestQuotesAsync(activeItems.Select(item => item.Ticker).ToArray(), feed, cancellationToken);
        MarketRows = activeItems
            .Select(item =>
            {
                var quote = quotes.TryGetValue(item.Ticker, out var value) ? value : new AlpacaLatestQuote(item.Ticker, null, null, null, null, null);
                return new WishlistMarketRow(item, quote, quote.MidPrice is not null);
            })
            .ToArray();

        Signals = await repository.GetSignalsAsync(SelectedWishlist?.Id, null, DateTimeOffset.UtcNow.AddMinutes(-20), 30, cancellationToken);
        RelatedNews = await LoadRelatedNewsAsync(activeItems.Select(item => item.Ticker).ToArray(), cancellationToken);
    }

    private async Task<IReadOnlyList<MobileNewsItem>> LoadRelatedNewsAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        var tickerSet = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tickerSet.Count == 0)
        {
            return Array.Empty<MobileNewsItem>();
        }

        var since = DateTimeOffset.UtcNow.AddHours(-4);
        var feed = await newsFeed.GetRollingAsync(4, null, cancellationToken);
        return feed.Items
            .Where(item => SplitTickerDisplay(item.Ticker).Any(tickerSet.Contains))
            .Where(item => item.Timestamp >= since)
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

    private static string? ResolveFinvizToken()
    {
        return Environment.GetEnvironmentVariable("FINVIZ_API_KEY")
            ?? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User);
    }

    private string ResolvePaperConfigPath(string? requested)
    {
        if (!String.IsNullOrWhiteSpace(requested) && PaperConfigs.Any(config => config.Path.Equals(requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        return PaperConfigs.FirstOrDefault(config => config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase))?.Path
            ?? PaperConfigs.FirstOrDefault()?.Path
            ?? String.Empty;
    }

    private string ResolveStrategyPath(string? requested)
    {
        if (!String.IsNullOrWhiteSpace(requested) && Strategies.Any(strategy => strategy.Path.Equals(requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        return Strategies.FirstOrDefault(strategy => strategy.Definition.StrategyId.Contains("intraday", StringComparison.OrdinalIgnoreCase))?.Path
            ?? Strategies.FirstOrDefault()?.Path
            ?? String.Empty;
    }

    private string ResolveQuoteFeed()
    {
        var selected = PaperConfigs.FirstOrDefault(config => config.Path.Equals(SelectedPaperConfigPath, StringComparison.OrdinalIgnoreCase));
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
