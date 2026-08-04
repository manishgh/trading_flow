using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class OrdersModel(
    IOrderActivityQuery orders,
    AlpacaManualOrderService manualOrders,
    TradingEnvironmentService environments) : PageModel
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
        "working" => "Review a protected buy on the Trade Desk; it appears here from submission until it fills, cancels, or expires.",
        "filled" or "rejected" or "closed" => "Switch to All to see every order the journal already holds.",
        _ => "Review a protected buy on the Trade Desk. Every submitted order is journalled here through to its terminal state."
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
