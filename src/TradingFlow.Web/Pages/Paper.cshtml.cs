using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

public sealed class PaperModel : PageModel
{
    private readonly ConfigCatalogService catalog;
    private readonly PaperEnvironmentService paperEnvironment;
    private readonly RunConfigWriter configWriter;
    private readonly PaperJobService paperJobs;
    private readonly IWishlistRepository wishlistRepository;
    private readonly ScreenerPresetService screenerPresets;
    private readonly IPaperRunUniverseSnapshotResolver universeSnapshots;
    private readonly ILogger<PaperModel> logger;

    public PaperModel(
        ConfigCatalogService catalog,
        PaperEnvironmentService paperEnvironment,
        RunConfigWriter configWriter,
        PaperJobService paperJobs,
        IWishlistRepository wishlistRepository,
        ScreenerPresetService screenerPresets,
        IPaperRunUniverseSnapshotResolver universeSnapshots,
        ILogger<PaperModel> logger)
    {
        this.catalog = catalog;
        this.paperEnvironment = paperEnvironment;
        this.configWriter = configWriter;
        this.paperJobs = paperJobs;
        this.wishlistRepository = wishlistRepository;
        this.screenerPresets = screenerPresets;
        this.universeSnapshots = universeSnapshots;
        this.logger = logger;
    }

