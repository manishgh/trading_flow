using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class BacktestsModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly OptimizationJobService optJobs;
    private readonly BacktestJobService backtestJobs;
    private readonly IWishlistRepository wishlists;

    public BacktestsModel(
        ConfigCatalogService catalog,
        OptimizationJobService optJobs,
        BacktestJobService backtestJobs,
        IWishlistRepository wishlists)
    {
        this.catalog = catalog;
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

    private IReadOnlyList<MonthlyBacktestRow>? monthlyRows;
    public string MonthlyUnavailable { get; private set; } = "Monthly evidence unavailable.";
    public IReadOnlyList<MonthlyBacktestRow> MonthlyRows
    {
        get
        {
            if(monthlyRows is not null)return monthlyRows;
            try
            {
                var job=SelectedJob ?? throw new InvalidOperationException("No completed run selected.");
                var result=job.Result ?? throw new InvalidOperationException("Completed result unavailable.");
                if(!result.EconomicResultsComplete)throw new InvalidOperationException("Incomplete cohort; monthly returns unavailable.");
                var config=catalog.GetConfig(job.ConfigPath).Config;
                if(config.TimeWindow.Start is not { } start||config.TimeWindow.End is not { } end)
                    throw new InvalidOperationException("A frozen start and end are needed for monthly display.");
                var reader=new SimpleYamlReader();var values=new List<MonthlyBacktestRow>();
                foreach(var strategy in result.StrategyResults)
                {
                    var path=config.Strategies.FirstOrDefault(path=>reader.ReadStrategy(path).StrategyId==strategy.StrategyId);
                    if(path is null||!System.IO.File.Exists(path+".artifact.json"))
                        throw new InvalidOperationException("Immutable strategy timing metadata is missing.");
                    var manifest=System.Text.Json.JsonSerializer.Deserialize<StrategyRunArtifactManifest>(System.IO.File.ReadAllText(path+".artifact.json"),
                        new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower)}});
                    if(manifest is null || manifest.SchemaVersion != 1 || manifest.SelectionMode != StrategySelectionMode.Backtest ||
                        manifest.Identity is null || manifest.AdmissionProfile is null)
                        throw new InvalidOperationException("Immutable backtest strategy metadata is malformed or unsupported.");
                    var definition=reader.ReadStrategy(path);
                    if(TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(manifest.Identity.StrategyId,manifest.Identity.SemanticVersion,definition,manifest.AdmissionProfile))!=manifest.Identity.ContentSha256)
                        throw new InvalidOperationException("Strategy snapshot hash differs from immutable metadata.");
                    var runtime=new AuthorizedRuntimeStrategy(manifest.Identity,StrategySelectionMode.Backtest,definition,manifest.AdmissionProfile);
                    values.AddRange(MonthlyBacktestPresentation.FromCompletedTrades(strategy,runtime,job.RunName,start,end));
                }
                monthlyRows=values;return values;
            }
            catch(Exception error) when(error is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            {MonthlyUnavailable=error.Message;return monthlyRows=[];}
        }
    }
    public string? SelectedMonth => MonthlyRows.Any(row=>row.Month.Month==Month)?Month:
        MonthlyRows.Where(row=>!row.Month.PartialPeriod).Select(row=>row.Month.Month).Order(StringComparer.Ordinal).LastOrDefault()
        ?? MonthlyRows.Select(row=>row.Month.Month).Order(StringComparer.Ordinal).LastOrDefault();
    public IReadOnlyList<BacktestTrade> TradesForSelectedMonth(StrategyBacktestResult strategy)
    {
        var row=MonthlyRows.FirstOrDefault(row=>row.AliasId==strategy.StrategyId&&row.Month.Month==SelectedMonth);
        return row is null?[]:strategy.CompletedTrades.Where(trade=>
            MonthlyBacktestPresentation.SourceSessionDate(trade.ExitTimestamp).ToString("yyyy-MM",System.Globalization.CultureInfo.InvariantCulture)==row.Month.Month).ToArray();
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

    private async Task LoadDataAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        Configs = catalog.GetBacktestConfigs();
        Wishlists = await wishlists.ListAsync(cancellationToken);
        Strategies = await catalog.GetStrategiesAsync(
            StrategySelectionMode.Backtest,
            cancellationToken);
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
