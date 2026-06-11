using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

public sealed class PaperModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly PaperEnvironmentService paperEnvironment;
    private readonly RunConfigWriter configWriter;
    private readonly PaperJobService paperJobs;
    private readonly IArtifactWriter artifactWriter;
    private readonly ILogger<PaperModel> logger;

    public PaperModel(
        ConfigCatalogService catalog,
        PaperEnvironmentService paperEnvironment,
        RunConfigWriter configWriter,
        PaperJobService paperJobs,
        IArtifactWriter artifactWriter,
        ILogger<PaperModel> logger)
    {
        this.catalog = catalog;
        this.paperEnvironment = paperEnvironment;
        this.configWriter = configWriter;
        this.paperJobs = paperJobs;
        this.artifactWriter = artifactWriter;
        this.logger = logger;
    }

    [BindProperty] public string ConfigPath { get; set; } = String.Empty;
    [BindProperty] public string SelectedStrategyPath { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public string? TickersCsv { get; set; }
    [BindProperty(SupportsGet = true)] public string OrderExpiration { get; set; } = "gtc";
    [BindProperty(SupportsGet = true)] public string EntryOrderType { get; set; } = "limit";
    [BindProperty(SupportsGet = true)] public bool ExtendedHours { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ScreenerFilter { get; set; }
    [BindProperty] public string? StrategyYaml { get; set; }
    
    [BindProperty] public string? QuickEditTimeframe { get; set; }
    [BindProperty] public string? QuickEditSetupType { get; set; }
    [BindProperty] public string? QuickEditStopAtr { get; set; }
    [BindProperty] public string? QuickEditTargetR { get; set; }

    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<BacktestJobSnapshot> LiveJobs { get; private set; } = [];
    public RunConfigSummary Selected { get; private set; } = null!;
    public PaperEnvironmentSnapshot? PaperSnapshot { get; private set; }
    public string AlpacaCheckJson { get; private set; } = String.Empty;
    public string SuggestedRunName { get; private set; } = String.Empty;

    public void OnGet(string? configPath, string? strategyPath)
    {
        SuggestedRunName = $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
        if (!string.IsNullOrEmpty(configPath)) ConfigPath = configPath;
        if (!string.IsNullOrEmpty(strategyPath)) SelectedStrategyPath = strategyPath;

        Load(ConfigPath, SelectedStrategyPath);
        
        if (string.IsNullOrEmpty(TickersCsv))
        {
            var selectedConfig = Selected;
            TickersCsv = selectedConfig != null ? String.Join(", ", selectedConfig.Config.Tickers) : "";
        }

        if (string.IsNullOrEmpty(OrderExpiration))
        {
            var selectedConfig = Configs.FirstOrDefault(c => c.Path == ConfigPath);
            OrderExpiration = selectedConfig?.Config.Execution.OrderExpiration ?? "gtc";
        }

        var selectedExecutionConfig = Configs.FirstOrDefault(c => c.Path == ConfigPath);
        if (string.IsNullOrEmpty(EntryOrderType))
        {
            EntryOrderType = selectedExecutionConfig?.Config.Execution.EntryOrderType ?? "limit";
        }

        ExtendedHours = selectedExecutionConfig?.Config.Execution.ExtendedHours ?? true;
        ScreenerFilter = selectedExecutionConfig?.Config.Screener?.Filters?.FirstOrDefault() ?? "";
        
        var selectedStrategy = Strategies.FirstOrDefault(s => s.Path == SelectedStrategyPath);
        if (selectedStrategy != null && string.IsNullOrEmpty(StrategyYaml))
        {
            try
            {
                StrategyYaml = System.IO.File.ReadAllText(selectedStrategy.Path);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to read selected strategy YAML from {StrategyPath}.",
                    selectedStrategy.Path);
            }
        }
        
        if (!string.IsNullOrEmpty(StrategyYaml))
        {
            var tfMatch = System.Text.RegularExpressions.Regex.Match(StrategyYaml, @"timeframe:\s*(\w+)");
            if (tfMatch.Success) QuickEditTimeframe = tfMatch.Groups[1].Value;

            var setupMatch = System.Text.RegularExpressions.Regex.Match(StrategyYaml, @"setup_type:\s*(\w+)");
            if (setupMatch.Success) QuickEditSetupType = setupMatch.Groups[1].Value;

            var stopMatch = System.Text.RegularExpressions.Regex.Match(StrategyYaml, @"stop_atr_multiple:\s*([\d\.]+)");
            if (stopMatch.Success) QuickEditStopAtr = stopMatch.Groups[1].Value;

            var targetMatch = System.Text.RegularExpressions.Regex.Match(StrategyYaml, @"target_r_multiple:\s*([\d\.]+)");
            if (targetMatch.Success) QuickEditTargetR = targetMatch.Groups[1].Value;
        }
    }

    public string FormatLocalTime(DateTimeOffset timestamp)
    {
        return UiDisplayFormatter.FormatLocalTime(timestamp);
    }

    public async Task OnPostAsync(CancellationToken cancellationToken)
    {
        var formConfigPath = Request.Form["BaseConfigPath"].ToString();
        if (!string.IsNullOrEmpty(formConfigPath)) ConfigPath = formConfigPath;

        Load(ConfigPath, SelectedStrategyPath);

        PaperSnapshot = await paperEnvironment.InspectAsync(ConfigPath, cancellationToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        AlpacaCheckJson = JsonSerializer.Serialize(PaperSnapshot.AlpacaReadOnlyCheck, jsonOptions);
    }

    public IActionResult OnPostSaveConfig()
    {
        var form = Request.Form;
        var baseConfigPath = form["BaseConfigPath"].ToString();
        var tickersCsv = form["TickersCsv"].ToString();
        var tickers = tickersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var strategyPath = form["SelectedStrategyPath"].ToString();
        var orderExpiration = form["OrderExpiration"].ToString();
        var entryOrderType = form["EntryOrderType"].ToString();
        var extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase);
        var screenerFilter = form["ScreenerFilter"].ToString();

        if (tickers.Length > 0 && !String.IsNullOrWhiteSpace(baseConfigPath))
        {
            var newPath = configWriter.SaveTempConfig(baseConfigPath, tickers, strategyPath, orderExpiration, entryOrderType, extendedHours, screenerFilter);
            return RedirectToPage(new { configPath = newPath, strategyPath, orderExpiration, entryOrderType, extendedHours, screenerFilter });
        }

        return RedirectToPage();
    }

    public IActionResult OnPostSaveStrategy()
    {
        var form = Request.Form;
        var strategyPath = form["SelectedStrategyPath"].ToString();
        var strategyYaml = form["StrategyYaml"].ToString();

        if (!string.IsNullOrWhiteSpace(form["QuickEditTimeframe"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"timeframe:\s*\w+", $"timeframe: {form["QuickEditTimeframe"]}");
            
        if (!string.IsNullOrWhiteSpace(form["QuickEditSetupType"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"setup_type:\s*\w+", $"setup_type: {form["QuickEditSetupType"]}");
            
        if (!string.IsNullOrWhiteSpace(form["QuickEditStopAtr"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"stop_atr_multiple:\s*[\d\.]+", $"stop_atr_multiple: {form["QuickEditStopAtr"]}");
            
        if (!string.IsNullOrWhiteSpace(form["QuickEditTargetR"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"target_r_multiple:\s*[\d\.]+", $"target_r_multiple: {form["QuickEditTargetR"]}");

        if (!string.IsNullOrWhiteSpace(strategyPath) && !string.IsNullOrWhiteSpace(strategyYaml))
        {
            try
            {
                artifactWriter.WriteText(strategyPath, strategyYaml);
            }
            catch (Exception exception)
            {
                ModelState.AddModelError(String.Empty, $"Could not save strategy: {exception.Message}");
            }
        }
        return RedirectToPage(new { 
            configPath = form["BaseConfigPath"].ToString(), 
            strategyPath, 
            tickersCsv = form["TickersCsv"].ToString(),
            orderExpiration = form["OrderExpiration"].ToString(), 
            entryOrderType = form["EntryOrderType"].ToString(),
            extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase),
            screenerFilter = form["ScreenerFilter"].ToString()
        });
    }

    public IActionResult OnPostDeleteConfig()
    {
        var form = Request.Form;
        var configPath = form["DeleteConfigPath"].ToString();
        if (!String.IsNullOrWhiteSpace(configPath))
        {
            configWriter.DeleteTempConfig(configPath);
        }
        return RedirectToPage(new { 
            strategyPath = form["SelectedStrategyPath"].ToString(),
            orderExpiration = form["OrderExpiration"].ToString(), 
            entryOrderType = form["EntryOrderType"].ToString(),
            extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase),
            screenerFilter = form["ScreenerFilter"].ToString()
        });
    }

    public IActionResult OnPostRunLive()
    {
        var form = Request.Form;
        var runName = form["RunName"].ToString();
        var baseConfigPath = form["BaseConfigPath"].ToString();
        var strategyPath = form["SelectedStrategyPath"].ToString();
        var orderExpiration = form["OrderExpiration"].ToString();
        var entryOrderType = form["EntryOrderType"].ToString();
        var extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase);
        var screenerFilter = form["ScreenerFilter"].ToString();
        var tickersCsv = form["TickersCsv"].ToString();
        
        if (String.IsNullOrWhiteSpace(runName))
            runName = "paper_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");

        var existingConfig = catalog.GetConfig(baseConfigPath);
        var tickers = string.IsNullOrWhiteSpace(tickersCsv) 
            ? existingConfig.Config.Tickers 
            : tickersCsv.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        var tempConfigPath = configWriter.SaveTempConfig(baseConfigPath, tickers, strategyPath, orderExpiration, entryOrderType, extendedHours, screenerFilter, runName);
        var job = paperJobs.Start(runName, tempConfigPath);
        return RedirectToPage("/PaperJob", new { id = job.JobId });
    }

    private void Load(string? configPath, string? strategyPath)
    {
        Configs = catalog.GetPaperConfigs();
        Strategies = catalog.GetStrategies();
        LiveJobs = paperJobs.List().Take(10).ToArray();
        
        if (Configs.Count == 0)
        {
            throw new InvalidOperationException("No paper configs found.");
        }

        Selected = String.IsNullOrWhiteSpace(configPath)
            ? Configs.First()
            : catalog.GetConfig(configPath);
        ConfigPath = Selected.Path;
        SelectedStrategyPath = String.IsNullOrWhiteSpace(strategyPath)
            ? Strategies.FirstOrDefault()?.Path ?? String.Empty
            : strategyPath;
    }

    public IActionResult OnGetStrategyDetails(string strategyPath)
    {
        if (string.IsNullOrEmpty(strategyPath) || !System.IO.File.Exists(strategyPath))
            return new JsonResult(new { success = false });

        var yaml = System.IO.File.ReadAllText(strategyPath);
        var tfMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"timeframe:\s*(\w+)");
        var setupMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"setup_type:\s*(\w+)");
        var stopMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"stop_atr_multiple:\s*([\d\.]+)");
        var targetMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"target_r_multiple:\s*([\d\.]+)");

        return new JsonResult(new {
            success = true,
            yaml = yaml,
            timeframe = tfMatch.Success ? tfMatch.Groups[1].Value : "",
            setupType = setupMatch.Success ? setupMatch.Groups[1].Value : "",
            stopAtr = stopMatch.Success ? stopMatch.Groups[1].Value : "",
            targetR = targetMatch.Success ? targetMatch.Groups[1].Value : ""
        });
    }

    public IActionResult OnGetConfigDetails(string configPath)
    {
        if (string.IsNullOrEmpty(configPath))
            return new JsonResult(new { success = false });

        try
        {
            var config = catalog.GetConfig(configPath);
            return new JsonResult(new {
                success = true,
                tickers = String.Join(", ", config.Config.Tickers),
                orderExpiration = config.Config.Execution.OrderExpiration,
                entryOrderType = config.Config.Execution.EntryOrderType,
                extendedHours = config.Config.Execution.ExtendedHours,
                screenerFilter = config.Config.Screener?.Filters?.FirstOrDefault() ?? "",
                broker = config.Config.Execution.Broker
            });
        }
        catch
        {
            return new JsonResult(new { success = false });
        }
    }
}
