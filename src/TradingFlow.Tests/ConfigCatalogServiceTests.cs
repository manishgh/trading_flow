using System.Text.RegularExpressions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConfigCatalogServiceTests
{
    [Fact]
    public void GetBacktestConfigs_SkipsMissingStrategyReferenceWithoutFailingCatalog()
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
                "poet-mxl-rgti-mu-msft-intraday-v8-comparison-90d.yaml");
            var yaml = File.ReadAllText(sourceConfig);
            yaml = Regex.Replace(
                yaml,
                @"(?ms)^strategies:\s*(?:\r?\n\s+- .*)+",
                "strategies:\n  - strategies/missing-deleted-strategy.yaml");

            File.WriteAllText(Path.Combine(tempRoot, "configs", "backtest", "stale-ui-run.yaml"), yaml);

            var catalog = new ConfigCatalogService(new ProjectPaths(tempRoot), new SimpleYamlReader());

            var configs = catalog.GetBacktestConfigs();

            var config = Assert.Single(configs);
            Assert.Empty(config.Strategies);
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
