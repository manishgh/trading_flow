using System.Text.RegularExpressions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConfigCatalogServiceTests
{
    [Fact]
    public void GetStrategies_ReturnsOnlyPromotedStrategies_WithAuditSummaries()
    {
        var repoRoot = TestRepository.FindRoot();
        var catalog = new ConfigCatalogService(new ProjectPaths(repoRoot), new SimpleYamlReader());

        var strategies = catalog.GetStrategies();

        Assert.Equal(3, strategies.Count);
        Assert.Contains(strategies, strategy => strategy.FileName == "intraday-ema10-ema20-macd-volume.v1.yaml" && strategy.Audit is null);
        
        Assert.DoesNotContain(strategies, strategy =>
            strategy.FileName == "brian_shannon_mta_avwap_strategies.yaml");
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

            var catalog = new ConfigCatalogService(new ProjectPaths(tempRoot), new SimpleYamlReader());

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
}


