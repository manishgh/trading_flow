using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Backtesting;

namespace TradingFlow.Web.Pages;

public sealed class BacktestsModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly OptimizationJobService optJobs;
    private readonly BacktestJobService backtestJobs;
    private readonly IWishlistRepository wishlists;
    private readonly TradingFlow.Web.Services.Desk.DeskSources stockSources;
    public IReadOnlyList<TradingFlow.Contracts.V1.BacktestStockSource> StockSources { get; private set; } = [];

    public BacktestsModel(
        ConfigCatalogService catalog,
        OptimizationJobService optJobs,
        BacktestJobService backtestJobs,
        IWishlistRepository wishlists, TradingFlow.Web.Services.Desk.DeskSources stockSources)
    {
        this.catalog = catalog;
        this.optJobs = optJobs;
        this.backtestJobs = backtestJobs;
        this.wishlists = wishlists;
        this.stockSources = stockSources;
    }

    [BindProperty] public string SelectedConfigPath { get; set; } = String.Empty;
    [BindProperty] public string SelectedStrategyPath { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public Guid? WishlistId { get; set; }

    /// <summary>The run this screen is showing, if any. Absent means Configure.</summary>
    [BindProperty(SupportsGet = true)] public Guid? JobId { get; set; }

    /// <summary>
    /// Strategy the matrix is drilled into, driving the analyzer and trades
    /// blocks. Carried in the query string so a drilled view survives the poll
    /// and can be shared.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? Drill { get; set; }

    [BindProperty(SupportsGet = true)] public string? Month { get; set; }

    public BacktestJobSnapshot? SelectedJob { get; private set; }

    /// <summary>
    /// Which of the three phases renders. Derived from the run status rather than
    /// from a control, so the screen cannot disagree with the worker.
    /// </summary>
    public string Phase => SelectedJob is null
        ? "configure"
        : SelectedJob.Status == "completed" ? "results" : "running";

    /// <summary>The strategy the results blocks are drilled into.</summary>
    public StrategyBacktestResult? DrilledStrategy
    {
        get
        {
            var results = SelectedJob?.Result?.StrategyResults;
            if (results is null || results.Count == 0)
            {
                return null;
            }

            return results.FirstOrDefault(item =>
                    item.StrategyId.Equals(Drill, StringComparison.OrdinalIgnoreCase))
                ?? results.OrderBy(item=>item.StrategyName,StringComparer.Ordinal).First();
        }
    }

    /// <summary>
    /// Assesses the supplied snapshot only; absent run configuration and trimmed trade details remain unevaluated.
    /// </summary>
    /// <remarks>
    /// A run built on a current wishlist fails the universe gate by construction,
    /// which is the point: a profitable run is not a promotion.
    /// </remarks>
    public ResearchPromotionAssessment? Gates => DrilledStrategy is { } strategy
        ? ResearchPromotionGates.Evaluate(strategy, null, SelectedJob?.Result?.UniversePromotion)
        : null;

    public StrategyDiagnosticReport? DrilledDiagnostic => SelectedJob?.Result?.Diagnostics
        .FirstOrDefault(item => item.StrategyId.Equals(DrilledStrategy?.StrategyId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Uses the worker's complete monthly records, never reconstructing returns from trimmed trade collections.</summary>
    public IReadOnlyList<TradingFlow.Contracts.V1.MonthlyRunResult> MonthlyRows => SelectedJob?.MonthlyResults ?? [];
    public string MonthlyUnavailable => SelectedJob?.MonthlyUnavailableReason ?? "No monthly result evidence was published for this run.";
    public string? SelectedMonth => MonthlyRows.Any(row => row.Month == Month) ? Month :
        MonthlyRows.Where(row => !row.PartialMonth).Select(row => row.Month).Order(StringComparer.Ordinal).LastOrDefault()
        ?? MonthlyRows.Select(row => row.Month).Order(StringComparer.Ordinal).LastOrDefault();
    /// <summary>Filters only the retained trade sample; monthly totals come from the separately preserved records.</summary>
    public IReadOnlyList<BacktestTrade> TradesForSelectedMonth(StrategyBacktestResult strategy)
    {
        return (SelectedJob?.Result?.CompletedTrades ?? []).Where(trade => trade.StrategyId == strategy.StrategyId &&
            MonthlyBacktestPresentation.SourceSessionDate(trade.ExitTimestamp, strategy.PortfolioState?.ValuationBasis)
                .ToString("yyyy-MM",System.Globalization.CultureInfo.InvariantCulture) == SelectedMonth).ToArray();
    }

    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<OptimizationJobSnapshot> OptimizationJobs { get; private set; } = [];
    public IReadOnlyList<BacktestJobSnapshot> BacktestJobs { get; private set; } = [];
    public IReadOnlyList<ResearchSnapshot> ResearchSnapshots { get; private set; } = [];

    /// <summary>Loads configuration and the exact persisted execution, including jobs waiting for a worker.</summary>
    public async Task OnGetAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        await LoadDataAsync(configPath, strategyPath, wishlistId, cancellationToken);
        SelectedJob = JobId is { } id ? await backtestJobs.GetSnapshotAsync(id, cancellationToken) : null;
    }

    /// <summary>
    /// Returns worker progress, preparation failures and committed candle facts for the current job.
    /// The browser patches only the existing status and evidence panel.
    /// </summary>
    public async Task<IActionResult> OnGetSnapshotAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await backtestJobs.GetSnapshotAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        var elapsed = (job.FinishedAt ?? DateTimeOffset.UtcNow) - (job.StartedAt ?? job.CreatedAt);
        var progressPercent = job.TotalTickerCount <= 0
            ? 0m
            : Math.Clamp((decimal)job.CompletedTickerCount / job.TotalTickerCount * 100m, 0m, 100m);
        return new JsonResult(new
        {
            job.JobId,
            job.Status,
            job.CurrentStage,
            job.CompletedTickerCount,
            job.TotalTickerCount,
            ProgressPercent = Math.Round(progressPercent, 1),
            Elapsed = elapsed.ToString(ElapsedFormat),
            job.ErrorMessage,
            PreparedMarketData = job.PreparedMarketData ?? job.Result?.PreparedMarketData,
            CanCancel = job.Status is "queued" or "running" or "cancelling",
            Events = job.Events.TakeLast(40),
            StrategyGroups = (job.StrategyGroups ?? [])
                .Select(group => new
                {
                    group.StrategyName,
                    group.Status,
                    group.CompletedTickerCount,
                    group.TotalTickerCount,
                    group.CurrentTicker,
                    ProgressPercent = group.TotalTickerCount <= 0
                        ? 0m
                        : Math.Round(Math.Clamp((decimal)group.CompletedTickerCount / group.TotalTickerCount * 100m, 0m, 100m), 1),
                    RecentEvents = group.RecentEvents.TakeLast(4)
                })
        });
    }

    private const string ElapsedFormat = @"hh\:mm\:ss";

    public IActionResult OnPostRunBacktest() => BadRequest("reviewed_run_required: Use the reviewed run workflow.");
    public async Task<IActionResult> OnPostCancelBacktestAsync(Guid id, CancellationToken cancellationToken)
    {
        await backtestJobs.CancelJobAsync(id, cancellationToken);
        return RedirectToPage(new
        {
            configPath = SelectedConfigPath,
            strategyPath = SelectedStrategyPath,
            wishlistId = WishlistId,
            jobId = id
        });
    }

    /// <summary>Reads catalog, operator lists and persisted recent work without creating or mutating them.</summary>
    private async Task LoadDataAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        Configs = catalog.GetBacktestConfigs();
        StockSources = await stockSources.BacktestChoicesAsync(cancellationToken);
        Wishlists = await wishlists.ListAsync(cancellationToken);
        Strategies = await catalog.GetStrategiesAsync(
            StrategySelectionMode.Backtest,
            cancellationToken);
        OptimizationJobs = optJobs.List().Take(5).ToArray();
        BacktestJobs = await backtestJobs.RecentSnapshotsAsync(5, cancellationToken);
        ResearchSnapshots = LoadResearchSnapshots();

        SelectedConfigPath = configPath ?? (Configs.FirstOrDefault()?.Path ?? String.Empty);
        SelectedStrategyPath = strategyPath ?? (Strategies.FirstOrDefault()?.Path ?? String.Empty);
        WishlistId = wishlistId ?? Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault)?.Id ?? Wishlists.FirstOrDefault()?.Id;
        SelectedWishlist = WishlistId is { } id
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == id)
            : null;
    }

    private static IReadOnlyList<ResearchSnapshot> LoadResearchSnapshots()
    {
        var repoRoot = FindRepositoryRoot();
        var root = Path.Combine(repoRoot, "data", "research", "backtests");
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "leaderboard.csv", SearchOption.AllDirectories)
            .SelectMany(path =>
            {
                var directory = Path.GetDirectoryName(path)!;
                var runId = Path.GetFileName(directory);
                var strategies = System.IO.File.ReadLines(path)
                    .Skip(1)
                    .Select(ParseLeaderboardLine)
                    .Where(x => x is not null)
                    .Cast<ResearchStrategyRow>()
                    .OrderBy(x => x.StrategyName, StringComparer.Ordinal)
                    .ToArray();

                if (strategies.Length == 0) return Array.Empty<ResearchSnapshot>();

                var universeSource = "unknown";
                var runConfigPath = Path.Combine(directory, "run_config.yaml");
                if (System.IO.File.Exists(runConfigPath))
                {
                    var configLine = System.IO.File.ReadLines(runConfigPath)
                        .FirstOrDefault(line => line.TrimStart().StartsWith("universe_source:", StringComparison.OrdinalIgnoreCase));
                    if (configLine != null)
                    {
                        var parts = configLine.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            universeSource = parts[1].Trim();
                        }
                    }
                }

                var decision = "RETAIN_RESEARCH";
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (System.IO.File.Exists(manifestPath))
                {
                    var manifestContent = System.IO.File.ReadAllText(manifestPath);
                    if (manifestContent.Contains("\"decision\"", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(manifestContent, @"""decision""\s*:\s*""([^""]+)""");
                        if (match.Success)
                        {
                            decision = match.Groups[1].Value;
                        }
                    }
                    else if (manifestContent.Contains("\"promoted_strategies\"", StringComparison.OrdinalIgnoreCase))
                    {
                        decision = "Legacy label; exact-identity authorization required";
                    }
                }

                return strategies.Select(strategy => new ResearchSnapshot(runId, directory, strategy.StrategyName, strategy.AcceptedTradeCount, universeSource, decision)).ToArray();
            })
            .Where(x => x is not null)
            .Cast<ResearchSnapshot>()
            .OrderByDescending(x => x.RunId)
            .Take(5)
            .ToArray();
    }

    private static ResearchStrategyRow? ParseLeaderboardLine(string line)
    {
        var columns = line.Split(',');
        if (columns.Length < 5) return null;

        return Decimal.TryParse(columns[1].Trim('"'), out var totalReturn) &&
            Decimal.TryParse(columns[3].Trim('"'), out var maxDrawdown) &&
            Int32.TryParse(columns[4].Trim('"'), out var trades)
            ? new ResearchStrategyRow(columns[0].Trim('"'), totalReturn, maxDrawdown, trades)
            : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "configs")) &&
                System.IO.File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private static decimal ParseDecimal(string value, decimal fallback)
    {
        return Decimal.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        return Int32.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static Guid? ParseGuid(string value)
    {
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    public sealed record ResearchSnapshot(
        string RunId,
        string Directory,
        string StrategyName,
        int AcceptedTradeCount,
        string UniverseSource,
        string Decision);

    private sealed record ResearchStrategyRow(
        string StrategyName,
        decimal TotalReturnPct,
        decimal MaxDrawdownPct,
        int AcceptedTradeCount);
}
