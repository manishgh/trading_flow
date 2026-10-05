using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages.Shared;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

public sealed class TradeDeskModel : PageModel
{
    private readonly DeskUniverseEvidenceService universeEvidence;
    public IReadOnlyDictionary<string, DeskUniverseEvidence> MarketEvidence { get; private set; } = new Dictionary<string, DeskUniverseEvidence>();
    public DeskUniverseEvidence EvidenceFor(string ticker) => MarketEvidence.GetValueOrDefault(ticker) ?? new();
    private readonly IWishlistRepository repository;
    private readonly ConfigCatalogService catalog;
    private readonly WishlistDeskService deskService;
    private readonly OperationalStatusService operationalStatus;
    private readonly SymbolIntelligenceService symbolIntelligence;
    private readonly UniverseRankService ranking;
    private readonly ScreenerSyncService screener;
    private readonly ScreenerPresetService screenerPresets;
    private readonly TradingEnvironmentService environments;
    private readonly DeskPreparationService preparation;
    private readonly TradingFlow.Web.Services.Desk.IDeskAccountReader accountReader;
    private readonly TradingFlow.Web.Services.Desk.DeskSources sources;

    public TradeDeskModel(
        IWishlistRepository repository,
        DeskUniverseEvidenceService universeEvidence,
        ConfigCatalogService catalog,
        WishlistDeskService deskService,
        OperationalStatusService operationalStatus,
        SymbolIntelligenceService symbolIntelligence,
        UniverseRankService ranking,
        ScreenerSyncService screener,
        ScreenerPresetService screenerPresets,
        TradingFlow.Web.Services.Desk.DeskSources sources,
        TradingEnvironmentService environments,
        DeskPreparationService preparation,
        TradingFlow.Web.Services.Desk.IDeskAccountReader accountReader)
    {
        this.universeEvidence = universeEvidence;
        this.accountReader = accountReader;
        this.environments = environments;
        this.preparation = preparation;
        this.repository = repository;
        this.catalog = catalog;
        this.deskService = deskService;
        this.operationalStatus = operationalStatus;
        this.symbolIntelligence = symbolIntelligence;
        this.ranking = ranking;
        this.screener = screener;
        this.screenerPresets = screenerPresets;
        this.sources = sources;
    }