    [BindProperty] public string ConfigPath { get; set; } = String.Empty;
    [BindProperty] public string SelectedStrategyPath { get; set; } = String.Empty;
    [BindProperty(SupportsGet = true)] public string? PaperExecutionMode { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? WishlistId { get; set; }
    [BindProperty(SupportsGet = true)] public string? OrderExpiration { get; set; } = "gtc";
    [BindProperty(SupportsGet = true)] public string? EntryOrderType { get; set; } = "limit";
    [BindProperty(SupportsGet = true)] public bool AllowExtendedHoursTrading { get; set; }
    [BindProperty(SupportsGet = true)] public bool NewsEnabled { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ScreenerFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? ExperimentSourceStrategyPath { get; set; }
    [BindProperty] public string ExperimentSemanticVersion { get; set; } = String.Empty;
    [BindProperty] public Guid ExperimentRegistrationOperationId { get; set; }
    [BindProperty] public DateTimeOffset ExperimentRegistrationRequestedAtUtc { get; set; }
    [BindProperty] public PaperExperimentParameterInput ExperimentParameters { get; set; } = new();

    /// <summary>
    /// Where the candidate universe comes from: <c>wishlist</c>, <c>screener</c>
    /// or <c>both</c>.
    /// </summary>
    /// <remarks>
    /// A screener is a universe in its own right, not an addition to a wishlist.
    /// Selecting <c>screener</c> runs against exactly what the screen returns and
    /// ignores the wishlist entirely, so an operator can trade a screen without
    /// first promoting its symbols into a curated list.
    /// </remarks>
    [BindProperty(SupportsGet = true)] public string? UniverseSource { get; set; } = "wishlist";

    /// <summary>
    /// Saved screens, offered by name. Finviz exposes no endpoint that lists the
    /// screens saved in its own UI, so the catalogue is local.
    /// </summary>
    public IReadOnlyList<TradingFlow.Domain.Wishlists.ScreenerPreset> ScreenerPresets { get; private set; } = [];
    public IReadOnlyList<RunConfigSummary> Configs { get; private set; } = [];
    public IReadOnlyList<StrategyOption> Strategies { get; private set; } = [];
    public IReadOnlyList<StrategyOption> ResearchStrategies { get; private set; } = [];
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
        ExperimentRegistrationOperationId = Guid.NewGuid();
        ExperimentRegistrationRequestedAtUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(configPath)) ConfigPath = configPath;
        if (!string.IsNullOrEmpty(strategyPath)) SelectedStrategyPath = strategyPath;
        WishlistId = wishlistId ?? WishlistId;
        PaperExecutionMode = NormalizePaperExecutionModeForDisplay(PaperExecutionMode);

        await LoadAsync(ConfigPath, SelectedStrategyPath, cancellationToken);
        await LoadWishlistsAsync(WishlistId, cancellationToken);

        PopulateExperimentParameterDefaults();
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

        AllowExtendedHoursTrading = selectedExecutionConfig?.Config.Execution.AllowExtendedHoursTrading ?? false;
        ScreenerFilter ??= String.Empty;
        UniverseSource = NormalizeUniverseSource(UniverseSource);

        var selectedStrategy = Strategies.FirstOrDefault(s => s.Path == SelectedStrategyPath);
        SelectedStrategyUsesNews = selectedStrategy is not null && StrategyCapabilityInspector.UsesNews(selectedStrategy.Definition);
        NewsEnabled = (selectedExecutionConfig?.Config.News.Enabled ?? true) && SelectedStrategyUsesNews;
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

        await LoadAsync(ConfigPath, SelectedStrategyPath, cancellationToken);
        await LoadWishlistsAsync(WishlistId, cancellationToken);

        PaperSnapshot = await paperEnvironment.InspectAsync(ConfigPath, cancellationToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        AlpacaCheckJson = JsonSerializer.Serialize(PaperSnapshot.AlpacaReadOnlyCheck, jsonOptions);
    }

    public async Task<IActionResult> OnPostRunLive(CancellationToken cancellationToken)
    {
        var form = Request.Form;
        var runName = form["RunName"].ToString();
        var baseConfigPath = form["BaseConfigPath"].ToString();
        var strategyPath = form["SelectedStrategyPath"].ToString();
        if (!TryNormalizePaperExecutionMode(
                form["PaperExecutionMode"].ToString(),
                out var normalizedPaperMode))
        {
            ModelState.AddModelError(String.Empty, "paper_execution_mode_invalid");
            PaperExecutionMode = "shadow";
            await LoadAsync(baseConfigPath, strategyPath, cancellationToken);
            await LoadWishlistsAsync(null, cancellationToken);
            return Page();
        }

        PaperExecutionMode = normalizedPaperMode;
        var wishlistIdText = form["WishlistId"].ToString();
        var orderExpiration = form["OrderExpiration"].ToString();
        var entryOrderType = form["EntryOrderType"].ToString();
        var allowExtendedHoursTrading = form.TryGetValue("AllowExtendedHoursTrading", out var extendedHoursValue) &&
            extendedHoursValue.ToString().Contains("true", StringComparison.OrdinalIgnoreCase);
        var newsEnabled = EffectiveNewsEnabled(strategyPath, form.TryGetValue("NewsEnabled", out var ne) && ne.ToString().Contains("true", StringComparison.OrdinalIgnoreCase));
        var screenerFilter = form["ScreenerFilter"].ToString();

        if (String.IsNullOrWhiteSpace(runName))
            runName = "paper_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");

        OrderExpiration = orderExpiration;
        EntryOrderType = entryOrderType;
        AllowExtendedHoursTrading = allowExtendedHoursTrading;
        NewsEnabled = newsEnabled;
        ScreenerFilter = screenerFilter;
        SuggestedRunName = runName;

        await LoadAsync(baseConfigPath, strategyPath, cancellationToken);
        await LoadWishlistsAsync(Guid.TryParse(wishlistIdText, out var parsedWishlistId) ? parsedWishlistId : null, cancellationToken);
        PopulateSelectedStrategyState();
        var selectedPaperStrategy = Strategies.SingleOrDefault(strategy =>
                Path.GetFullPath(strategy.Path).Equals(
                    Path.GetFullPath(strategyPath),
                    StringComparison.OrdinalIgnoreCase));
        if (selectedPaperStrategy is null)
        {
            var failureCode = PaperExecutionMode == "experiment"
                ? "no_paper_experiment_strategy"
                : "no_paper_shadow_strategy";
            ModelState.AddModelError(
                String.Empty,
                $"{failureCode}: no exact strategy artifact is authorized for the selected paper mode.");
            return Page();
        }

        var source = NormalizeUniverseSource(form["UniverseSource"].ToString());
        UniverseSource = source;

        // A screener-only run ignores the wishlist outright rather than merging
        // with it: the operator asked to trade the screen, and silently adding
        // curated names would make the universe something neither source states.
        var usesWishlist = source is "wishlist" or "both";
        var usesScreener = source is "screener" or "both";
        var wishlistTickers = usesWishlist
            ? SelectedWishlist?.Items
                .Where(item => item.Active)
                .Select(item => item.Ticker)
                .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
                .ToArray() ?? []
            : [];
        string tempConfigPath;
        try
        {
            var universe = await universeSnapshots.ResolveAsync(
                wishlistTickers,
                usesWishlist,
                "wishlist",
                screenerFilter,
                usesScreener,
                usesWishlist ? SelectedWishlist?.Id : null,
                strategyPath,
                cancellationToken);
            tempConfigPath = configWriter.SaveTempConfig(
                baseConfigPath,
                universe.Tickers,
                selectedPaperStrategy.Artifact,
                ResolvePaperSelectionMode(),
                orderExpiration,
                entryOrderType,
                allowExtendedHoursTrading,
                universe.ScreenerQuery,
                runName,
                newsEnabled,
                usesWishlist ? SelectedWishlist?.Id : null,
                usesWishlist ? SelectedWishlist?.Name : null,
                universe.Source,
                universe.ResolvedAtUtc,
                universe.SelectedSourceTickers,
                universe.ScreenerTickers);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogInformation(
                "Paper run {RunName} rejected before job creation: {Reason}",
                runName,
                exception.Message);
            ModelState.AddModelError(String.Empty, exception.Message);
            return Page();
        }

        var job = await paperJobs.StartAsync(runName, tempConfigPath, cancellationToken);
        return RedirectToPage("/PaperJob", new { id = job.JobId });
    }

