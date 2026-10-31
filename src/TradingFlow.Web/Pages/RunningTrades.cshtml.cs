using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Contracts.Operator;
using TradingFlow.Application.Jobs;

namespace TradingFlow.Web.Pages;

public sealed class RunningTradesModel : PageModel
{
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;
    private readonly TradingEnvironmentService environments;
    private readonly IPositionProtectionReader protection;
    private readonly IDeskAccountReader account;
    private readonly TradingLimitsService limits;

    /// <summary>Uses the same account/protection readers and saved operational limits as the Positions API.</summary>
    public RunningTradesModel(
        PaperJobService paperJobs,
        MobileAutomationService automation,
        TradingEnvironmentService environments,
        IPositionProtectionReader protection,
        IDeskAccountReader account,
        TradingLimitsService limits)
    {
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.environments = environments;
        this.protection = protection;
        this.account = account;
        this.limits = limits;
    }

    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "all";

    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    /// <summary>Environment this screen is operating against, from the query segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public IReadOnlyList<AccountPosition> Trades { get; private set; } = [];

    public decimal? TotalPl { get; private set; }
    public bool AccountAvailable { get; private set; }
    public string AccountDetail { get; private set; } = "Account holdings have not been read.";
    public DateTimeOffset? AccountReadAtUtc { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Protection asserted against the broker's working orders, keyed by ticker.
    /// The local book records what was intended; this records what is actually
    /// working, and the two can differ when an entry fills but its bracket does
    /// not land.
    /// </summary>
    public IReadOnlyDictionary<string, PositionProtection> Protection { get; private set; } =
        new Dictionary<string, PositionProtection>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Σ current price × quantity across the whole book, not just the filtered
    /// source. Exposure is a portfolio fact; filtering the view must not make it
    /// look smaller than it is.
    /// </summary>
    public decimal? GrossExposure { get; private set; }

    /// <summary>Positions with no working broker stop. Unknown does not count.</summary>
    public int? UnprotectedCount { get; private set; }

    /// <summary>Positions whose protection could not be read from the broker.</summary>
    public int? UnknownProtectionCount { get; private set; }

    /// <summary>Open position count across every source.</summary>
    public int? SlotsUsed { get; private set; }

    /// <summary>The saved paper account profile's concurrent-position limit.</summary>
    public int? MaxConcurrentPositions { get; private set; }

    /// <summary>
    /// A count over the cap is a broken risk control being reported, not a
    /// figure. The screen surfaces it as a block rather than a number.
    /// </summary>
    public bool SlotsOverCap => MaxConcurrentPositions is > 0 && SlotsUsed is { } used && used > MaxConcurrentPositions.Value;

    public static IReadOnlyList<(string Key, string Label)> Sources { get; } = new[]
    {
        ("all", "All"),
        ("wishlist", "Wishlist"),
        ("stockpulse", "Stock Pulse"),
        ("manual", "Manual"),
        ("account", "Account holdings without local runs")
    };

    /// <summary>Columns the position book can be ordered by.</summary>
    public static IReadOnlyList<(string Key, string Label)> SortColumns { get; } =
    [
        ("symbol", "Ticker"),
        ("entry", "Entry"),
        ("pl", "P/L"),
        ("plpct", "P/L %")
    ];

    /// <summary>Reads account holdings even when no local job is active.</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Closes a tracked paper position after the operator confirms the reviewed exit.
    /// </summary>
    /// <remarks>
    /// The environment lock is re-checked here, not only in the view. Hiding the
    /// review control does not stop a POST that was composed by hand.
    /// </remarks>
    public async Task<IActionResult> OnPostCloseAsync(
        string closeKind,
        Guid? jobId,
        Guid? sessionId,
        string ticker,
        CancellationToken cancellationToken)
    {
        if (!EnvironmentState.IsEnabled)
        {
            ErrorMessage = EnvironmentState.LockReason;
            await LoadAsync(cancellationToken);
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
            "The broker reported no open holdings in the bound paper account.",
        _ => "Switch the source filter to All to see every broker holding."
    };

    /// <summary>Whether the empty state should point back at the unfiltered book.</summary>
    public bool EmptyStateOffersAllSources =>
        !String.IsNullOrWhiteSpace(Source) && !String.Equals(Source, "all", StringComparison.OrdinalIgnoreCase);

    /// <summary>Projects the shared account response; totals cover all holdings before filtering and remain unknown after a failed read.</summary>
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var limit = (await limits.GetAsync("paper", cancellationToken)).MaxOpenPositions;
        var snapshot = await RunningTradesBuilder.ReadAccountAsync(account,
            _ => RunningTradesBuilder.BuildAsync(paperJobs, automation), protection, limit, Source, cancellationToken);
        Trades = SortTrades(snapshot.Trades).ToArray();
        TotalPl = snapshot.TotalUnrealizedPl;
        AccountAvailable = snapshot.Available;
        AccountDetail = snapshot.Detail;
        AccountReadAtUtc = snapshot.ReadAtUtc;
        if (!snapshot.Available) ErrorMessage ??= snapshot.Detail;
        GrossExposure = snapshot.GrossExposure;
        SlotsUsed = snapshot.SlotsUsed;
        MaxConcurrentPositions = snapshot.MaxConcurrentPositions;
        UnprotectedCount = snapshot.UnprotectedCount;
        UnknownProtectionCount = snapshot.UnknownProtectionCount;
        Protection = snapshot.Trades.ToDictionary(item => item.Ticker,
            item => new PositionProtection(item.Ticker, item.HasBrokerStop, item.ProtectionKnown, item.ProtectionSummary),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Protection state for a row, or unknown when the broker was not read.</summary>
    public PositionProtection ProtectionFor(string ticker) =>
        Protection.TryGetValue(ticker, out var found)
            ? found
            : new PositionProtection(ticker, false, false, "Protection has not been read.");

    /// <summary>Orders displayed rows only, preserving nullable broker fields rather than replacing them with zero.</summary>
    private IEnumerable<AccountPosition> SortTrades(IEnumerable<AccountPosition> trades)
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

    /// <summary>Keeps equal sort values stable by stock symbol during polling.</summary>
    private static IEnumerable<AccountPosition> Order<TKey>(
        IEnumerable<AccountPosition> trades,
        Func<AccountPosition, TKey> key,
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