    /// <summary>Environment this screen is operating against, from the route segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    /// <summary>
    /// The wishlist to monitor. Null means every wishlist at once - the desk's
    /// default, because the operator follows all of their lists, not one.
    /// </summary>
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }

    /// <summary>
    /// Where the rows come from: <c>lists</c> (wishlists, the default) or
    /// <c>screener</c> (the symbols a screen returned).
    /// </summary>
    [BindProperty(SupportsGet = true), Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public string Scope { get; set; } = "lists";

    /// <summary>Wishlist that screener symbols are added to. Defaults to the default wishlist.</summary>
    [BindProperty(SupportsGet = true)] public Guid? TargetId { get; set; }

    /// <summary>One symbol to prepare, from the rail. Null prepares every symbol in the view that needs it.</summary>
    [BindProperty] public string? PrepareTicker { get; set; }

    /// <summary>Name to save the current custom screen under.</summary>
    [BindProperty] public string? PresetName { get; set; }
    [BindProperty(SupportsGet = true), Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public string Source { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? StrategyId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ticker { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinPrice { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxPrice { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxSpreadBps { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinChange { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxChange { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinVolumeZ20 { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxVolumeZ20 { get; set; }
    [BindProperty(SupportsGet = true)] public string? Tracked { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 50;
    public int MatchingCount { get; private set; }
    public int PageCount { get; private set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Sector { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinMarketCap { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxMarketCap { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinVolume { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinRvol { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxRvol { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinAtrPercent { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxAtrPercent { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinGap { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxGap { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinVwapDistance { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxVwapDistance { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MinRsi { get; set; }
    [BindProperty(SupportsGet = true)] public decimal? MaxRsi { get; set; }
    [BindProperty(SupportsGet = true)] public int? MaxEarningsDays { get; set; }
    [BindProperty(SupportsGet = true)] public bool? CatalystOnly { get; set; }
    [BindProperty(SupportsGet = true)] public string? SignalState { get; set; }
    private DeskGridQuery GridQuery => new(Search, MinPrice, MaxPrice, MaxSpreadBps, MinChange, MaxChange, MinVolumeZ20, MaxVolumeZ20, Tracked, Sector, MinMarketCap, MaxMarketCap, MinVolume, MinRvol, MaxRvol, MinAtrPercent, MaxAtrPercent, MinGap, MaxGap, MinVwapDistance, MaxVwapDistance, MinRsi, MaxRsi, MaxEarningsDays, CatalystOnly, SignalState);
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }
    [BindProperty(SupportsGet = true), Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public string PredictionHorizon { get; set; } = "auto";
    public string ScreenerScopeName => "swing";

    /// <summary>Catalogued Finviz signal (<c>signal:code</c>), Finviz URL, saved screener name, or bare query string.</summary>
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

    /// <summary>The symbols this view monitors, resolved from the scope before any quote is read.</summary>
    public DeskUniverse Universe { get; private set; } = DeskUniverse.Empty;

    /// <summary>Totals over every open position, independent of the view.</summary>
    public TradingFlow.Web.Services.Desk.DeskAccount Account { get; private set; } = new(false, "Account not read", DateTimeOffset.UtcNow, []);
    public DeskPortfolioSummary Portfolio { get; private set; } = DeskPortfolioSummary.Empty;

    /// <summary>Warmup-service preparation state for every symbol on the desk.</summary>
    public DeskPreparationSnapshot Preparation { get; private set; } =
        new(false, "Preparation state has not loaded.", new Dictionary<string, DeskPreparationStatus>(), null);

    public bool IsScreenerScope => String.Equals(Scope, "screener", StringComparison.OrdinalIgnoreCase);

    /// <summary>Wishlist the screener band adds to.</summary>
    public Wishlist? TargetWishlist { get; private set; }

    /// <summary>Built-in Finviz signals offered as ready-made screens, grouped for the picker.</summary>
    public static IReadOnlyList<IGrouping<string, FinvizSignal>> SignalGroups { get; } =
        FinvizSignalCatalog.All.GroupBy(signal => signal.Group).ToArray();
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


    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "UNKNOWN", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "disconnected", "No quote has been received.", "UNKNOWN", "not connected",
        "attention", "Broker state has not loaded.", "blocked", "Admission state has not loaded.");

    /// <summary>
    /// Views over the ranked universe. All / Observed setups / In trade / With news
    /// are properties of the row; Needs prep is the preparation state; Disagree is
    /// the agreement flag, which is why the desk ranks a universe rather than
    /// filtering a list: it is only answerable after scoring. Screener membership
    /// is no longer a view: a screener is now a universe of its own.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> Filters { get; } =
    [
        ("all", "All"),
        ("signal", "Observed setups"),
        ("trade", "In trade"),
        ("news", "With news"),
        ("prep", "Needs prep"),
        ("disagree", "Disagree")
    ];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Tracked = Tracked?.Trim().ToLowerInvariant();
        SignalState = SignalState?.Trim().ToLowerInvariant();
        Scope = String.IsNullOrWhiteSpace(Scope) ? "lists" : Scope.Trim().ToLowerInvariant();
        Source = String.IsNullOrWhiteSpace(Source) ? "all" : Source.Trim().ToLowerInvariant();
        PredictionHorizon = String.IsNullOrWhiteSpace(PredictionHorizon) ? "auto" : PredictionHorizon.Trim();
        if (Scope is not ("lists" or "screener") || !Filters.Any(x => x.Key == Source)) return BadRequest("Choose a supported desk universe and view.");
        var typedFields = new[] { nameof(Id), nameof(TargetId), nameof(MinPrice), nameof(MaxPrice), nameof(MaxSpreadBps),
            nameof(MinChange), nameof(MaxChange), nameof(MinVolumeZ20), nameof(MaxVolumeZ20), nameof(PageNumber), nameof(PageSize), nameof(Ticket), nameof(MinMarketCap), nameof(MaxMarketCap), nameof(MinVolume), nameof(MinRvol), nameof(MaxRvol), nameof(MinAtrPercent), nameof(MaxAtrPercent), nameof(MinGap), nameof(MaxGap), nameof(MinVwapDistance), nameof(MaxVwapDistance), nameof(MinRsi), nameof(MaxRsi), nameof(MaxEarningsDays), nameof(CatalystOnly) };
        if (typedFields.Any(key => ModelState.TryGetValue(key, out var field) && field.Errors.Count > 0))
            return BadRequest("Choose valid numeric desk filters, page values and group IDs.");
        try { DeskGridFilter.Validate(GridQuery); }
        catch (InvalidOperationException exception) { return BadRequest(exception.Message); }
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSetObservedAsync(Guid wishlistId, bool isObserved, CancellationToken cancellationToken)
    {
        await repository.SetObservedAsync(wishlistId, isObserved, cancellationToken);
        StatusMessage = isObserved ? "Wishlist observer started." : "Wishlist observer paused.";
        return RedirectToPage("/TradeDesk", RouteWith(("id", wishlistId.ToString())));
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
        await LoadAsync(cancellationToken);
        if (TargetWishlist is not { } target)
        {
            ErrorMessage = "Choose a wishlist to add the screener symbols to.";
            return Page();
        }

        if (ScreenerResult is not { Succeeded: true } result)
        {
            ErrorMessage = ScreenerResult?.Error ?? "Choose a screener first.";
            return Page();
        }

        foreach (var symbol in result.NotInWishlist)
        {
            await repository.AddOrUpdateItemAsync(
                target.Id,
                symbol,
                displayName: null,
                notes: $"Added from {result.Name}",
                cancellationToken);
        }

        StatusMessage = result.NotInWishlist.Count == 0
            ? $"Every screener symbol is already in {target.Name}."
            : $"Added {result.NotInWishlist.Count} symbol(s) from {result.Name} to {target.Name}.";
        return RedirectToPage("/TradeDesk", RouteWith());
    }

    /// <summary>
    /// Saves the screen in view as a named preset, so it is offered by name under
    /// "My saved screens". Finviz cannot list the screens saved in its own UI, so
    /// this is how one of those becomes pickable: paste its URL, run it, save it.
    /// Saving the same name again replaces the query and clears any verification.
    /// </summary>
    public async Task<IActionResult> OnPostSaveScreenerAsync(CancellationToken cancellationToken)
    {
        var name = PresetName?.Trim() ?? String.Empty;
        var query = ScreenerQuery?.Trim() ?? String.Empty;
        string? problem = name.Length == 0 || query.Length == 0
            ? "Run a custom screen and give it a name before saving it."
            : name.Length > 120
                ? "A preset name can be at most 120 characters."
                : name.StartsWith(FinvizSignalCatalog.InputPrefix, StringComparison.OrdinalIgnoreCase)
                    ? $"A preset name cannot start with \"{FinvizSignalCatalog.InputPrefix}\"."
                    : null;
        if (problem is not null)
        {
            ErrorMessage = problem;
            await LoadAsync(cancellationToken);
            return Page();
        }

        var filterQuery = FinvizSignalCatalog.TryResolveInput(query, out var signal) ? signal.Query : query;
        var preset = await screenerPresets.SaveAsync(name, ScreenerScope.Swing, filterQuery, cancellationToken);
        StatusMessage = $"Saved \"{preset.Name}\". It is listed under My saved screens.";
        return RedirectToPage("/TradeDesk", RouteWith(("scope", "screener"), ("screenerQuery", preset.Name)));
    }

    /// <summary>
    /// Asks the warmup service to prepare the symbols in this view that are not
    /// ready - or the one symbol posted from the rail. The desk never warms data
    /// itself; it queues the request and shows the service's answer.
    /// </summary>
    public async Task<IActionResult> OnPostPrepareAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!Preparation.Available)
        {
            ErrorMessage = Preparation.Error;
            return Page();
        }

        IReadOnlyList<string> tickers = String.IsNullOrWhiteSpace(PrepareTicker)
            ? Preparation.NeedingPreparation
            : Universe.Tickers.Contains(PrepareTicker.Trim())
                ? [PrepareTicker.Trim().ToUpperInvariant()]
                : [];
        if (!String.IsNullOrWhiteSpace(PrepareTicker) && tickers.Count == 0)
        {
            ErrorMessage = $"{PrepareTicker.Trim().ToUpperInvariant()} is not in this view.";
            return Page();
        }

        try
        {
            StatusMessage = await preparation.PrepareAsync(tickers, Universe.Label, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ErrorMessage = $"Preparation request failed: {exception.Message}";
            return Page();
        }

        return RedirectToPage("/TradeDesk", RouteWith());
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Strategies = await catalog.GetStrategiesAsync(
            environments.Parse(Env) == TradingEnvironment.Live
                ? StrategySelectionMode.RunLive
                : StrategySelectionMode.RunPaperExperiment,
            cancellationToken);
        SelectedStrategyId = ResolveStrategyId();
        SelectedPaperConfigPath = ResolvePaperConfigPath();
        RankConfig = ResolveRankConfig();
        Wishlists = await sources.FollowedListsAsync(cancellationToken);
        Scope = IsScreenerScope ? "screener" : "lists";
        SelectedWishlist = Id.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == Id.Value)
            : null;
        Id = SelectedWishlist?.Id;
        TargetWishlist = (TargetId.HasValue ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == TargetId.Value) : null)
            ?? Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)
            ?? Wishlists.FirstOrDefault();
        TargetId = TargetWishlist?.Id;
        ScreenerPresets = await screenerPresets.ListAsync(ScreenerScope.Swing, cancellationToken);

        if (IsScreenerScope)
        {
            // The screener result is the universe. It is diffed against the target
            // wishlist so the band can say how many hits are new to that list.
            ScreenerResult = await ResolveScreenerResultAsync(cancellationToken);
            Universe = ScreenerResult is { Succeeded: true } hits
                ? DeskUniverse.ForScreener(hits.Name, hits.AllSymbols.Count > 0 ? hits.AllSymbols : hits.Symbols, Wishlists)
                : DeskUniverse.Empty with { Kind = DeskUniverseKind.Screener, Label = ScreenerDisplayName };
        }
        else
        {
            Universe = SelectedWishlist is null
                ? DeskUniverse.ForAllWishlists(Wishlists)
                : DeskUniverse.ForWishlist(SelectedWishlist);
        }

        var quoteFeed = ResolveQuoteFeed();
        var snapshot = await deskService.BuildAsync(
            Universe,
            quoteFeed,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromHours(4),
            cancellationToken);
        var preparationTask = preparation.GetAsync(
            snapshot.Rows.Select(row => row.Ticker).ToArray(),
            cancellationToken);

        Account = await accountReader.ReadAsync(cancellationToken);
        var summary = TradingFlow.Web.Services.Desk.DeskAccountSummary.Summarize(Account);
        if (summary is not null) Portfolio = summary;
        else Account = Account with { Available = false };
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

        PredictionHorizon = String.IsNullOrWhiteSpace(PredictionHorizon) ? "auto" : PredictionHorizon.Trim().ToLowerInvariant();

        var screenerSymbols = IsScreenerScope && ScreenerResult is { Succeeded: true } screened
            ? screened.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var earningsSymbols = snapshot.Rows
            .Where(row => row.LatestSignal?.SignalType.Contains("earnings", StringComparison.OrdinalIgnoreCase) == true)
            .Select(row => row.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = await ranking.RankAsync(
            snapshot.Rows,
            RankConfig,
            DeskHorizon,
            PredictionHorizon,
            screenerSymbols,
            earningsSymbols,
            UniverseSource,
            cancellationToken);
        Preparation = await preparationTask;

        AllRows = ranked.Rows;
        MarketEvidence = await universeEvidence.ReadAsync(AllRows, ScreenerResult, cancellationToken);
        var window = DeskGridPage<RankedDeskRow>.Create(SortRows(AllRows.Where(MatchesFilter).Where(MatchesQuery)), PageNumber, PageSize);
        Rows = window.Rows;
        MatchingCount = window.TotalMatching;
        PageNumber = window.Number;
        PageSize = window.Size;
        PageCount = window.PageCount;
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
                cancellationToken);
            await Task.WhenAll(operationalStatusTask, symbolIntelligenceTask);
            OperationalStatus = await operationalStatusTask;
            SymbolIntelligence = await symbolIntelligenceTask;

        }
    }

    private async Task<ScreenerSyncResult?> ResolveScreenerResultAsync(CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(ScreenerQuery))
        {
            return null;
        }

        return await screener.PreviewAsync(ScreenerQuery, ScreenerScope.Swing, TargetId, cancellationToken);
    }

    /// <summary>Recorded on the ranking run so an audit can tell which universe was scored.</summary>
    private string UniverseSource => Universe.Kind switch
    {
        DeskUniverseKind.AllWishlists => "wishlists:all",
        DeskUniverseKind.Screener => $"screener:{Universe.Label}",
        _ => $"wishlist:{Universe.Label}"
    };

    /// <summary>Display name of the current screener input: the catalogue label, or the input itself.</summary>
    public string ScreenerDisplayName => FinvizSignalCatalog.TryResolveInput(ScreenerQuery, out var signal)
        ? $"Finviz · {signal.Label}"
        : ScreenerResult?.Name ?? ScreenerQuery ?? "No screener";

    /// <summary>Whether the current screener input is one of the pickable entries rather than a custom query.</summary>
    public bool ScreenerIsFromPicker =>
        FinvizSignalCatalog.TryResolveInput(ScreenerQuery, out _) ||
        ScreenerPresets.Any(preset => String.Equals(preset.Name, ScreenerQuery?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// The current view state as route values, with <paramref name="overrides"/>
    /// applied. Every link and redirect on the desk goes through this, so no
    /// control can silently drop the scope, sort or filter the operator chose.
    /// Null or empty values are left out of the URL.
    /// </summary>
    public Dictionary<string, string> RouteWith(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["env"] = Env,
            ["scope"] = IsScreenerScope ? "screener" : null,
            ["id"] = IsScreenerScope ? null : Id?.ToString(),
            ["screenerQuery"] = IsScreenerScope ? ScreenerQuery : null,
            ["targetId"] = IsScreenerScope ? TargetId?.ToString() : null,
            ["source"] = String.Equals(Source, "all", StringComparison.OrdinalIgnoreCase) ? null : Source,
            ["strategyId"] = StrategyId,
            ["ticker"] = Ticker,
            ["search"] = Search,
            ["minPrice"] = MinPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxPrice"] = MaxPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxSpreadBps"] = MaxSpreadBps?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minChange"] = MinChange?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxChange"] = MaxChange?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minVolumeZ20"] = MinVolumeZ20?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxVolumeZ20"] = MaxVolumeZ20?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tracked"] = Tracked,
            ["sector"] = Sector,
            ["minMarketCap"] = MinMarketCap?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxMarketCap"] = MaxMarketCap?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minVolume"] = MinVolume?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minRvol"] = MinRvol?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxRvol"] = MaxRvol?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minAtrPercent"] = MinAtrPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxAtrPercent"] = MaxAtrPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minGap"] = MinGap?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxGap"] = MaxGap?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minVwapDistance"] = MinVwapDistance?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxVwapDistance"] = MaxVwapDistance?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minRsi"] = MinRsi?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxRsi"] = MaxRsi?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxEarningsDays"] = MaxEarningsDays?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["catalystOnly"] = CatalystOnly == true ? "true" : null,
            ["signalState"] = SignalState,
            ["pageNumber"] = PageNumber > 1 ? PageNumber.ToString() : null,
            ["pageSize"] = PageSize != 50 ? PageSize.ToString() : null,
            ["sort"] = Sort,
            ["dir"] = Dir
        };
        if (overrides.Any(x => x.Key is "sort" or "source" or "scope" or "id" or "screenerQuery")) values["pageNumber"] = null;
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values
            .Where(pair => !String.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which weight set the ranking uses. The desk horizon follows the selected
    /// strategy's own timeframe rather than a separate control, so the ordering
    /// always matches the strategy the operator is reading against.
    /// </summary>
    public string DeskHorizon => "swing";

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
        new("price", "Last", "last-column", "Mid of the inside quote with the change against the previous close.", false, true, true),
        new("change", "Chg %", "change-column", "Change against the previous session close, in percent.", true, false, true),
        new("bidask", "Bid / Ask", "quote-column", "Inside bid and ask with quoted sizes.", false, true),
        new("spread", "Spread", "spread-column", "Inside spread in basis points.", false, true, true),
        new("z20", "Volume Z20", "rvol-column", "Daily volume Z-score against its 20-session baseline; advisory evidence.", true, false, true),
        new("sector", "Sector", "evidence-column", "Sector from archived Finviz discovery export; unknown outside available rows.", true, false),
        new("cap", "Cap · $M", "evidence-column", "Market cap in USD millions from Finviz or the stored earnings provider.", true, false, true),
        new("volume", "Daily volume", "evidence-column", "Latest completed daily SIP/all session volume, not accumulating current-session volume.", true, false, true),
        new("rvol", "Daily RVOL", "evidence-column", "Alpaca daily relative volume with cohort and sample counts; separate from slot RVOL and Finviz vendor RVOL.", true, false, true),
        new("atr", "ATR %", "evidence-column", "ATR14 as a percentage of the same completed daily close.", true, false, true),
        new("gap", "Daily gap %", "evidence-column", "Latest completed daily open against the prior completed daily close.", true, false, true),
        new("vwap", "VWAP dist %", "evidence-column", "Completed warm 1m SIP/all close distance from its same-bar session VWAP.", true, false, true),
        new("rsi", "RSI14", "evidence-column", "Latest completed daily RSI14; unknown when insufficient.", true, false, true),
        new("catalyst", "Catalyst", "evidence-column", "Observed indicator catalyst; unknown does not mean absence.", true, false),
        new("earnings", "Earnings in", "evidence-column", "Days in New York to a stored future earnings schedule, within 35 days; missing calendar coverage stays unknown.", true, false, true),
        new("eligibility", "Setup watch", "eligibility-column", "Non-authorizing technical observation and its reason.", false, true),
        new("predictor", "Predictor", "predictor-column", "Model signal, probability and horizon.", false, true),
        new("sync", "Sync", "sync-column", "Whether the verdict and the model agree.", false, true),
        new("prep", "Prep", "prep-column", "Warmup preparation state: candles, indicators and catalysts cached.", true, true),
        new("pl", "Position", "position-column", "Quantity at average entry, cost and market value.", false, true, true),
        new("pnl", "P/L", "pnl-column", "Open P/L in money and percent of cost.", false, true, true),
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

    private bool MatchesQuery(RankedDeskRow ranked) => DeskGridFilter.Matches(
        new(ranked.Ticker, ranked.Row.DisplayName, ranked.Row.Quote.MidPrice, ranked.Row.SpreadBps,
            ranked.Row.ChangePct, ranked.Evidence.Swing?.VolumeZ20, ranked.Row.HasTrade, EvidenceFor(ranked.Ticker), ranked.Row.HasSignal), GridQuery);

    private IEnumerable<RankedDeskRow> SortRows(IEnumerable<RankedDeskRow> rows)
    {
        // No sort key keeps the rank order, which is the desk's own answer to
        // "what should I look at first". Sorting is an override of that, not the
        // default reading.
        var descending = SortDescending;
        return (Sort?.ToLowerInvariant()) switch
        {
            "price" => Order(rows, row => row.Row.LastPrice ?? Decimal.MinValue, descending),
            // Unknown change sorts last in both directions' natural reading: it is
            // not a small move, it is no reference.
            "change" => Order(rows, row => row.Row.ChangePct ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "bidask" => Order(rows, row => row.Row.Quote.BidPrice ?? Decimal.MinValue, descending),
            "spread" => Order(rows, row => row.Row.SpreadBps ?? Decimal.MaxValue, descending),
            "cap" => Order(rows, row => EvidenceFor(row.Ticker).MarketCapMillions ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "volume" => Order(rows, row => EvidenceFor(row.Ticker).Volume ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "rvol" => Order(rows, row => EvidenceFor(row.Ticker).DailyRvol ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "atr" => Order(rows, row => EvidenceFor(row.Ticker).AtrPercent ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "gap" => Order(rows, row => EvidenceFor(row.Ticker).GapPercent ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "vwap" => Order(rows, row => EvidenceFor(row.Ticker).VwapDistancePercent ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "rsi" => Order(rows, row => EvidenceFor(row.Ticker).Rsi ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "earnings" => Order(rows, row => EvidenceFor(row.Ticker).EarningsInDays ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "sector" => Order(rows, row => EvidenceFor(row.Ticker).Sector ?? (descending ? "" : "\uffff"), descending),
            "catalyst" => Order(rows, row => EvidenceFor(row.Ticker).HasCatalyst == true ? 1m : (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "z20" => Order(rows, row => row.Evidence.Swing?.VolumeZ20 ?? (descending ? Decimal.MinValue : Decimal.MaxValue), descending),
            "eligibility" => Order(rows, row => row.Row.HasSignal ? 1m : 0m, descending),
            "pl" => Order(rows, row => row.Row.PositionValue ?? Decimal.MinValue, descending),
            "pnl" => Order(rows, row => row.Row.Trade?.UnrealizedPl ?? Decimal.MinValue, descending),
            "prep" => Order(rows, row => (int)Preparation.For(row.Ticker).State, descending),
            "news" => Order(rows, row => row.Row.NewsTimestamp ?? DateTimeOffset.MinValue, descending),
            "ticker" => descending
                ? rows.OrderByDescending(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.Rank).ThenBy(row => row.Ticker, StringComparer.OrdinalIgnoreCase)
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

    private bool MatchesFilter(RankedDeskRow row, string? source) => source?.ToLowerInvariant() switch
    {
        "trade" => row.Row.HasTrade,
        // Predictor disagreement is advisory and cannot hide observed technical setups.
        "signal" => row.Row.HasSignal,
        "news" => row.Row.HasNews,
        "prep" => Preparation.For(row.Ticker).NeedsPreparation,
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

        return Strategies.FirstOrDefault()?.Definition.StrategyId
            ?? String.Empty;
    }

    private string ResolveQuoteFeed()
    {
        var selected = catalog.GetPaperConfigs().FirstOrDefault(config => config.Path.Equals(SelectedPaperConfigPath, StringComparison.OrdinalIgnoreCase));
        return selected?.Config.Providers.Alpaca.DataFeed ?? "sip";
    }
}
