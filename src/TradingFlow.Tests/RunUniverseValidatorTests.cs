using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class RunUniverseValidatorTests
{
    [Theory]
    [InlineData("intraday-backtest-profile.yaml")]
    [InlineData("swing-backtest-profile.yaml")]
    public void BaseWishlistProfileCannotExecuteBeforeResolution(string fileName)
    {
        var root = TestRepository.FindRoot();
        var run = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", fileName));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RunUniverseValidator.RequireResolved(run));

        Assert.Contains("unresolved wishlist template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyTickerListWithoutResolvedSnapshotCannotBypassTheContract()
    {
        var root = TestRepository.FindRoot();
        var template = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "intraday-backtest-profile.yaml"));
        var missingProvenance = template with
        {
            Tickers = ["AAPL"],
            Universe = null
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RunUniverseValidator.RequireResolved(missingProvenance));

        Assert.Contains("does not declare a resolved universe snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvedSnapshotWithSymbolsPasses()
    {
        var root = TestRepository.FindRoot();
        var template = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "intraday-backtest-profile.yaml"));
        var resolved = template with
        {
            Tickers = ["AAPL"],
            Universe = template.Universe! with { Mode = TradingFlow.Domain.Backtesting.UniverseConfig.ResolvedSnapshotMode }
        };

        RunUniverseValidator.RequireResolved(resolved);
    }
}
