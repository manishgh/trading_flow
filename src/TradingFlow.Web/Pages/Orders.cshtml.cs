using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Pages.Shared;
using TradingFlow.Web.Services;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

/// <summary>The Trading journal and account view; order tickets load separately on demand.</summary>
public sealed class OrdersModel(
    IOrderActivityQuery orders,
    IOrderWorkflowService workflow,
    IOrderIntentRepository intents,
    TradingEnvironmentService environments,
    TradingFlow.Web.Services.Desk.DeskSources sources,
    TradingFlow.Web.Services.Desk.IDeskAccountReader accountReader,
    OperationalStatusService operationalStatus,
    ConfigCatalogService catalog) : PageModel
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

    /// <summary>A validated symbol that opens the popup from an existing deep link.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Ticker { get; set; }

    /// <summary>Side carried by ticket deep links; the shared server workflow checks position availability.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Side { get; set; }

    /// <summary>Limit a replace carries into the ticket, from the order being replaced.</summary>
    [BindProperty(SupportsGet = true)]
    public decimal? LimitPrice { get; set; }

    /// <summary>Preserve existing strategy query context across journal filters.</summary>
    [BindProperty(SupportsGet = true)]
    public string? StrategyId { get; set; }

    /// <summary>Totals over every open position, as on the desk.</summary>
    public TradingFlow.Web.Services.Desk.DeskAccount Account { get; private set; } = new(false, "Account not read", DateTimeOffset.UtcNow, []);
    public DeskPortfolioSummary Portfolio { get; private set; } = DeskPortfolioSummary.Empty;

    public OperationalStatusSnapshot OperationalStatus { get; private set; } = new(
        "UNKNOWN", "unknown", "Status has not loaded.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "disconnected", "No quote has been received.", "UNKNOWN", "not connected",
        "attention", "Broker state has not loaded.", "blocked", "Admission state has not loaded.");

    /// <summary>Symbols offered by the new-order box: every wishlist symbol and every journalled symbol.</summary>
    public IReadOnlyList<string> SymbolChoices { get; private set; } = [];

    /// <summary>Symbols with a working order, streamed for the journal's Last column.</summary>
    public IReadOnlyList<string> StreamSymbols { get; private set; } = [];

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public HashSet<string> ReplacementClientIds { get; } = new(StringComparer.Ordinal);

    /// <summary>True once a cancel round-trip has produced an outcome to announce.</summary>
    public bool HasBanner => !String.IsNullOrWhiteSpace(StatusMessage) || !String.IsNullOrWhiteSpace(ErrorMessage);

    public IReadOnlyList<OrderActivitySnapshot> Items { get; private set; } = [];
    public int WorkingCount { get; private set; }
    public int FilledCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int ClosedCount { get; private set; }

    /// <summary>Load the journal and account facts without fetching an unopened order ticket.</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Filter = NormalizeFilter(Filter);
        var all = await orders.ListRecentAsync(limit: 500, cancellationToken: cancellationToken);
        WorkingCount = all.Count(item => IsWorking(item.State));
        FilledCount = all.Count(item => item.State == OrderState.Filled);
        RejectedCount = all.Count(item => item.State == OrderState.Rejected);
        ClosedCount = all.Count(item => IsClosed(item.State));
        Items = SortItems(all.Where(item => MatchesFilter(item.State, Filter))).ToArray();
        ReplacementClientIds.Clear();
        foreach (var item in Items.Where(x => CanCancel(x) && x.FilledQuantity is 0m))
        {
            var intent = await intents.GetByClientOrderIdAsync(item.ClientOrderId, cancellationToken);
            if (intent?.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry or OrderIntentKind.PositionExit)
                ReplacementClientIds.Add(item.ClientOrderId);
        }
        StreamSymbols = all
            .Where(item => IsWorking(item.State))
            .Select(item => item.Symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await LoadAccountAsync(all, cancellationToken);
    }

    /// <summary>
    /// Read only the account/status and symbol suggestions shown in the journal.
    /// Strategy catalog, selected-stock technicals and ticket context load on demand.
    /// </summary>
    private async Task LoadAccountAsync(IReadOnlyList<OrderActivitySnapshot> all, CancellationToken cancellationToken)
    {
        var lists = await sources.FollowedListsAsync(cancellationToken);
        var symbol = NormalizeSymbol(Ticker);
        if (!String.IsNullOrWhiteSpace(Ticker) && symbol is null)
        {
            ErrorMessage ??= $"{Ticker.Trim()} is not a symbol the ticket can open on.";
        }
        Ticker = symbol;

        var feed = catalog.DefaultQuoteFeed();
        Account = await accountReader.ReadAsync(cancellationToken);
        var summary = TradingFlow.Web.Services.Desk.DeskAccountSummary.Summarize(Account);
        if (summary is not null) Portfolio = summary;
        else Account = Account with { Available = false };
        OperationalStatus = await operationalStatus.GetAsync(
            feed,
            Array.Empty<DateTimeOffset?>(),
            cancellationToken);

        SymbolChoices = lists
            .SelectMany(list => list.Items.Where(item => item.Active).Select(item => item.Ticker))
            .Concat(all.Select(item => item.Symbol))
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
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
        string clientOrderId,
        string idempotencyKey,
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
            var intent = await intents.GetByClientOrderIdAsync(clientOrderId, cancellationToken)
                ?? throw new InvalidOperationException("The journaled order was not found.");
            var result = await workflow.CancelAsync(new CancelOrderRequest(
                ContractVersions.VersionOne, intent.AccountId, intent.IntentId,
                "Operator requested cancellation from the order journal.", idempotencyKey), cancellationToken);
            StatusMessage = $"{result.State}: {result.RejectionReason ?? "Cancellation is recorded; the journal updates when the broker confirms."}";
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
