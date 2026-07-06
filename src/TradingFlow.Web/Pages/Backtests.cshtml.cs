using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    }

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
            ParseDecimal(form["RiskPerTradePct"].ToString(), 2m),
            ParseDecimal(form["MaxPositionValuePct"].ToString(), 25m),
            ParseInt(form["MaxConcurrentPositions"].ToString(), 4),
            form["CachePolicy"].ToString(),
            []);

        var configPath = configWriter.WriteBacktestConfig(request);
        var job = backtestJobs.Start(request.RunName, configPath);
        return RedirectToPage("/Job", new { id = job.JobId });
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
                return best is null
                    ? null
                    : new ResearchSnapshot(runId, directory, best.StrategyName, best.TotalReturnPct, best.MaxDrawdownPct, best.AcceptedTradeCount);
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
        int AcceptedTradeCount);

    private sealed record ResearchStrategyRow(
        string StrategyName,
        decimal TotalReturnPct,
        decimal MaxDrawdownPct,
        int AcceptedTradeCount);
}
