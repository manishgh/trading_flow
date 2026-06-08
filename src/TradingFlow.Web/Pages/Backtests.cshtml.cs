using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class BacktestsModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly RunConfigWriter configWriter;
    private readonly OptimizationJobService optJobs;
    private readonly BacktestJobService backtestJobs;

    public BacktestsModel(ConfigCatalogService catalog, RunConfigWriter configWriter, OptimizationJobService optJobs, BacktestJobService backtestJobs)
    {
        this.catalog = catalog;
        this.configWriter = configWriter;
        this.optJobs = optJobs;
        this.backtestJobs = backtestJobs;
    }

    [BindProperty] public string SelectedConfigPath { get; set; } = String.Empty;
    [BindProperty] public string SelectedStrategyPath { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public string? TickersCsv { get; set; }

    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<OptimizationJobSnapshot> OptimizationJobs { get; private set; } = [];
    public IReadOnlyList<BacktestJobSnapshot> BacktestJobs { get; private set; } = [];

    public void OnGet(string? configPath, string? strategyPath, string? tickersCsv)
    {
        LoadData(configPath, strategyPath);
        
        if (tickersCsv != null)
        {
            TickersCsv = tickersCsv;
        }
        else
        {
            var selectedConfig = Configs.FirstOrDefault(c => c.Path == SelectedConfigPath);
            TickersCsv = selectedConfig != null ? String.Join(", ", selectedConfig.Config.Tickers) : "";
        }
    }

    public IActionResult OnPostSaveConfig()
    {
        var form = Request.Form;
        var baseConfigPath = form["BaseConfigPath"].ToString();
        var tickersCsv = form["TickersCsv"].ToString();
        var tickers = tickersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tickers.Length > 0 && !String.IsNullOrWhiteSpace(baseConfigPath))
        {
            var strategyPath = form["SelectedStrategyPath"].ToString();
            var newPath = configWriter.SaveTempConfig(baseConfigPath, tickers, strategyPath);
            return RedirectToPage(new { configPath = newPath, strategyPath = strategyPath });
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDeleteConfig()
    {
        var form = Request.Form;
        var configPath = form["DeleteConfigPath"].ToString();
        if (!String.IsNullOrWhiteSpace(configPath))
        {
            configWriter.DeleteTempConfig(configPath);
        }
        return RedirectToPage(new { strategyPath = form["SelectedStrategyPath"].ToString() });
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

    private void LoadData(string? configPath, string? strategyPath)
    {
        Configs = catalog.GetBacktestConfigs();
        Strategies = catalog.GetStrategies();
        OptimizationJobs = optJobs.List().Take(5).ToArray();
        BacktestJobs = backtestJobs.List().Take(5).ToArray();

        SelectedConfigPath = configPath ?? (Configs.FirstOrDefault()?.Path ?? String.Empty);
        SelectedStrategyPath = strategyPath ?? (Strategies.FirstOrDefault()?.Path ?? String.Empty);
    }

    private static decimal ParseDecimal(string value, decimal fallback)
    {
        return Decimal.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        return Int32.TryParse(value, out var parsed) ? parsed : fallback;
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
}
