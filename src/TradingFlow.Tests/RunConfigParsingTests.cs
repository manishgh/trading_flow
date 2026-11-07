using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    [Fact]
    public void OpeningGapPolicy_SurvivesGeneratedRunFiles()
    {
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(FindRepositoryRoot(), "configs", "backtest", "strategies", "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        var path = Path.Combine(Path.GetTempPath(), $"opening-gap-roundtrip-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(original));
            Assert.Equal(original.Execution, reader.ReadStrategy(path).Execution);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpeningGapPolicy_IsOptInAndRejectsUnknownValues()
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategies = Path.Combine(root, "configs", "backtest", "strategies");
        var kept = Path.Combine(strategies, "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml");
        Assert.Equal(ExecutionRules.KeepAndExitNextOpen, reader.ReadStrategy(kept).Execution.OpeningGapPolicy);
        Assert.Null(reader.ReadStrategy(Path.Combine(strategies, "recovery-sma5-news-avoidance.bt-v1.yaml")).Execution.OpeningGapPolicy);

        var invalid = Path.Combine(Path.GetTempPath(), $"opening-gap-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(invalid, File.ReadAllText(kept).Replace(ExecutionRules.KeepAndExitNextOpen, "guess"));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(invalid));
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    [Fact]
    public void BacktestResearchStrategies_UseDailySetupsUnlessDeclaredSameDay()
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
            if (StrategyHorizonResolver.Resolve(strategy) == "swing")
                Assert.Equal("1d", strategy.Timeframe, ignoreCase: true);
            else
                Assert.NotEqual("1d", strategy.Timeframe, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("src/TradingFlow.Tests/Fixtures/Strategies/daily-trailing-swing.fixture.yaml")]
    [InlineData("configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml")]
    [InlineData("configs/backtest/strategies/recovery-sma5-news-avoidance.bt-v1.yaml")]
    public void StrategySerialization_RoundTripsSharedSwingBrain(string strategyFile)
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(root, strategyFile));
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
