using System.Text.RegularExpressions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConfigCatalogServiceTests
{
    [Fact]
    public async Task GetStrategies_ReturnsCurrentResearchBootstrapSet()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        var strategies = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest);

        Assert.Equal(6, strategies.Count);
        Assert.Contains(strategies, strategy => strategy.FileName == "intraday-ema10-ema20-macd-volume.v1.yaml" && strategy.Audit is null);
        Assert.All(strategies, strategy => Assert.Equal(StrategyLifecycleState.Research, strategy.Lifecycle));
        Assert.DoesNotContain(strategies, strategy =>
            strategy.FileName == "brian_shannon_mta_avwap_strategies.yaml");
    }

    [Fact]
    public async Task PaperCatalogs_StartEmptyAndNeverFallBackToResearch()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunLive));
    }

    [Fact]
    public void GetConfig_ParsesCanonicalPaperProfilesAgainstLifecycleCatalog()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        var intraday = catalog.GetConfig(Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"));
        var swing = catalog.GetConfig(Path.Combine(repoRoot, "configs", "paper", "alpaca-paper-swing.yaml"));

        Assert.Empty(intraday.Strategies);
        Assert.Empty(swing.Strategies);
    }

    [Fact]
    public void GetBacktestConfigs_SkipsStaleRunConfigWithoutFailingCatalog()
    {
        var repoRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-catalog-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "configs", "backtest"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "configs", "strategies"));

        try
        {
            var sourceConfig = Path.Combine(
                repoRoot,
                "configs",
                "backtest",
                "intraday-backtest-profile.yaml");
            var yaml = File.ReadAllText(sourceConfig);
            yaml = Regex.Replace(
                yaml,
                @"(?ms)^strategies:\s*(?:\r?\n\s+- .*)+",
                "strategies:\n  - strategies/missing-deleted-strategy.yaml");

            File.WriteAllText(Path.Combine(tempRoot, "configs", "backtest", "stale-ui-run.yaml"), yaml);

            var reader = new SimpleYamlReader();
            var catalog = new ConfigCatalogService(
                new ProjectPaths(tempRoot),
                reader,
                CreateStrategyCatalog(repoRoot, reader),
                CreateExperimentStore(repoRoot, reader),
                new StrategyAuthorizationTestRegistry());

            var configs = catalog.GetBacktestConfigs();

            Assert.Empty(configs);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static StrategyArtifactCatalog CreateStrategyCatalog(string root, SimpleYamlReader reader) =>
        new(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);

    private static StrategyExperimentArtifactStore CreateExperimentStore(string root, SimpleYamlReader reader)
    {
        var catalog = CreateStrategyCatalog(root, reader);
        return new StrategyExperimentArtifactStore(
            Path.Combine(Path.GetTempPath(), "trading-flow-empty-experiments", Guid.NewGuid().ToString("N")),
            catalog,
            reader,
            TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance);
    }
}


