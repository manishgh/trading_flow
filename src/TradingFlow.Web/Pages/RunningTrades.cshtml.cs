using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class RunningTradesModel : PageModel
{
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;
    private readonly TradingEnvironmentService environments;

    public RunningTradesModel(
        PaperJobService paperJobs,
        MobileAutomationService automation,
        TradingEnvironmentService environments)
    {
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.environments = environments;
    }

    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";

    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    /// <summary>Environment this screen is operating against, from the query segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public IReadOnlyList<MobileRunningTrade> Trades { get; private set; } = Array.Empty<MobileRunningTrade>();

    public decimal TotalPl { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static IReadOnlyList<(string Key, string Label)> Sources { get; } = new[]
    {
        ("all", "All"),
        ("wishlist", "Wishlist"),
        ("stockpulse", "Stock Pulse"),
        ("manual", "Manual")
    };

    /// <summary>Columns the position book can be ordered by.</summary>
    public static IReadOnlyList<(string Key, string Label)> SortColumns { get; } =
    [
        ("symbol", "Ticker"),
        ("entry", "Entry"),
        ("pl", "P/L"),
        ("plpct", "P/L %")
    ];

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    /// <summary>
    /// Closes a tracked paper position after the operator confirms the reviewed exit.
    /// </summary>
    /// <remarks>
    /// The environment lock is re-checked here, not only in the view. Hiding the
    /// review control does not stop a POST that was composed by hand.
    /// </remarks>
    public async Task<IActionResult> OnPostCloseAsync(string closeKind, Guid? jobId, Guid? sessionId, string ticker)
    {
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            await LoadAsync();
            return Page();
        }

        if (closeKind == "paper_job" && jobId is { } job)
        {
            await paperJobs.ClosePositionAsync(job, ticker);
        }
        else if (closeKind == "automation" && sessionId is { } session)
        {
            await automation.CloseAsync(session);
        }

        return RedirectToPage(new { Source, sort = Sort, dir = Dir, env = Env });
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

    /// <summary>Names the action that puts rows into the active source filter.</summary>
    public string EmptyStateHint => Source?.ToLowerInvariant() switch
    {
        "all" or null or "" =>
            "Start a run from Paper Lab, or review a protected buy on the Trade Desk. Positions appear here as soon as an entry fills.",
        _ => "Switch the source filter to All, or start a run from Paper Lab that trades this source."
    };

    /// <summary>Whether the empty state should point back at the unfiltered book.</summary>
    public bool EmptyStateOffersAllSources =>
        !String.IsNullOrWhiteSpace(Source) && !String.Equals(Source, "all", StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync()
    {
        var all = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        Trades = SortTrades(RunningTradesBuilder.Filter(all, Source)).ToArray();
        TotalPl = Trades.Sum(trade => trade.UnrealizedPl);
    }

    private IEnumerable<MobileRunningTrade> SortTrades(IEnumerable<MobileRunningTrade> trades)
    {
        // No sort key keeps the builder's own order, so an operator who never touches
        // a header sees the same book they saw before sorting existed.
        var descending = SortDescending;
        return Sort?.Trim().ToLowerInvariant() switch
        {
            "symbol" => Order(trades, trade => trade.Ticker, descending, StringComparer.OrdinalIgnoreCase),
            "entry" => Order(trades, trade => trade.EntryPrice, descending),
            "pl" => Order(trades, trade => trade.UnrealizedPl, descending),
            "plpct" => Order(trades, trade => trade.UnrealizedPlPct, descending),
            _ => trades
        };
    }

    private static IEnumerable<MobileRunningTrade> Order<TKey>(
        IEnumerable<MobileRunningTrade> trades,
        Func<MobileRunningTrade, TKey> key,
        bool descending,
        IComparer<TKey>? comparer = null)
    {
        // Ticker is the tiebreaker so equal values keep a stable order instead of
        // shuffling between the ten-second value polls.
        return descending
            ? trades.OrderByDescending(key, comparer).ThenBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
            : trades.OrderBy(key, comparer).ThenBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase);
    }
}
