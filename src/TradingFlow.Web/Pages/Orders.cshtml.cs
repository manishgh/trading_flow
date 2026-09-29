using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages.Shared;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

/// <summary>
/// The Orders screen: a subset of the desk. The order journal takes the place of
/// the market grid, and the desk's rail - quote, position and the reviewed
/// ticket - sits beside it, so another order can be placed, or one replaced,
/// without leaving the journal.
/// </summary>
public sealed class OrdersModel(
    IOrderActivityQuery orders,
    AlpacaManualOrderService manualOrders,
    TradingEnvironmentService environments,
    IWishlistRepository wishlists,
    WishlistDeskService desk,
    ManualOrderTicketService tickets,
    OperationalStatusService operationalStatus,
    ConfigCatalogService catalog,
    DeskTicketPlanService ticketPlans) : PageModel
{
    public static IReadOnlyList<(string Key, string Label)> Filters { get; } =
    [
        ("all", "All"),
        ("working", "Working"),
        ("filled", "Filled"),
        ("rejected", "Rejected"),
        ("closed", "Cancelled / Expired")
    ];

    /// <summary>Columns the journal table can be ordered by.</summary>
    public static IReadOnlyList<(string Key, string Label)> SortColumns { get; } =
    [
        ("updated", "Updated"),
        ("symbol", "Symbol"),
        ("status", "Status"),
        ("requested", "Requested"),
        ("filled", "Filled")
    ];

    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Env { get; set; }

    /// <summary>The symbol the rail and ticket are open on, or null for none.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Ticker { get; set; }

    /// <summary>Side a replace opens the ticket on. Only a held position can open on sell.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Side { get; set; }

    /// <summary>Limit a replace carries into the ticket, from the order being replaced.</summary>
    [BindProperty(SupportsGet = true)]
    public decimal? LimitPrice { get; set; }

    /// <summary>
    /// Strategy the ticket's stop and target follow, as on the desk. Defaults to
    /// the first strategy the environment may run, the desk's own default.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? StrategyId { get; set; }

    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];

    /// <summary>The selected symbol's quote, position and wishlist membership.</summary>
    public WishlistDeskRow? SelectedRow { get; private set; }

    /// <summary>The selected symbol's most recent orders, newest first.</summary>
    public IReadOnlyList<OrderActivitySnapshot> SelectedOrders { get; private set; } = [];

    /// <summary>Totals over every open position, as on the desk.</summary>
    public DeskPortfolioSummary Portfolio { get; private set; } = DeskPortfolioSummary.Empty;

    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "UNKNOWN", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "disconnected", "No quote has been received.", "UNKNOWN", "not connected",
        "attention", "Broker state has not loaded.", "blocked", "Admission state has not loaded.");

    /// <summary>Symbols offered by the new-order box: every wishlist symbol and every journalled symbol.</summary>
    public IReadOnlyList<string> SymbolChoices { get; private set; } = [];

    /// <summary>Symbols with a working order, streamed for the journal's Last column.</summary>
    public IReadOnlyList<string> StreamSymbols { get; private set; } = [];

    public ManualOrderTicketPreview? TicketPreview { get; private set; }
    public ManualOrderTicketConfirmation? TicketConfirmation { get; private set; }

    /// <summary>
    /// Opening limit, stop and target for the rail ticket, from the selected
    /// strategy's own rules on live market state. Empty values carry a reason.
    /// </summary>
    public DeskTicketPlan TicketPlan { get; private set; } =
        DeskTicketPlan.Unavailable(null, "No symbol selected.", null);

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>True once a cancel round-trip has produced an outcome to announce.</summary>
    public bool HasBanner => !String.IsNullOrWhiteSpace(StatusMessage) || !String.IsNullOrWhiteSpace(ErrorMessage);

    public IReadOnlyList<OrderActivitySnapshot> Items { get; private set; } = [];
    public int WorkingCount { get; private set; }
    public int FilledCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int ClosedCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Filter = NormalizeFilter(Filter);
        var all = await orders.ListRecentAsync(limit: 500, cancellationToken: cancellationToken);
        WorkingCount = all.Count(item => IsWorking(item.State));
        FilledCount = all.Count(item => item.State == OrderState.Filled);
        RejectedCount = all.Count(item => item.State == OrderState.Rejected);
        ClosedCount = all.Count(item => IsClosed(item.State));
        Items = SortItems(all.Where(item => MatchesFilter(item.State, Filter))).ToArray();
        StreamSymbols = all
            .Where(item => IsWorking(item.State))
            .Select(item => item.Symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await LoadRailAsync(all, cancellationToken);
    }

    /// <summary>
    /// Loads the rail the same way the desk does - quote, position and lists from
    /// the desk service - for the one symbol in view, plus the account strip.
    /// </summary>
    private async Task LoadRailAsync(IReadOnlyList<OrderActivitySnapshot> all, CancellationToken cancellationToken)
    {
        var lists = await wishlists.ListAsync(cancellationToken);
        var symbol = NormalizeSymbol(Ticker);
        if (!String.IsNullOrWhiteSpace(Ticker) && symbol is null)
        {
            ErrorMessage ??= $"{Ticker.Trim()} is not a symbol the ticket can open on.";
        }
        Ticker = symbol;

        Strategies = await catalog.GetStrategiesAsync(
            environments.Parse(Env) == TradingEnvironment.Live
                ? StrategySelectionMode.RunLive
                : StrategySelectionMode.RunPaperExperiment,
            cancellationToken);
        StrategyId = Strategies.FirstOrDefault(option =>
                option.Definition.StrategyId.Equals(StrategyId, StringComparison.OrdinalIgnoreCase))?.Definition.StrategyId
            ?? Strategies.FirstOrDefault()?.Definition.StrategyId;

        var feed = catalog.DefaultQuoteFeed();
        var universe = symbol is null
            ? DeskUniverse.Empty
            : DeskUniverse.ForSymbols("Orders", [symbol], lists);
        var snapshot = await desk.BuildAsync(
            universe,
            feed,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromHours(4),
            cancellationToken);
        Portfolio = snapshot.Portfolio;
        SelectedRow = snapshot.Rows.FirstOrDefault();
        OperationalStatus = await operationalStatus.GetAsync(
            feed,
            snapshot.Rows.Select(row => row.Quote.Timestamp),
            cancellationToken);

        SymbolChoices = lists
            .SelectMany(list => list.Items.Where(item => item.Active).Select(item => item.Ticker))
            .Concat(all.Select(item => item.Symbol))
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (SelectedRow is { } row)
        {
            SelectedOrders = all
                .Where(item => item.Symbol.Equals(row.Ticker, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(8)
                .ToArray();
            var profile = catalog.DefaultPaperConfig();
            TicketPlan = await ticketPlans.PlanAsync(
                Strategies.FirstOrDefault(option => option.Definition.StrategyId == StrategyId)?.Definition,
                row.Ticker,
                row.Quote.AskPrice,
                row.Quote.MidPrice,
                LimitPrice,
                profile?.Config.Engine.IndicatorWarmupBars ?? 0,
                DeskTicketPlanService.RiskBudget(OperationalStatus.Equity, profile?.Config.Portfolio.AccountRiskBudgetPct),
                cancellationToken);
        }
    }

    /// <summary>Side the rail ticket opens on: sell only for a replace of a sell.</summary>
    public string TicketSide =>
        String.Equals(Side, "sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";

    /// <summary>
    /// Stage one of the rail ticket - the same server review the desk uses. The
    /// environment lock is checked here, not only in the view.
    /// </summary>
    public async Task OnPostPreviewTicketAsync(
        [FromForm] DeskTicketForm ticket,
        CancellationToken cancellationToken)
    {
        await OnGetAsync(cancellationToken);
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

    /// <summary>Stage two: confirms against the token the preview issued, or fails closed.</summary>
    public async Task OnPostConfirmTicketAsync(string? ticketToken, CancellationToken cancellationToken)
    {
        if (!EnvironmentState.IsEnabled)
        {
            await OnGetAsync(cancellationToken);
            ErrorMessage = EnvironmentState.LockReason;
            return;
        }

        try
        {
            TicketConfirmation = await tickets.ConfirmAsync(ticketToken ?? String.Empty, cancellationToken);
            StatusMessage = $"Paper order accepted for {TicketConfirmation.Ticker}. It appears in the journal below.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }

        // Reload after the confirm so the journal already carries the new order.
        await OnGetAsync(cancellationToken);
    }

    /// <summary>
    /// The current view as route values with <paramref name="overrides"/> applied,
    /// so selecting a symbol, sorting or filtering never drops the others.
    /// </summary>
    public Dictionary<string, string> RouteWith(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["env"] = Env,
            ["filter"] = Filter == "all" ? null : Filter,
            ["sort"] = Sort,
            ["dir"] = Dir,
            ["ticker"] = Ticker,
            ["strategyId"] = StrategyId
        };
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values
            .Where(pair => !String.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One symbol from operator input, or null when it is not a valid equity symbol.</summary>
    internal static string? NormalizeSymbol(string? input)
    {
        var parsed = DeskStreamTickers.Parse(input?.Trim());
        return parsed.Count == 1 ? parsed.First() : null;
    }

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

    /// <summary>Headline for the empty state of the active filter.</summary>
    public string EmptyStateTitle => Filter switch
    {
        "working" => "No order is working right now.",
        "filled" => "No order has filled yet.",
        "rejected" => "No order has been rejected.",
        "closed" => "No order has been cancelled or has expired.",
        _ => "The order journal is empty."
    };

    /// <summary>Names the action that puts rows into the active filter.</summary>
    public string EmptyStateHint => Filter switch
    {
        "working" => "Open a ticket under New order, or review one on the desk; it appears here from submission until it fills, cancels, or expires.",
        "filled" or "rejected" or "closed" => "Switch to All to see every order the journal already holds.",
        _ => "Open a ticket under New order, or review one on the desk. Every submitted order is journalled here through to its terminal state."
    };

    /// <summary>Whether the empty state should point back at the unfiltered list.</summary>
    public bool EmptyStateOffersAllFilter => Filter is "filled" or "rejected" or "closed";

    private IEnumerable<OrderActivitySnapshot> SortItems(IEnumerable<OrderActivitySnapshot> items)
    {
        // No sort key means the journal's own recency order, which is what an
        // operator watching a live book expects to see on arrival.
        var descending = SortDescending;
        return Sort?.Trim().ToLowerInvariant() switch
        {
            "updated" => Order(items, item => item.UpdatedAtUtc, descending),
            "symbol" => Order(items, item => item.Symbol, descending, StringComparer.OrdinalIgnoreCase),
            // Status orders by lifecycle position, not by label, so working orders
            // group together instead of scattering alphabetically.
            "status" => Order(items, item => (int)item.State, descending),
            "requested" => Order(items, item => item.RequestedQuantity, descending),
            "filled" => Order(items, item => item.FilledQuantity ?? 0m, descending),
            _ => items
        };
    }

    private static IEnumerable<OrderActivitySnapshot> Order<TKey>(
        IEnumerable<OrderActivitySnapshot> items,
        Func<OrderActivitySnapshot, TKey> key,
        bool descending,
        IComparer<TKey>? comparer = null)
    {
        // Recency is the tiebreaker so equal values keep a stable order instead of
        // shuffling between polls.
        return descending
            ? items.OrderByDescending(key, comparer).ThenByDescending(item => item.UpdatedAtUtc)
            : items.OrderBy(key, comparer).ThenByDescending(item => item.UpdatedAtUtc);
    }

    /// <summary>
    /// Cancels a working broker order.
    /// </summary>
    /// <remarks>
    /// The environment lock is checked here, not only in the view. Hiding a button
    /// does not stop a POST.
    /// </remarks>
    public async Task<IActionResult> OnPostCancelAsync(
        string brokerOrderId,
        CancellationToken cancellationToken)
    {
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            await OnGetAsync(cancellationToken);
            return Page();
        }

        try
        {
            var cancelled = await manualOrders.CancelOrderAsync(brokerOrderId, cancellationToken);
            StatusMessage = cancelled
                ? "Cancel request accepted. The order journal will show the terminal state once the broker confirms."
                : "The broker did not accept the cancel request; the order may already be terminal.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }

        await OnGetAsync(cancellationToken);
        return Page();
    }

    /// <summary>Whether an order is still working and therefore cancellable.</summary>
    public static bool CanCancel(OrderActivitySnapshot item) =>
        IsWorking(item.State) &&
        item.State != OrderState.CancelPending &&
        !String.IsNullOrWhiteSpace(item.BrokerOrderId);

    public static bool MatchesFilter(OrderState state, string filter) => filter switch
    {
        "working" => IsWorking(state),
        "filled" => state == OrderState.Filled,
        "rejected" => state == OrderState.Rejected,
        "closed" => IsClosed(state),
        _ => true
    };

    private static bool IsWorking(OrderState state) => state is
        OrderState.Intent or
        OrderState.Submitted or
        OrderState.Acked or
        OrderState.PartiallyFilled or
        OrderState.CancelPending;

    private static bool IsClosed(OrderState state) => state is
        OrderState.Canceled or
        OrderState.Expired;

    private static string NormalizeFilter(string? filter)
    {
        var normalized = filter?.Trim().ToLowerInvariant() ?? "all";
        return Filters.Any(item => item.Key == normalized) ? normalized : "all";
    }
}
