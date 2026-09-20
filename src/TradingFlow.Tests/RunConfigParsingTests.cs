using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    [Fact]
    public void RuntimeStrategyCatalog_ContainsOnlyDailySwingSetups()
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var paths = Directory.GetFiles(
            Path.Combine(root, "configs", "strategies"),
            "*.yaml",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            var strategy = reader.ReadStrategy(path);
            Assert.Equal("1d", strategy.Timeframe, ignoreCase: true);
        }
    }

    [Fact]
    public void BacktestResearchStrategies_ContainOnlyDailySwingSetups()
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var paths = Directory.GetFiles(
            Path.Combine(root, "configs", "backtest", "strategies"),
            "*.yaml",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            var strategy = reader.ReadStrategy(path);
            Assert.Equal("1d", strategy.Timeframe, ignoreCase: true);
        }
    }

    [Theory]
    [InlineData("minervini-trend-template-vcp.v4-trend-rider.yaml")]
    [InlineData("swing-reversal-reclaim-bull-quality-no-news.v1.yaml")]
    [InlineData("swing-mean-reversion-reclaim.v1.yaml")]
    [InlineData("swing-catalyst-drift.v5.yaml")]
    public void StrategySerialization_RoundTripsSharedSwingBrain(string strategyFile)
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(root, "configs", "strategies", strategyFile));
        var tempPath = Path.Combine(Path.GetTempPath(), $"swing-roundtrip-{Guid.NewGuid():N}.yaml");

        File.WriteAllText(tempPath, RunConfigWriter.SerializeStrategyYaml(original));
        try
        {
            var roundTripped = reader.ReadStrategy(tempPath);
            Assert.Equal(original.EntryRules, roundTripped.EntryRules);
            Assert.Equal(original.ExitRules, roundTripped.ExitRules);
            Assert.Equal(original.Execution, roundTripped.Execution);
            Assert.Equal(original.Regime, roundTripped.Regime);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void SwingBacktestProfile_UsesWishlistUniverseAndDailyStrategies()
    {
        var root = FindRepositoryRoot();
        var run = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));

        Assert.NotEmpty(run.Strategies);
        Assert.All(run.Strategies, path =>
            Assert.Equal("1d", new SimpleYamlReader().ReadStrategy(path).Timeframe, ignoreCase: true));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate TradingFlow.sln.");
    }
}
