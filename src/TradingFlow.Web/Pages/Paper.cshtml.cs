using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
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
    private readonly IWishlistRepository wishlistRepository;
    private readonly ILogger<PaperModel> logger;

    public PaperModel(
        ConfigCatalogService catalog,
        PaperEnvironmentService paperEnvironment,
        RunConfigWriter configWriter,
        PaperJobService paperJobs,
        IArtifactWriter artifactWriter,
        IWishlistRepository wishlistRepository,
        ILogger<PaperModel> logger)
    {
        this.catalog = catalog;
        this.paperEnvironment = paperEnvironment;
        this.configWriter = configWriter;
        this.paperJobs = paperJobs;
        this.artifactWriter = artifactWriter;
        this.wishlistRepository = wishlistRepository;
        this.logger = logger;
    }

    [BindProperty] public string ConfigPath { get; set; } = String.Empty;
    [BindProperty] public string SelectedStrategyPath { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public Guid? WishlistId { get; set; }
    [BindProperty(SupportsGet = true)] public string OrderExpiration { get; set; } = "gtc";
    [BindProperty(SupportsGet = true)] public string EntryOrderType { get; set; } = "limit";
    [BindProperty(SupportsGet = true)] public bool ExtendedHours { get; set; } = true;
    [BindProperty(SupportsGet = true)] public bool NewsEnabled { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ScreenerFilter { get; set; }
    [BindProperty] public string? StrategyYaml { get; set; }

    [BindProperty] public string? QuickEditTimeframe { get; set; }
    [BindProperty] public string? QuickEditSetupType { get; set; }
    [BindProperty] public string? QuickEditMinVolumeSpike { get; set; }
    [BindProperty] public string? QuickEditStopAtr { get; set; }
    [BindProperty] public string? QuickEditTargetR { get; set; }

    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public IReadOnlyList<BacktestJobSnapshot> LiveJobs { get; private set; } = [];
    public RunConfigSummary Selected { get; private set; } = null!;
    public Wishlist? SelectedWishlist { get; private set; }
    public PaperEnvironmentSnapshot? PaperSnapshot { get; private set; }
    public string AlpacaCheckJson { get; private set; } = String.Empty;
    public string SuggestedRunName { get; private set; } = String.Empty;
    public bool SelectedStrategyUsesNews { get; private set; }
    public string WishlistTickersCsv => SelectedWishlist is null
        ? String.Empty
        : String.Join(", ", SelectedWishlist.Items.Where(item => item.Active).Select(item => item.Ticker).OrderBy(ticker => ticker));

    public async Task OnGetAsync(string? configPath, string? strategyPath, Guid? wishlistId, CancellationToken cancellationToken)
    {
        SuggestedRunName = $"paper_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
        if (!string.IsNullOrEmpty(configPath)) ConfigPath = configPath;
        if (!string.IsNullOrEmpty(strategyPath)) SelectedStrategyPath = strategyPath;
        WishlistId = wishlistId ?? WishlistId;

        Load(ConfigPath, SelectedStrategyPath);
        await LoadWishlistsAsync(WishlistId, cancellationToken);

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
        ScreenerFilter ??= String.Empty;

        var selectedStrategy = Strategies.FirstOrDefault(s => s.Path == SelectedStrategyPath);
        SelectedStrategyUsesNews = selectedStrategy is not null && StrategyUsesNews(selectedStrategy.Definition);
        NewsEnabled = (selectedExecutionConfig?.Config.News.Enabled ?? true) && SelectedStrategyUsesNews;
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

            var volumeMatch = System.Text.RegularExpressions.Regex.Match(StrategyYaml, @"min_volume_spike:\s*([\d\.]+)");
            if (volumeMatch.Success) QuickEditMinVolumeSpike = volumeMatch.Groups[1].Value;

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
        if (Guid.TryParse(Request.Form["WishlistId"].ToString(), out var wishlistId)) WishlistId = wishlistId;

        Load(ConfigPath, SelectedStrategyPath);
        await LoadWishlistsAsync(WishlistId, cancellationToken);

        PaperSnapshot = await paperEnvironment.InspectAsync(ConfigPath, cancellationToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        AlpacaCheckJson = JsonSerializer.Serialize(PaperSnapshot.AlpacaReadOnlyCheck, jsonOptions);
    }

    public IActionResult OnPostSaveStrategy()
    {
        var form = Request.Form;
        var strategyPath = form["SelectedStrategyPath"].ToString();
        var strategyYaml = form["StrategyYaml"].ToString();

        if (!string.IsNullOrWhiteSpace(form["QuickEditTimeframe"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(
                strategyYaml,
                @"(?m)^timeframe:\s*\w+",
                $"timeframe: {form["QuickEditTimeframe"]}",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(1));

        if (!string.IsNullOrWhiteSpace(form["QuickEditSetupType"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"setup_type:\s*\w+", $"setup_type: {form["QuickEditSetupType"]}");

        if (!string.IsNullOrWhiteSpace(form["QuickEditMinVolumeSpike"]))
            strategyYaml = System.Text.RegularExpressions.Regex.Replace(strategyYaml, @"min_volume_spike:\s*[\d\.]+", $"min_volume_spike: {form["QuickEditMinVolumeSpike"]}");

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
            wishlistId = form["WishlistId"].ToString(),
            orderExpiration = form["OrderExpiration"].ToString(),
            entryOrderType = form["EntryOrderType"].ToString(),
            extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase),
            newsEnabled = form.TryGetValue("NewsEnabled", out var ne) && ne.ToString().Contains("true", StringComparison.OrdinalIgnoreCase),
            screenerFilter = form["ScreenerFilter"].ToString()
        });
    }

    public async Task<IActionResult> OnPostRunLive(CancellationToken cancellationToken)
    {
        var form = Request.Form;
        var runName = form["RunName"].ToString();
        var baseConfigPath = form["BaseConfigPath"].ToString();
        var strategyPath = form["SelectedStrategyPath"].ToString();
        var wishlistIdText = form["WishlistId"].ToString();
        var orderExpiration = form["OrderExpiration"].ToString();
        var entryOrderType = form["EntryOrderType"].ToString();
        var extendedHours = form.TryGetValue("ExtendedHours", out var eh) && eh.ToString().Contains("true", StringComparison.OrdinalIgnoreCase);
        var newsEnabled = EffectiveNewsEnabled(strategyPath, form.TryGetValue("NewsEnabled", out var ne) && ne.ToString().Contains("true", StringComparison.OrdinalIgnoreCase));
        var screenerFilter = form["ScreenerFilter"].ToString();

        if (String.IsNullOrWhiteSpace(runName))
            runName = "paper_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");

        Load(baseConfigPath, strategyPath);
        await LoadWishlistsAsync(Guid.TryParse(wishlistIdText, out var parsedWishlistId) ? parsedWishlistId : null, cancellationToken);
        var wishlistTickers = SelectedWishlist?.Items
            .Where(item => item.Active)
            .Select(item => item.Ticker)
            .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
            .ToArray() ?? [];
        if (wishlistTickers.Length == 0 && String.IsNullOrWhiteSpace(screenerFilter))
        {
            ModelState.AddModelError(String.Empty, "Select a wishlist with active tickers or provide a Finviz screener.");
            return Page();
        }

        var tempConfigPath = configWriter.SaveTempConfig(
            baseConfigPath,
            wishlistTickers,
            strategyPath,
            orderExpiration,
            entryOrderType,
            extendedHours,
            screenerFilter,
            runName,
            newsEnabled,
            SelectedWishlist?.Id,
            SelectedWishlist?.Name,
            SelectedWishlist is null ? "finviz" : "wishlist");
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
            ? Configs.FirstOrDefault(config => config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase)) ?? Configs.First()
            : catalog.GetConfig(configPath);
        ConfigPath = Selected.Path;
        SelectedStrategyPath = String.IsNullOrWhiteSpace(strategyPath)
            ? Strategies.FirstOrDefault()?.Path ?? Selected.Strategies.FirstOrDefault()?.Path ?? String.Empty
            : strategyPath;
    }

    private async Task LoadWishlistsAsync(Guid? wishlistId, CancellationToken cancellationToken)
    {
        Wishlists = await wishlistRepository.ListAsync(cancellationToken);
        SelectedWishlist = wishlistId.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == wishlistId.Value)
            : Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault) ?? Wishlists.FirstOrDefault();
        WishlistId = SelectedWishlist?.Id;
    }

    public IActionResult OnGetStrategyDetails(string strategyPath)
    {
        if (string.IsNullOrEmpty(strategyPath) || !System.IO.File.Exists(strategyPath))
            return new JsonResult(new { success = false });

        var yaml = System.IO.File.ReadAllText(strategyPath);
        var tfMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"timeframe:\s*(\w+)");
        var setupMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"setup_type:\s*(\w+)");
        var volumeMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"min_volume_spike:\s*([\d\.]+)");
        var stopMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"stop_atr_multiple:\s*([\d\.]+)");
        var targetMatch = System.Text.RegularExpressions.Regex.Match(yaml, @"target_r_multiple:\s*([\d\.]+)");
        var strategy = catalog.GetStrategies().FirstOrDefault(x => Path.GetFullPath(x.Path).Equals(Path.GetFullPath(strategyPath), StringComparison.OrdinalIgnoreCase));
        var usesNews = strategy is not null && StrategyUsesNews(strategy.Definition);

        return new JsonResult(new {
            success = true,
            yaml = yaml,
            timeframe = tfMatch.Success ? tfMatch.Groups[1].Value : "",
            setupType = setupMatch.Success ? setupMatch.Groups[1].Value : "",
            minVolumeSpike = volumeMatch.Success ? volumeMatch.Groups[1].Value : "",
            stopAtr = stopMatch.Success ? stopMatch.Groups[1].Value : "",
            targetR = targetMatch.Success ? targetMatch.Groups[1].Value : "",
            usesNews
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
                newsEnabled = config.Config.News.Enabled,
                screenerFilter = config.Config.Screener?.Filters?.FirstOrDefault() ?? "",
                broker = config.Config.Execution.Broker
            });
        }
        catch
        {
            return new JsonResult(new { success = false });
        }
    }

    private bool EffectiveNewsEnabled(string strategyPath, bool requestedNewsEnabled)
    {
        if (!requestedNewsEnabled)
        {
            return false;
        }

        var strategy = Strategies.FirstOrDefault(x => Path.GetFullPath(x.Path).Equals(Path.GetFullPath(strategyPath), StringComparison.OrdinalIgnoreCase))
            ?? catalog.GetStrategies().FirstOrDefault(x => Path.GetFullPath(x.Path).Equals(Path.GetFullPath(strategyPath), StringComparison.OrdinalIgnoreCase));
        return strategy is not null && StrategyUsesNews(strategy.Definition);
    }

    private static bool StrategyUsesNews(StrategyDefinition strategy)
    {
        return strategy.EntryRules.RequirePositiveNews ||
            strategy.EntryRules.MaxShortNewsSentiment is not null ||
            strategy.EntryRules.MinShortCatalystDropPct is not null ||
            strategy.EntryRules.MinCatalystPriceMovePct is not null ||
            strategy.EntryRules.MaxCatalystPriceMovePct is not null ||
            strategy.EntryRules.SetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase) ||
            strategy.EntryRules.ShortSetupType.Contains("catalyst", StringComparison.OrdinalIgnoreCase);
    }
}
