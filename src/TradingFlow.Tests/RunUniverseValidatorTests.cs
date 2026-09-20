using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class RunUniverseValidatorTests
{
    [Fact]
    public void BaseWishlistProfileCannotExecuteBeforeResolution()
    {
        var root = TestRepository.FindRoot();
        var run = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RunUniverseValidator.RequireResolved(run));

        Assert.Contains("unresolved wishlist template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyTickerListWithoutResolvedSnapshotCannotBypassTheContract()
    {
        var root = TestRepository.FindRoot();
        var template = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));
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
            Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));
        var resolved = template with
        {
            Tickers = ["AAPL"],
            Universe = template.Universe! with { Mode = TradingFlow.Domain.Backtesting.UniverseConfig.ResolvedSnapshotMode }
        };

        RunUniverseValidator.RequireResolved(resolved);
    }
}
