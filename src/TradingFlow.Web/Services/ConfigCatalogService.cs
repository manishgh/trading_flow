using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class ConfigCatalogService
{
    private readonly ProjectPaths paths;
    private readonly SimpleYamlReader yamlReader;

    public ConfigCatalogService(ProjectPaths paths, SimpleYamlReader yamlReader)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
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
            .Select(path => new StrategyOption(
                path,
                Path.GetFileName(path),
                yamlReader.ReadStrategy(path)))
            .ToArray();
    }

    public RunConfigSummary GetConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var config = yamlReader.ReadBacktestRun(fullPath);
        var strategyOptions = config.Strategies
            .Select(path => new StrategyOption(path, Path.GetFileName(path), yamlReader.ReadStrategy(path)))
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
            .Select(GetConfig)
            .ToArray();
    }
}
