using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Backtesting;
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
    private readonly UniverseRankService ranking;
    private readonly ScreenerSyncService screener;
    private readonly ScreenerPresetService screenerPresets;
    private readonly ManualOrderTicketService tickets;
    private readonly TradingEnvironmentService environments;

    public TradeDeskModel(
        IWishlistRepository repository,
        ConfigCatalogService catalog,
        WishlistDeskService deskService,
        OperationalStatusService operationalStatus,
        SymbolIntelligenceService symbolIntelligence,
        UniverseRankService ranking,
        ScreenerSyncService screener,
        ScreenerPresetService screenerPresets,
        ManualOrderTicketService tickets,
        TradingEnvironmentService environments)
    {
        this.environments = environments;
        this.repository = repository;
        this.catalog = catalog;
        this.deskService = deskService;
        this.operationalStatus = operationalStatus;
        this.symbolIntelligence = symbolIntelligence;
        this.ranking = ranking;
        this.screener = screener;
        this.screenerPresets = screenerPresets;
        this.tickets = tickets;
    }

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

    /// <summary>Screener scope, <c>swing</c> or <c>intraday</c>. Carried so the band survives a reload.</summary>
    [BindProperty(SupportsGet = true)] public string ScreenerScopeName { get; set; } = "intraday";

    /// <summary>Finviz URL, saved screener name, or bare query string.</summary>
    [BindProperty(SupportsGet = true)] public string? ScreenerQuery { get; set; }

    /// <summary>
    /// Phone level 3 - the ticket. The phone desk is three levels with one back
    /// path, and the level lives in the query string like every other view state
    /// on this screen, so a reload lands where the operator was rather than at
    /// the top of the list.
    /// </summary>
    [BindProperty(SupportsGet = true)] public bool Ticket { get; set; }

    /// <summary>
    /// Which of the three phone levels this request renders. Desktop ignores it:
    /// the grid, the evidence rail and the ticket are all on screen at once.
    /// </summary>
    public string MobileLevel => SelectedRow is null ? "list" : Ticket ? "ticket" : "symbol";

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public string? SelectedStrategyId { get; private set; }
    public string? SelectedPaperConfigPath { get; private set; }

    /// <summary>The ranked universe after the view filter, search and range bounds.</summary>
    public IReadOnlyList<RankedDeskRow> Rows { get; private set; } = [];

    /// <summary>The whole ranked universe, before any filter. Drives the tab counts.</summary>
    public IReadOnlyList<RankedDeskRow> AllRows { get; private set; } = [];

    public IReadOnlyList<WishlistSignal> RecentSignals { get; private set; } = [];
    public IReadOnlyList<MobileNewsItem> RelatedNews { get; private set; } = [];
    public IReadOnlyList<MobileRunningTrade> RunningTrades { get; private set; } = [];
    public decimal TotalPl { get; private set; }
    public RankedDeskRow? SelectedRow { get; private set; }
    public MobileSymbolIntelligenceResponse? SymbolIntelligence { get; private set; }
    public ScreenerSyncResult? ScreenerResult { get; private set; }

    /// <summary>
    /// Saved screens for the selected scope, offered by name. Finviz has no
    /// endpoint that lists the screens saved in its own UI, so this is the local
    /// catalogue rather than a mirror of anything remote.
    /// </summary>
    public IReadOnlyList<TradingFlow.Domain.Wishlists.ScreenerPreset> ScreenerPresets { get; private set; } = [];
    public UniverseRankConfig RankConfig { get; private set; } = UniverseRankConfig.Default;

    /// <summary>
    /// Live ticket preview for the selected symbol, populated by the preview post.
    /// Null on a plain GET: the checklist states what the server checked, so it
    /// cannot be rendered before the server has checked anything.
    /// </summary>
    public ManualOrderTicketPreview? TicketPreview { get; private set; }

    public ManualOrderTicketConfirmation? TicketConfirmation { get; private set; }

    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "UNKNOWN", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "disconnected", "No quote has been received.", "UNKNOWN", "not connected",
        "attention", "Broker state has not loaded.", "blocked", "Admission state has not loaded.");

    /// <summary>
    /// Views over the ranked universe. All / Signals / In trade are properties of
    /// the row; Screener is membership of the active screener result; Disagree is
    /// the agreement flag. The last two are why the desk ranks a universe rather
    /// than filtering a list: both are only answerable after scoring.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> Filters { get; } =
    [
        ("all", "All"),
        ("signal", "Signals"),
        ("trade", "In trade"),
        ("screener", "Screener"),
        ("disagree", "Disagree")
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
        return RedirectToPage("/TradeDesk", new { id = wishlistId, source = Source, strategyId = StrategyId, ticker = Ticker, env = Env });
    }

    /// <summary>
    /// Reads what the screener would return and diffs it against the wishlist.
    /// Adds nothing - the operator decides after seeing the count.
    /// </summary>
    public async Task OnPostSyncScreenerAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (ScreenerResult is { Succeeded: true } result)
        {
            StatusMessage = $"{result.Name}: {result.Symbols.Count} hit(s), {result.NotInWishlist.Count} not yet in the wishlist.";
        }
        else if (ScreenerResult is { } failure)
        {
            ErrorMessage = failure.Error;
        }
    }

    /// <summary>
    /// Adds the screener symbols that are not already in the wishlist. Idempotent,
    /// and it never removes an existing entry.
    /// </summary>
    public async Task<IActionResult> OnPostAddScreenerAsync(CancellationToken cancellationToken)
    {
        if (Id is not { } wishlistId)
        {
            ErrorMessage = "Select a wishlist before adding screener symbols.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var scope = ParseScope(ScreenerScopeName);
        var result = await screener.PreviewAsync(ScreenerQuery ?? String.Empty, scope, wishlistId, cancellationToken);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error;
            await LoadAsync(cancellationToken);
            return Page();
        }

        foreach (var symbol in result.NotInWishlist)
        {
            await repository.AddOrUpdateItemAsync(
                wishlistId,
                symbol,
                displayName: null,
                notes: $"Added from {result.Name}",
                cancellationToken);
        }

        StatusMessage = result.NotInWishlist.Count == 0
            ? "Every screener symbol is already in the wishlist."
            : $"Added {result.NotInWishlist.Count} symbol(s) from {result.Name}.";
        return RedirectToPage("/TradeDesk", new
        {
            id = wishlistId,
            source = Source,
            strategyId = StrategyId,
            ticker = Ticker,
            env = Env,
            screenerScopeName = ScreenerScopeName,
            screenerQuery = ScreenerQuery
        });
    }

    /// <summary>
    /// Stage one of the inline ticket. The server checks quote, spread, session,
    /// account, exposure and duplicates and returns what it found; the checklist
    /// renders that response rather than recomputing it client-side.
    /// </summary>
    public async Task OnPostPreviewTicketAsync(
        [FromForm] TicketForm ticket,
        CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            return;
        }

        try
        {
            TicketPreview = await tickets.PreviewAsync(ticket.ToDraft(), cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    /// <summary>
    /// Stage two. The token is what the server issued at preview; posting without
    /// one, or with an expired one, fails closed at the service.
    /// </summary>
    public async Task OnPostConfirmTicketAsync(string? ticketToken, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            return;
        }

        try
        {
            TicketConfirmation = await tickets.ConfirmAsync(ticketToken ?? String.Empty, cancellationToken);
            StatusMessage = $"Paper order accepted for {TicketConfirmation.Ticker}.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    /// <summary>Inline ticket fields. Maps one-to-one onto <see cref="ManualOrderDraft"/>.</summary>
    public sealed class TicketForm
    {
        public string Ticker { get; set; } = String.Empty;
        public string Side { get; set; } = "buy";
        public decimal Quantity { get; set; } = 1m;
        public decimal LimitPrice { get; set; }
        public decimal? StopLossPrice { get; set; }
        public decimal? TakeProfitPrice { get; set; }
        public string Horizon { get; set; } = "intraday";
        public string OrderType { get; set; } = "limit";
        public decimal? TriggerPrice { get; set; }
        public string TimeInForce { get; set; } = "day";
        public bool AllowExtendedHoursTrading { get; set; }

        public bool IsExit => String.Equals(Side, "sell", StringComparison.OrdinalIgnoreCase);

        public ManualOrderDraft ToDraft() => new(
            Ticker.Trim().ToUpperInvariant(),
            IsExit ? "sell" : "buy",
            Quantity,
            LimitPrice,
            // An exit carries no bracket: the protection belonged to the entry.
            IsExit ? null : StopLossPrice,
            IsExit ? null : TakeProfitPrice,
            Horizon,
            AllowExtendedHoursTrading,
            "sip",
            IsExit ? OrderType : "limit",
            TriggerPrice,
            TimeInForce);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Strategies = catalog.GetStrategies();
        SelectedStrategyId = ResolveStrategyId();
        SelectedPaperConfigPath = ResolvePaperConfigPath();
        RankConfig = ResolveRankConfig();
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

        PredictionMode = MarketPredictorHttpClient.TryNormalizeMode(PredictionMode, out var normalizedMode)
            ? normalizedMode
            : "unified";
        PredictionHorizon = String.IsNullOrWhiteSpace(PredictionHorizon) ? "auto" : PredictionHorizon.Trim().ToLowerInvariant();
        ScreenerScopeName = ParseScope(ScreenerScopeName).ToString().ToLowerInvariant();

        ScreenerPresets = await screenerPresets.ListAsync(ParseScope(ScreenerScopeName), cancellationToken);

        // An intraday screen already read this session is reused rather than
        // re-fetched; a swing screen or an explicit sync goes to the provider.
        ScreenerResult = await ResolveScreenerResultAsync(cancellationToken);
        var screenerSymbols = ScreenerResult?.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var earningsSymbols = snapshot.Rows
            .Where(row => row.LatestSignal?.SignalType.Contains("earnings", StringComparison.OrdinalIgnoreCase) == true)
            .Select(row => row.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = await ranking.RankAsync(
            snapshot.Rows,
            RankConfig,
            DeskHorizon,
            PredictionMode,
            PredictionHorizon,
            screenerSymbols,
            earningsSymbols,
            SelectedWishlist is null ? "wishlist:none" : $"wishlist:{SelectedWishlist.Name}",
            cancellationToken);

        AllRows = ranked.Rows;
        Rows = SortRows(AllRows.Where(MatchesFilter).Where(MatchesQuery)).ToArray();
        SelectedRow = String.IsNullOrWhiteSpace(Ticker)
            ? null
            : AllRows.FirstOrDefault(row => row.Ticker.Equals(Ticker, StringComparison.OrdinalIgnoreCase));
        Ticker = SelectedRow?.Ticker;

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
                SelectedRow.Row,
                PredictionMode,
                PredictionHorizon,
                cancellationToken);
            await Task.WhenAll(operationalStatusTask, symbolIntelligenceTask);
            OperationalStatus = await operationalStatusTask;
            SymbolIntelligence = await symbolIntelligenceTask;
        }
    }

    private async Task<ScreenerSyncResult?> ResolveScreenerResultAsync(CancellationToken cancellationToken)
    {
        var scope = ParseScope(ScreenerScopeName);
        if (String.IsNullOrWhiteSpace(ScreenerQuery))
        {
            // With no query typed, an intraday screen already taken this session
            // still applies; nothing is fetched.
            return scope == ScreenerScope.Intraday ? screener.GetCurrentIntradayResult() : null;
        }

        return await screener.PreviewAsync(ScreenerQuery, scope, Id, cancellationToken);
    }

    /// <summary>
    /// Which weight set the ranking uses. The desk horizon follows the selected
    /// strategy's own timeframe rather than a separate control, so the ordering
    /// always matches the strategy the operator is reading against.
    /// </summary>
    public string DeskHorizon
    {
        get
        {
            var strategy = Strategies.FirstOrDefault(candidate =>
                candidate.Definition.StrategyId.Equals(SelectedStrategyId, StringComparison.OrdinalIgnoreCase));
            return strategy?.Definition.StrategyId.Contains("swing", StringComparison.OrdinalIgnoreCase) == true
                ? "swing"
                : "intraday";
        }
    }

    /// <summary>Row count in the view before search and range bounds.</summary>
    public int UnfilteredCount => AllRows.Count;

    /// <summary>Rows matching a view, used for the tab counts.</summary>
    public int CountFor(string key) => AllRows.Count(row => MatchesFilter(row, key));

    /// <summary>
    /// One market-grid column. <paramref name="Optional"/> columns are offered in
    /// the column chooser and may be switched off by the operator; the rest always
    /// render so the grid can never be reduced to nothing. Every column sorts
    /// through the same URL round-trip, so a sorted view survives reload and can
    /// be shared.
    /// </summary>
    /// <param name="Key">Sort key carried in the query string. Empty when the column does not sort.</param>
    /// <param name="Label">Visible header text.</param>
    /// <param name="CssClass">Width class shared by the header and its cells.</param>
    /// <param name="Description">Sentence shown beside the chooser checkbox.</param>
    /// <param name="Optional">Whether the chooser can hide this column.</param>
    /// <param name="VisibleByDefault">Server-rendered state before a stored preference applies.</param>
    /// <param name="Numeric">Right-aligned when true.</param>
    public sealed record DeskColumn(
        string Key,
        string Label,
        string CssClass,
        string Description,
        bool Optional,
        bool VisibleByDefault,
        bool Numeric = false);

    /// <summary>
    /// Columns the market table renders, in display order. Size, quote age and
    /// setup were folded into Bid/Ask, Last and TradingFlow respectively - if
    /// operators miss them, re-add them to the chooser rather than the default
    /// grid.
    /// </summary>
    public static IReadOnlyList<DeskColumn> Columns { get; } =
    [
        new("ticker", "Market", "market-column", "Symbol and display name.", false, true),
        new("price", "Last", "last-column", "Mid of the inside quote with the session change.", false, true, true),
        new("bidask", "Bid / Ask", "quote-column", "Inside bid and ask with quoted sizes.", false, true),
        new("spread", "Spread", "spread-column", "Inside spread in basis points.", false, true, true),
        new("rvol", "RVOL", "rvol-column", "Relative volume against the same time of day.", true, false, true),
        new("eligibility", "TradingFlow", "eligibility-column", "TradingFlow verdict and its reason.", false, true),
        new("predictor", "Predictor", "predictor-column", "Model signal, probability and horizon.", false, true),
        new("sync", "Sync", "sync-column", "Whether the verdict and the model agree.", false, true),
        new("pl", "Position", "position-column", "Tracked position and open P/L.", false, true, true),
        new("news", "Latest news", "news-column", "Most recent story matched to the symbol.", true, false)
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

    internal static ScreenerScope ParseScope(string? value) =>
        String.Equals(value, "swing", StringComparison.OrdinalIgnoreCase)
            ? ScreenerScope.Swing
            : ScreenerScope.Intraday;

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

    private bool MatchesQuery(RankedDeskRow ranked)
    {
        var row = ranked.Row;
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

    private IEnumerable<RankedDeskRow> SortRows(IEnumerable<RankedDeskRow> rows)
    {
        // No sort key keeps the rank order, which is the desk's own answer to
        // "what should I look at first". Sorting is an override of that, not the
        // default reading.
        var descending = SortDescending;
        return (Sort?.ToLowerInvariant()) switch
        {
            "price" => Order(rows, row => row.Row.LastPrice ?? Decimal.MinValue, descending),
            "bidask" => Order(rows, row => row.Row.Quote.BidPrice ?? Decimal.MinValue, descending),
            "spread" => Order(rows, row => row.Row.SpreadBps ?? Decimal.MaxValue, descending),
            "rvol" => Order(rows, row => row.Evidence.Intraday?.RelativeVolume ?? Decimal.MinValue, descending),
            "eligibility" => Order(rows, row => row.Row.HasSignal ? 1m : 0m, descending),
            "pl" => Order(rows, row => row.Row.Trade?.UnrealizedPl ?? Decimal.MinValue, descending),
            "news" => Order(rows, row => row.Row.NewsTimestamp ?? DateTimeOffset.MinValue, descending),
            "ticker" => descending
                ? rows.OrderByDescending(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.Rank)
        };
    }

    private static IEnumerable<RankedDeskRow> Order<TKey>(
        IEnumerable<RankedDeskRow> rows,
        Func<RankedDeskRow, TKey> key,
        bool descending)
    {
        // Ticker is the tiebreaker so equal values keep a stable, predictable order
        // instead of shuffling between requests.
        return descending
            ? rows.OrderByDescending(key).ThenBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key).ThenBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesFilter(RankedDeskRow row) => MatchesFilter(row, Source);

    private static bool MatchesFilter(RankedDeskRow row, string? source) => source?.ToLowerInvariant() switch
    {
        "trade" => row.Row.HasTrade,
        // Signals is "tradable now": the technicals triggered on the completed bar
        // and the model is not standing against it.
        "signal" => row.Row.HasSignal && row.Agreement != AgreementFlag.Conflict,
        "screener" => row.FromScreener,
        "disagree" => row.Agreement == AgreementFlag.Conflict,
        _ => true
    };

    /// <summary>
    /// Ranking weights from the active paper profile. A profile that cannot be
    /// read falls back to the shipped defaults rather than to no ranking, so the
    /// desk always has an order it can explain.
    /// </summary>
    private UniverseRankConfig ResolveRankConfig()
    {
        var selected = catalog.GetPaperConfigs()
            .FirstOrDefault(config => config.Path.Equals(SelectedPaperConfigPath, StringComparison.OrdinalIgnoreCase));
        return selected?.Config.Rank ?? UniverseRankConfig.Default;
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
