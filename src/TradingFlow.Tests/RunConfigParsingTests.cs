using TradingFlow.Engine.Configuration;
using Xunit;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    [Fact]
    public void AlpacaPaperConfig_UsesSipFeedAndIntradayTimeframes()
    {
        var repoRoot = FindRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml");

        var config = new SimpleYamlReader().ReadBacktestRun(configPath);

        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.Contains("5m", config.Intervals);
        Assert.Contains("15m", config.Intervals);
        Assert.Contains("1h", config.Intervals);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "configs")) &&
                File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find trading_flow repository root.");
    }
}
