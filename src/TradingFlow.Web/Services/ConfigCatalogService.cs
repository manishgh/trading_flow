using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class ConfigCatalogService
{
    private static readonly HashSet<string> ActiveStrategyFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "intraday-ema10-ema20-macd-volume.v1.yaml",
        "minervini-trend-template-vcp.v4-trend-rider.yaml",
        "swing-reversal-reclaim-bull-quality-no-news.v1.yaml"
    };

    private static readonly IReadOnlyDictionary<string, StrategyAuditSummary> StrategyAudits =
        new Dictionary<string, StrategyAuditSummary>(StringComparer.OrdinalIgnoreCase)
        {
            ["swing-reversal-reclaim-bull-quality-no-news.v1.yaml"] = new(
                11.9532m,
                3.8461m,
                10,
                60.0m,
                "12.2 days",
                "data/backtest/results/shared/portfolio/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d-0001.json"),
            ["minervini-trend-template-vcp.v4-trend-rider.yaml"] = new(
                13.0165m,
                2.7407m,
                44,
                22.7m,
                "2-6 days",
                null)
        };

    private readonly ProjectPaths paths;
    private readonly SimpleYamlReader yamlReader;
    private readonly ILogger<ConfigCatalogService> logger;

    public ConfigCatalogService(ProjectPaths paths, SimpleYamlReader yamlReader, ILogger<ConfigCatalogService>? logger = null)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigCatalogService>.Instance;
    }

    public IReadOnlyList<RunConfigSummary> GetBacktestConfigs()
    {
        return GetRunConfigs(paths.BacktestConfigsRoot);
    }

    public IReadOnlyList<RunConfigSummary> GetPaperConfigs()
    {
        return GetRunConfigs(paths.PaperConfigsRoot);
    }

    public IReadOnlyList<StrategyOption> GetStrategies()
    {
        if (!Directory.Exists(paths.StrategiesRoot))
        {
            return Array.Empty<StrategyOption>();
        }

        return Directory.GetFiles(paths.StrategiesRoot, "*.yaml", SearchOption.TopDirectoryOnly)
            .Where(path => ActiveStrategyFileNames.Contains(Path.GetFileName(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(TryCreateStrategyOption)
            .OfType<StrategyOption>()
            .ToArray();
    }

    public RunConfigSummary GetConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var config = yamlReader.ReadBacktestRun(fullPath);
        var strategyOptions = config.Strategies
            .Select(TryCreateStrategyOption)
            .OfType<StrategyOption>()
            .ToArray();
        return new RunConfigSummary(fullPath, Path.GetFileName(fullPath), config, strategyOptions);
    }

    private IReadOnlyList<RunConfigSummary> GetRunConfigs(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<RunConfigSummary>();
        }

        return Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}archive{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}temp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ui-runs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}strategies{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(TryGetConfig)
            .OfType<RunConfigSummary>()
            .ToArray();
    }

    private RunConfigSummary? TryGetConfig(string path)
    {
        try
        {
            return GetConfig(path);
        }
        catch (Exception exception) when (IsCatalogRecoverable(exception))
        {
            logger.LogWarning(exception, "Skipping invalid run config {ConfigPath} while building catalog.", path);
            return null;
        }
    }

    private StrategyOption? TryCreateStrategyOption(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            StrategyAudits.TryGetValue(fileName, out var audit);
            return new StrategyOption(path, fileName, yamlReader.ReadStrategy(path), audit);
        }
        catch (Exception exception) when (IsCatalogRecoverable(exception))
        {
            logger.LogWarning(exception, "Skipping invalid strategy reference {StrategyPath} while building catalog.", path);
            return null;
        }
    }

    private static bool IsCatalogRecoverable(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException;
    }
}


