using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class BacktestsModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly RunConfigWriter configWriter;
    private readonly OptimizationJobService optJobs;
    private readonly BacktestJobService backtestJobs;
    private readonly IWishlistRepository wishlists;

    public BacktestsModel(
        ConfigCatalogService catalog,
        RunConfigWriter configWriter,
        OptimizationJobService optJobs,
        BacktestJobService backtestJobs,
        IWishlistRepository wishlists)
    {
        this.catalog = catalog;
        this.configWriter = configWriter;
        this.optJobs = optJobs;
        this.backtestJobs = backtestJobs;
        this.wishlists = wishlists;
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

    /// <summary>Metric the leaderboard is ranked on.</summary>
    [BindProperty(SupportsGet = true)] public string Metric { get; set; } = "return";

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
                ?? results.FirstOrDefault(item =>
                    item.StrategyId.Equals(SelectedJob!.Result!.Winner?.StrategyId, StringComparison.OrdinalIgnoreCase))
                ?? results[0];
        }
    }

    /// <summary>
    /// The seven preregistered gates for the drilled strategy.
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

    /// <summary>Metrics the leaderboard can be ranked on.</summary>
    public static IReadOnlyList<(string Key, string Label)> Metrics { get; } =
    [
        ("return", "Total return %"),
        ("daily", "Avg daily return %"),
        ("net", "Net profit"),
        ("drawdown", "Max drawdown %"),
        ("winrate", "Win rate %")
    ];

    /// <summary>
    /// Strategies ordered on the chosen metric. Drawdown sorts ascending because
    /// less is better; everything else descending.
    /// </summary>
    public IReadOnlyList<StrategyBacktestResult> Leaderboard
    {
        get
        {
            var results = SelectedJob?.Result?.StrategyResults ?? [];
            return Metric switch
            {
                "daily" => results.OrderByDescending(item => item.AverageDailyReturnPct).ToArray(),
                "net" => results.OrderByDescending(item => item.NetProfit).ToArray(),
                "drawdown" => results.OrderBy(item => item.MaxDrawdownPct).ToArray(),
                "winrate" => results.OrderByDescending(WinRatePct).ToArray(),
                _ => results.OrderByDescending(item => item.TotalReturnPct).ToArray()
            };
        }
    }

    public static decimal WinRatePct(StrategyBacktestResult strategy)
    {
        var decided = strategy.WinningTradeCount + strategy.LosingTradeCount;
        return decided <= 0 ? 0m : (decimal)strategy.WinningTradeCount / decided * 100m;
    }

    /// <summary>The value the leaderboard bar and figure show for one strategy.</summary>
    public decimal MetricValue(StrategyBacktestResult strategy) => Metric switch
    {
        "daily" => strategy.AverageDailyReturnPct,
        "net" => strategy.NetProfit,
        "drawdown" => strategy.MaxDrawdownPct,
        "winrate" => WinRatePct(strategy),
        _ => strategy.TotalReturnPct
    };

    /// <summary>
    /// Rank of <paramref name="strategy"/> on one matrix metric, 1 being best.
    /// Cell tint encodes rank rather than absolute value so the table stays
    /// readable whatever the scale.
    /// </summary>
    public int RankOn(string metric, StrategyBacktestResult strategy)
    {
        var results = SelectedJob?.Result?.StrategyResults ?? [];
        Func<StrategyBacktestResult, decimal> selector = metric switch
        {
            "daily" => item => item.AverageDailyReturnPct,
            "net" => item => item.NetProfit,
            "drawdown" => item => -item.MaxDrawdownPct,
            "trades" => item => item.AcceptedTradeCount,
            "winrate" => WinRatePct,
            "hold" => item => (decimal)(BacktestDerivedMetrics.AverageHold(item)?.TotalMinutes ?? 0),
            "profitfactor" => item => BacktestDerivedMetrics.ProfitFactor(item) ?? 0m,
            _ => item => item.TotalReturnPct
        };
        var ordered = results.OrderByDescending(selector).ToArray();
        var index = Array.FindIndex(ordered, item => item.StrategyId == strategy.StrategyId);
        return index < 0 ? results.Count : index + 1;
    }

    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<OptimizationJobSnapshot> OptimizationJobs { get; private set; } = [];
    public IReadOnlyList<BacktestJobSnapshot> BacktestJobs { get; private set; } = [];
    public IReadOnlyList<ResearchSnapshot> ResearchSnapshots { get; private set; } = [];

    public async Task OnGetAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        await LoadDataAsync(configPath, strategyPath, wishlistId, cancellationToken);
        SelectedJob = JobId is { } id ? backtestJobs.Get(id) : null;
    }

    /// <summary>
    /// Progress poll for the running phase. Returns the same projection the
    /// previous job page used, so the cadence and the reload-on-first-completion
    /// behaviour are unchanged.
    /// </summary>
    public IActionResult OnGetSnapshot(Guid id)
    {
        var job = backtestJobs.Get(id);
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

    public IActionResult OnPostSaveStrategy()
    {
        var form = Request.Form;
        var strategyPath = form["SaveStrategyPath"].ToString();

        if (!String.IsNullOrWhiteSpace(strategyPath))
        {
            var overrides = ParseStrategyOverrides(form, strategyPath);
            if (overrides is null) return RedirectToPage();
            
            configWriter.SaveStrategyPermanent(strategyPath, overrides);
        }

        return RedirectToPage(new { configPath = form["SelectedConfigPath"].ToString(), strategyPath });
    }

    public IActionResult OnPostOptimize()
    {
        var form = Request.Form;
        var request = new OptimizationRunRequest(
            form["SelectedStrategyPath"].ToString(),
            form["SelectedConfigPath"].ToString(),
            form["RunName"].ToString(),
            form["Metric"].ToString(),
            form["TargetRMultipleCsv"].ToString(),
            form["StopAtrMultipleCsv"].ToString(),
            form["MinVolumeSpikeCsv"].ToString());

        var configPath = configWriter.WriteOptimizationConfig(request);
        var job = optJobs.Start(request.RunName, configPath);
        return RedirectToPage("/OptimizationJob", new { id = job.JobId });
    }

    public async Task<IActionResult> OnPostRunBacktestAsync(CancellationToken cancellationToken)
    {
        var form = Request.Form;
        var baseConfigPath = form["SelectedConfigPath"].ToString();
        var wishlistIdValue = ParseGuid(form["WishlistId"].ToString());
        var strategyPaths = form["StrategyPaths"]
            .Select(x => x?.Trim())
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();

        if (String.IsNullOrWhiteSpace(baseConfigPath) || strategyPaths.Length == 0 || wishlistIdValue is null)
        {
            return RedirectToPage(new { configPath = baseConfigPath, strategyPath = form["SelectedStrategyPath"].ToString(), wishlistId = wishlistIdValue });
        }

        var wishlist = await wishlists.GetByIdAsync(wishlistIdValue.Value, cancellationToken);
        if (wishlist is null)
        {
            return RedirectToPage(new { configPath = baseConfigPath, strategyPath = form["SelectedStrategyPath"].ToString() });
        }

        var tickers = wishlist.Items
            .Where(item => item.Active)
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tickers.Length == 0)
        {
            return RedirectToPage(new { configPath = baseConfigPath, strategyPath = form["SelectedStrategyPath"].ToString(), wishlistId = wishlistIdValue });
        }

        var request = new BacktestRunRequest(
            baseConfigPath,
            form["RunName"].ToString(),
            ParseInt(form["LookbackDays"].ToString(), 180),
            wishlist.Id,
            wishlist.Name,
            tickers,
            strategyPaths,
            ParseDecimal(form["StartingCapital"].ToString(), 10000m),
            ParseDecimal(form["AccountRiskBudgetPct"].ToString(), 1m),
            ParseDecimal(form["MaxPositionNotionalPct"].ToString(), 25m),
            ParseInt(form["MaxConcurrentPositions"].ToString(), 4),
            form["CachePolicy"].ToString(),
            []);

        var configPath = configWriter.WriteBacktestConfig(request);
        var job = backtestJobs.Start(request.RunName, configPath);
        // The run stays on this screen: Configure, Running and Results are three
        // phases of one place rather than a handoff to a second page.
        return RedirectToPage(new { jobId = job.JobId, wishlistId = wishlist.Id });
    }

    public IActionResult OnPostCancelBacktest(Guid id)
    {
        backtestJobs.CancelJob(id);
        return RedirectToPage(new
        {
            configPath = SelectedConfigPath,
            strategyPath = SelectedStrategyPath,
            wishlistId = WishlistId,
            jobId = id
        });
    }

    private async Task LoadDataAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        Configs = catalog.GetBacktestConfigs();
        Wishlists = await wishlists.ListAsync(cancellationToken);
        Strategies = catalog.GetStrategies();
        OptimizationJobs = optJobs.List().Take(5).ToArray();
        BacktestJobs = backtestJobs.List().Take(5).ToArray();
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
            .Select(path =>
            {
                var directory = Path.GetDirectoryName(path)!;
                var runId = Path.GetFileName(directory);
                var best = System.IO.File.ReadLines(path)
                    .Skip(1)
                    .Select(ParseLeaderboardLine)
                    .Where(x => x is not null)
                    .Cast<ResearchStrategyRow>()
                    .OrderByDescending(x => x.TotalReturnPct)
                    .FirstOrDefault();
                
                if (best is null) return null;

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
                        decision = "PROMOTED";
                    }
                }

                return new ResearchSnapshot(runId, directory, best.StrategyName, best.TotalReturnPct, best.MaxDrawdownPct, best.AcceptedTradeCount, universeSource, decision);
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

    private static decimal? ParseNullableDecimal(string value)
    {
        return Decimal.TryParse(value, out var parsed) ? parsed : null;
    }

    private static StrategyParameterOverride? ParseStrategyOverrides(Microsoft.AspNetCore.Http.IFormCollection form, string strategyPath)
    {
        if (String.IsNullOrWhiteSpace(strategyPath)) return null;

        return new StrategyParameterOverride(
            strategyPath,
            ParseDecimal(form["MinVolumeSpike"].ToString(), 1m),
            ParseDecimal(form["MinEntryRsi"].ToString(), 0m),
            ParseDecimal(form["MaxEntryRsi"].ToString(), 100m),
            ParseNullableDecimal(form["MaxVwapExtensionAtr"].ToString()),
            form.ContainsKey("ConfluenceEnabled"),
            form["ConfluenceTimeframe"].ToString(),
            ParseInt(form["ConfluenceEmaPeriod"].ToString(), 50),
            ParseDecimal(form["StopAtrMultiple"].ToString(), 2m),
            ParseDecimal(form["TargetRMultiple"].ToString(), 2m),
            ParseDecimal(form["MaxHoldHours"].ToString(), 24m),
            form.ContainsKey("EnableAtrTrailingStop"),
            ParseDecimal(form["TrailingStopAtrMultiple"].ToString(), 2m),
            ParseDecimal(form["TrailingActivationR"].ToString(), 1m),
            ParseInt(form["MinHoldBarsBeforeTechnicalExit"].ToString(), 1),
            form["ExecutionTimeframe"].ToString(),
            ParseDecimal(form["SlippageBps"].ToString(), 0m));
    }

    public sealed record ResearchSnapshot(
        string RunId,
        string Directory,
        string BestStrategyName,
        decimal TotalReturnPct,
        decimal MaxDrawdownPct,
        int AcceptedTradeCount,
        string UniverseSource,
        string Decision);

    private sealed record ResearchStrategyRow(
        string StrategyName,
        decimal TotalReturnPct,
        decimal MaxDrawdownPct,
        int AcceptedTradeCount);
}