    public async Task<IActionResult> OnPostCreateExperimentAsync(CancellationToken cancellationToken)
    {
        PaperExecutionMode = "experiment";
        await LoadAsync(ConfigPath, SelectedStrategyPath, cancellationToken);
        await LoadWishlistsAsync(WishlistId, cancellationToken);
        if (!ModelState.IsValid)
        {
            ResetExperimentRegistrationCommandIfMissing();
            return Page();
        }

        var source = ResearchStrategies.SingleOrDefault(strategy =>
            !String.IsNullOrWhiteSpace(ExperimentSourceStrategyPath) &&
            Path.GetFullPath(strategy.Path).Equals(
                Path.GetFullPath(ExperimentSourceStrategyPath),
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            ModelState.AddModelError(String.Empty, "paper_experiment_source_invalid");
            ResetExperimentRegistrationCommandIfMissing();
            return Page();
        }

        if (ExperimentRegistrationOperationId == Guid.Empty ||
            ExperimentRegistrationRequestedAtUtc == default ||
            ExperimentRegistrationRequestedAtUtc.Offset != TimeSpan.Zero)
        {
            ModelState.AddModelError(String.Empty, "paper_experiment_operation_invalid");
            ResetExperimentRegistrationCommandIfMissing();
            return Page();
        }

        var actor = User.Identity?.Name ?? "local-operator";
        var semanticVersion = String.IsNullOrWhiteSpace(ExperimentSemanticVersion)
            ? source.Identity.SemanticVersion
            : ExperimentSemanticVersion.Trim();
        StrategyParameterOverride? parameterOverride = null;
        if (ExperimentParameters.ApplyOverrides)
        {
            if (String.IsNullOrWhiteSpace(ExperimentSemanticVersion) ||
                semanticVersion.Equals(source.Identity.SemanticVersion, StringComparison.Ordinal))
            {
                ModelState.AddModelError(
                    nameof(ExperimentSemanticVersion),
                    "Changed experiment parameters require a new semantic version.");
                return Page();
            }

            parameterOverride = ExperimentParameters.ToOverride(source.Path, semanticVersion, actor);
        }

        try
        {
            var artifact = configWriter.RegisterPaperExperiment(
                source.Path,
                semanticVersion,
                actor,
                ExperimentRegistrationOperationId,
                ExperimentRegistrationRequestedAtUtc,
                parameterOverride);
            return RedirectToPage("/Paper", new
            {
                paperExecutionMode = "experiment",
                strategyPath = artifact.SourcePath,
                wishlistId = WishlistId
            });
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException)
        {
            logger.LogInformation(
                "Paper experiment registration {OperationId} rejected: {Reason}",
                ExperimentRegistrationOperationId,
                exception.Message);
            ModelState.AddModelError(String.Empty, exception.Message);
            return Page();
        }
    }

    private void PopulateSelectedStrategyState()
    {
        var selectedStrategy = Strategies.FirstOrDefault(strategy => strategy.Path == SelectedStrategyPath);
        SelectedStrategyUsesNews = selectedStrategy is not null && StrategyCapabilityInspector.UsesNews(selectedStrategy.Definition);
    }

    private async Task LoadAsync(
        string? configPath,
        string? strategyPath,
        CancellationToken cancellationToken)
    {
        Configs = catalog.GetPaperConfigs();
        ResearchStrategies = await catalog.GetStrategiesAsync(
            StrategySelectionMode.CreatePaperExperiment,
            cancellationToken);
        Strategies = await catalog.GetStrategiesAsync(
            ResolvePaperSelectionMode(),
            cancellationToken);
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
            ? String.Empty
            : Strategies.Any(strategy => Path.GetFullPath(strategy.Path).Equals(
                Path.GetFullPath(strategyPath),
                StringComparison.OrdinalIgnoreCase))
                ? strategyPath
                : String.Empty;
    }

    private async Task LoadWishlistsAsync(Guid? wishlistId, CancellationToken cancellationToken)
    {
        ScreenerPresets = await screenerPresets.ListAsync(null, cancellationToken);
        Wishlists = await wishlistRepository.ListAsync(cancellationToken);
        SelectedWishlist = wishlistId.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == wishlistId.Value)
            : Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault) ?? Wishlists.FirstOrDefault();
        WishlistId = SelectedWishlist?.Id;
    }

    public async Task<IActionResult> OnGetStrategyDetailsAsync(
        string strategyPath,
        string? paperExecutionMode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(strategyPath) || !System.IO.File.Exists(strategyPath))
            return new JsonResult(new { success = false });

        if (!TryNormalizePaperExecutionMode(paperExecutionMode, out var normalizedMode))
        {
            return BadRequest(new { success = false, error = "paper_execution_mode_invalid" });
        }

        var strategies = await catalog.GetStrategiesAsync(
            normalizedMode == "experiment"
                ? StrategySelectionMode.RunPaperExperiment
                : StrategySelectionMode.RunPaperShadow,
            cancellationToken);
        var strategy = strategies.FirstOrDefault(x =>
            Path.GetFullPath(x.Path).Equals(Path.GetFullPath(strategyPath), StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
        {
            return new JsonResult(new { success = false });
        }

        var usesNews = StrategyCapabilityInspector.UsesNews(strategy.Definition);

        return new JsonResult(new {
            success = true,
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
                allowExtendedHoursTrading = config.Config.Execution.AllowExtendedHoursTrading,
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

        var strategy = Strategies.FirstOrDefault(x => Path.GetFullPath(x.Path).Equals(Path.GetFullPath(strategyPath), StringComparison.OrdinalIgnoreCase));
        return strategy is not null && StrategyCapabilityInspector.UsesNews(strategy.Definition);
    }

    private static string NormalizeUniverseSource(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "screener" => "screener",
            "both" => "both",
            _ => "wishlist"
        };

    private StrategySelectionMode ResolvePaperSelectionMode() =>
        PaperExecutionMode == "experiment"
            ? StrategySelectionMode.RunPaperExperiment
            : StrategySelectionMode.RunPaperShadow;

    private static string NormalizePaperExecutionModeForDisplay(string? value) =>
        TryNormalizePaperExecutionMode(value, out var normalized) ? normalized : "shadow";

    private static bool TryNormalizePaperExecutionMode(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? String.Empty;
        return normalized is "experiment" or "shadow";
    }

    private void PopulateExperimentParameterDefaults()
    {
        var source = ResearchStrategies.FirstOrDefault(strategy =>
                !String.IsNullOrWhiteSpace(ExperimentSourceStrategyPath) &&
                Path.GetFullPath(strategy.Path).Equals(
                    Path.GetFullPath(ExperimentSourceStrategyPath),
                    StringComparison.OrdinalIgnoreCase))
            ?? ResearchStrategies.FirstOrDefault();
        if (source is null)
        {
            return;
        }

        ExperimentSourceStrategyPath = source.Path;
        ExperimentParameters = PaperExperimentParameterInput.From(source.Definition);
    }

    private void ResetExperimentRegistrationCommandIfMissing()
    {
        if (ExperimentRegistrationOperationId == Guid.Empty)
        {
            ExperimentRegistrationOperationId = Guid.NewGuid();
        }

        if (ExperimentRegistrationRequestedAtUtc == default ||
            ExperimentRegistrationRequestedAtUtc.Offset != TimeSpan.Zero)
        {
            ExperimentRegistrationRequestedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}
