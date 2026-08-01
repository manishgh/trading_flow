using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web.Pages;

public sealed class OrdersModel(IOrderActivityQuery orders) : PageModel
{
    public static IReadOnlyList<(string Key, string Label)> Filters { get; } =
    [
        ("all", "All"),
        ("working", "Working"),
        ("filled", "Filled"),
        ("rejected", "Rejected"),
        ("closed", "Cancelled / Expired")
    ];

    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "all";

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
        Items = all.Where(item => MatchesFilter(item.State, Filter)).ToArray();
    }

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
