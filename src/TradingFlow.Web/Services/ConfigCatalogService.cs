using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class ConfigCatalogService
{
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
            return new StrategyOption(path, Path.GetFileName(path), yamlReader.ReadStrategy(path));
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
