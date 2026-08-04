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
    private readonly SymbolIntelligenceService symbolIntelligence;

    public TradeDeskModel(
        IWishlistRepository repository,
        ConfigCatalogService catalog,
        WishlistDeskService deskService,
        OperationalStatusService operationalStatus,
        SymbolIntelligenceService symbolIntelligence,
        TradingEnvironmentService environments)
    {
        this.environments = environments;
        this.repository = repository;
        this.catalog = catalog;
        this.deskService = deskService;
        this.operationalStatus = operationalStatus;
        this.symbolIntelligence = symbolIntelligence;
    }

    private readonly TradingEnvironmentService environments;

    /// <summary>Environment this screen is operating against, from the route segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? StrategyId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ticker { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinPrice { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxPrice { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxSpreadBps { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }
    [BindProperty(SupportsGet = true)] public string PredictionMode { get; set; } = "unified";
    [BindProperty(SupportsGet = true)] public string PredictionHorizon { get; set; } = "auto";

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public string? SelectedStrategyId { get; private set; }
    public string? SelectedPaperConfigPath { get; private set; }
    public IReadOnlyList<WishlistDeskRow> Rows { get; private set; } = [];
    public IReadOnlyList<WishlistSignal> RecentSignals { get; private set; } = [];
    public IReadOnlyList<MobileNewsItem> RelatedNews { get; private set; } = [];
    public IReadOnlyList<MobileRunningTrade> RunningTrades { get; private set; } = [];
    public decimal TotalPl { get; private set; }
    public WishlistDeskRow? SelectedRow { get; private set; }
    public MobileSymbolIntelligenceResponse? SymbolIntelligence { get; private set; }
    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "UNKNOWN", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
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
        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyId = StrategyId, ticker = Ticker });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Strategies = catalog.GetStrategies();
        SelectedStrategyId = ResolveStrategyId();
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
        Rows = SortRows(snapshot.Rows.Where(MatchesFilter).Where(MatchesQuery)).ToArray();
        UnfilteredCount = snapshot.Rows.Count;
        SelectedRow = String.IsNullOrWhiteSpace(Ticker)
            ? null
            : Rows.FirstOrDefault(row => row.Ticker.Equals(Ticker, StringComparison.OrdinalIgnoreCase));
        Ticker = SelectedRow?.Ticker;
        PredictionMode = MarketPredictorHttpClient.TryNormalizeMode(PredictionMode, out var normalizedMode)
            ? normalizedMode
            : "unified";
        PredictionHorizon = String.IsNullOrWhiteSpace(PredictionHorizon) ? "auto" : PredictionHorizon.Trim().ToLowerInvariant();
        var operationalStatusTask = operationalStatus.GetAsync(
            quoteFeed,
            snapshot.Rows.Select(row => row.Quote.Timestamp),
            cancellationToken);
        if (SelectedRow is null)
        {
            OperationalStatus = await operationalStatusTask;
        }
        else
        {
            var symbolIntelligenceTask = symbolIntelligence.BuildAsync(
                SelectedRow,
                PredictionMode,
                PredictionHorizon,
                cancellationToken);
            await Task.WhenAll(operationalStatusTask, symbolIntelligenceTask);
            OperationalStatus = await operationalStatusTask;
            SymbolIntelligence = await symbolIntelligenceTask;
        }
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

    /// <summary>Row count before search, price, and spread filters are applied.</summary>
    public int UnfilteredCount { get; private set; }

    /// <summary>
    /// One market-grid column. <paramref name="Optional"/> columns are offered in the
    /// column chooser and may be switched off by the operator; the rest always render
    /// so the grid can never be reduced to nothing. Every column sorts through the
    /// same URL round-trip, so a sorted view survives reload and can be shared.
    /// </summary>
    /// <param name="Key">Sort key carried in the query string.</param>
    /// <param name="Label">Visible header text.</param>
    /// <param name="CssClass">Width class shared by the header and its cells.</param>
    /// <param name="Description">Sentence shown beside the chooser checkbox.</param>
    /// <param name="Optional">Whether the chooser can hide this column.</param>
    /// <param name="VisibleByDefault">Server-rendered state before a stored preference applies.</param>
    public sealed record DeskColumn(
        string Key,
        string Label,
        string CssClass,
        string Description,
        bool Optional,
        bool VisibleByDefault);

    /// <summary>
    /// Columns the market table renders, in display order. Each one is backed by a
    /// value the desk snapshot already carries; see <see cref="WishlistDeskRow"/> for
    /// the two TradingView columns that were investigated and rejected for having no
    /// server-side source.
    /// </summary>
    public static IReadOnlyList<DeskColumn> Columns { get; } =
    [
        new("ticker", "Market", "market-column", "Symbol and display name.", false, true),
        new("price", "Last", "last-column", "Mid of the inside quote.", true, true),
        new("bidask", "Bid / Ask", "quote-column", "Inside bid and ask with quote state.", false, true),
        new("size", "Size", "size-column", "Quoted size at the inside bid and ask.", true, false),
        new("spread", "Spread", "spread-column", "Inside spread in basis points.", false, true),
        new("quoteage", "Updated", "quoteage-column", "Exchange time of the latest quote.", true, true),
        new("eligibility", "Eligibility", "eligibility-column", "TradingFlow verdict and its reason.", false, true),
        new("setup", "Setup", "setup-column", "Name and time of the latest persisted setup.", true, false),
        new("pl", "Position", "position-column", "Tracked position and open P/L.", false, true),
        new("news", "News", "news-column", "Most recent story matched to the symbol.", true, false)
    ];

    /// <summary>Columns the chooser can switch off, in display order.</summary>
    public static IReadOnlyList<DeskColumn> OptionalColumns { get; } =
        Columns.Where(column => column.Optional).ToArray();

    /// <summary>Current sort direction, defaulting to ascending.</summary>
    public bool SortDescending => String.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>aria-sort</c> value for <paramref name="key"/>, or null when unsorted.</summary>
    public string? AriaSortFor(string key) =>
        String.Equals(Sort, key, StringComparison.OrdinalIgnoreCase)
            ? (SortDescending ? "descending" : "ascending")
            : null;

    /// <summary>Direction a header link should request so clicking it toggles.</summary>
    public string NextDirectionFor(string key) =>
        String.Equals(Sort, key, StringComparison.OrdinalIgnoreCase) && !SortDescending ? "desc" : "asc";

    private bool MatchesQuery(WishlistDeskRow row)
    {
        if (!String.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            var matches = row.Ticker.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        // A row with no quote cannot satisfy a price or spread bound. Excluding it is
        // deliberate: a numeric filter should not silently pass unknown values.
        var mid = row.Quote.MidPrice;
        if (MinPrice is { } min && (mid is null || mid < min))
        {
            return false;
        }
        if (MaxPrice is { } max && (mid is null || mid > max))
        {
            return false;
        }
        if (MaxSpreadBps is { } maxSpread)
        {
            var spread = row.SpreadBps;
            if (spread is null || spread > maxSpread)
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerable<WishlistDeskRow> SortRows(IEnumerable<WishlistDeskRow> rows)
    {
        // Rows without a value sort to the far end rather than mixing into the middle,
        // so "no quote yet" never looks like a real reading of zero.
        var descending = SortDescending;
        return (Sort?.ToLowerInvariant()) switch
        {
            "price" => Order(rows, row => row.LastPrice ?? Decimal.MinValue, descending),
            "bidask" => Order(rows, row => row.Quote.BidPrice ?? Decimal.MinValue, descending),
            "size" => Order(rows, row => row.TopOfBookSize ?? Decimal.MinValue, descending),
            "spread" => Order(rows, row => row.SpreadBps ?? Decimal.MaxValue, descending),
            "quoteage" => Order(rows, row => row.QuoteTimestamp ?? DateTimeOffset.MinValue, descending),
            "eligibility" => Order(rows, row => row.HasSignal ? 1m : 0m, descending),
            "setup" => Order(rows, row => row.SetupDetectedAtUtc ?? DateTimeOffset.MinValue, descending),
            "pl" => Order(rows, row => row.Trade?.UnrealizedPl ?? Decimal.MinValue, descending),
            "news" => Order(rows, row => row.NewsTimestamp ?? DateTimeOffset.MinValue, descending),
            "ticker" => descending
                ? rows.OrderByDescending(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase),
            _ => rows
        };
    }

    private static IEnumerable<WishlistDeskRow> Order<TKey>(
        IEnumerable<WishlistDeskRow> rows,
        Func<WishlistDeskRow, TKey> key,
        bool descending)
    {
        // Ticker is the tiebreaker so equal values keep a stable, predictable order
        // instead of shuffling between requests.
        return descending
            ? rows.OrderByDescending(key).ThenBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key).ThenBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase);
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

    private string ResolveStrategyId()
    {
        if (!String.IsNullOrWhiteSpace(StrategyId) &&
            Strategies.Any(strategy => strategy.Definition.StrategyId.Equals(StrategyId, StringComparison.OrdinalIgnoreCase)))
        {
            return StrategyId;
        }

        return Strategies.FirstOrDefault(strategy => strategy.Definition.StrategyId.Contains("intraday", StringComparison.OrdinalIgnoreCase))?.Definition.StrategyId
            ?? Strategies.FirstOrDefault()?.Definition.StrategyId
            ?? String.Empty;
    }

    private string ResolveQuoteFeed()
    {
        var selected = catalog.GetPaperConfigs().FirstOrDefault(config => config.Path.Equals(SelectedPaperConfigPath, StringComparison.OrdinalIgnoreCase));
        return selected?.Config.Providers.Alpaca.DataFeed ?? "sip";
    }

}
