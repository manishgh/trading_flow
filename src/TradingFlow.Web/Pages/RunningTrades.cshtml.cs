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
    private readonly PositionProtectionService protection;
    private readonly ConfigCatalogService catalog;

    public RunningTradesModel(
        PaperJobService paperJobs,
        MobileAutomationService automation,
        TradingEnvironmentService environments,
        PositionProtectionService protection,
        ConfigCatalogService catalog)
    {
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.environments = environments;
        this.protection = protection;
        this.catalog = catalog;
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
    public decimal GrossExposure { get; private set; }

    /// <summary>Positions with no working broker stop. Unknown does not count.</summary>
    public int UnprotectedCount { get; private set; }

    /// <summary>Positions whose protection could not be read from the broker.</summary>
    public int UnknownProtectionCount { get; private set; }

    /// <summary>Open position count across every source.</summary>
    public int SlotsUsed { get; private set; }

    /// <summary>The real cap from the active paper profile's portfolio config.</summary>
    public int MaxConcurrentPositions { get; private set; }

    /// <summary>
    /// A count over the cap is a broken risk control being reported, not a
    /// figure. The screen surfaces it as a block rather than a number.
    /// </summary>
    public bool SlotsOverCap => MaxConcurrentPositions > 0 && SlotsUsed > MaxConcurrentPositions;

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
            "Start a run from Paper Lab, or review a protected buy on the Trade Desk. Positions appear here as soon as an entry fills.",
        _ => "Switch the source filter to All, or start a run from Paper Lab that trades this source."
    };

    /// <summary>Whether the empty state should point back at the unfiltered book.</summary>
    public bool EmptyStateOffersAllSources =>
        !String.IsNullOrWhiteSpace(Source) && !String.Equals(Source, "all", StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var all = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        Trades = SortTrades(RunningTradesBuilder.Filter(all, Source)).ToArray();
        TotalPl = Trades.Sum(trade => trade.UnrealizedPl);

        // Exposure and slots are portfolio facts, so they are measured across the
        // whole book. Filtering to one source narrows what is listed, never what
        // the account is actually carrying.
        GrossExposure = all.Sum(trade => trade.CurrentPrice * trade.Quantity);
        SlotsUsed = all.Count;
        MaxConcurrentPositions = ResolveMaxConcurrentPositions();

        Protection = await protection.GetAsync(
            all.Select(trade => trade.Ticker).ToArray(),
            cancellationToken);
        UnprotectedCount = Protection.Values.Count(item => item.IsKnown && !item.HasBrokerStop);
        UnknownProtectionCount = Protection.Values.Count(item => !item.IsKnown);
    }

    /// <summary>Protection state for a row, or unknown when the broker was not read.</summary>
    public PositionProtection ProtectionFor(string ticker) =>
        Protection.TryGetValue(ticker, out var found)
            ? found
            : new PositionProtection(ticker, false, false, "Protection has not been read.");

    /// <summary>
    /// The cap the risk gate actually enforces, from the active paper profile.
    /// Zero means it could not be read, and the screen says unknown rather than
    /// implying an unlimited book.
    /// </summary>
    private int ResolveMaxConcurrentPositions()
    {
        var configs = catalog.GetPaperConfigs();
        var selected = configs.FirstOrDefault(config =>
                config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase))
            ?? configs.FirstOrDefault();
        return selected?.Config.Portfolio.MaxConcurrentPositions ?? 0;
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
