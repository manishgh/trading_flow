using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Tests;

public sealed class BacktestArtifactProjectorTests
{
    [Fact]
    public void Project_SummaryRetention_RemovesTradeAndOrderDetailButKeepsAnalytics()
    {
        var result = CreateResult();

        var projected = BacktestArtifactProjector.Project(result, new ArtifactRetentionConfig("summary"));

        Assert.Empty(projected.CompletedTrades);
        Assert.Empty(projected.AcceptedOrders);
        Assert.All(projected.StrategyResults, strategy => Assert.Empty(strategy.CompletedTrades));
        Assert.Equal(result.AcceptedTradeCount, projected.AcceptedTradeCount);
        Assert.Equal(result.RejectedTradeCount, projected.RejectedTradeCount);
        Assert.Equal(result.Winner, projected.Winner);
        Assert.Equal(result.AverageDailyReturnPct, projected.AverageDailyReturnPct);
        Assert.Equal(result.TradingDayCount, projected.TradingDayCount);
        Assert.Equal(result.Diagnostics, projected.Diagnostics);
        Assert.Equal(result.Diagnostics.Single().DailyPnl, projected.Diagnostics.Single().DailyPnl);
        Assert.Equal(result.TickerResults, projected.TickerResults);
        Assert.Equal(result.Validation, projected.Validation);
    }

    [Fact]
    public void Project_FullRetention_KeepsFullResultDetail()
    {
        var result = CreateResult();

        var projected = BacktestArtifactProjector.Project(result, new ArtifactRetentionConfig("full"));

        Assert.Same(result, projected);
        Assert.Single(projected.CompletedTrades);
        Assert.Single(projected.AcceptedOrders);
        Assert.Single(projected.StrategyResults.Single().CompletedTrades);
    }

    private static BacktestResult CreateResult()
    {
        var started = DateTimeOffset.Parse("2026-06-09T08:00:00Z");
        var trade = new BacktestTrade(
            "AMD",
            "Swing",
            "long",
            started,
            started.AddHours(1),
            10,
            100m,
            110m,
            95m,
            120m,
            "target",
            100m,
            2m,
            98m);
        var strategy = new StrategyBacktestResult(
            "strategy-1",
            "Swing",
            "test",
            100000m,
            100098m,
            98m,
            0.098m,
            0.049m,
            2,
            0.01m,
            1,
            1,
            0,
            1,
            0,
            [trade]);
        var winner = new WinnerStrategySummary(
            strategy.StrategyId,
            strategy.StrategyName,
            strategy.EndingCapital,
            strategy.NetProfit,
            strategy.TotalReturnPct,
            strategy.AverageDailyReturnPct,
            strategy.MaxDrawdownPct,
            strategy.AcceptedTradeCount,
            strategy.RejectedTradeCount);
        var diagnostics = new StrategyDiagnosticReport(
            strategy.StrategyId,
            strategy.StrategyName,
            10,
            1,
            1,
            100m,
            98m,
            0m,
            0m,
            new DailyPnlSummary(
                2,
                1,
                1,
                0,
                98m,
                49m,
                98m,
                98m,
                DateOnly.FromDateTime(started.UtcDateTime.Date),
                98m,
                DateOnly.FromDateTime(started.UtcDateTime.Date)),
            [new DirectionPnlSummary("long", 1, 1, 0, 98m, 98m, 98m, 98m)],
            new Dictionary<string, int> { ["target"] = 1 },
            new Dictionary<string, int> { ["rvol"] = 2 },
            new Dictionary<string, IReadOnlyList<string>> { ["rvol"] = ["rvol (Actual: 0.5, Required: 2.0)"] },
            ["ok"]);

        return new BacktestResult(
            "run",
            "run.json",
            started,
            started.AddHours(2),
            100000m,
            100098m,
            98m,
            0.098m,
            0.049m,
            2,
            0.01m,
            winner,
            10,
            1,
            1,
            0,
            1,
            0,
            [strategy],
            [TickerBacktestResult.Completed("AMD", 10, 1)],
            CreateValidation(),
            [trade],
            [diagnostics],
            [new FinalizedOrder("AMD", "Swing", 10, 100m, 95m, 120m, started)]);
    }

    private static BacktestValidationReport CreateValidation()
    {
        return new BacktestValidationReport(
            new OutOfSampleValidation(false, 0m, null, []),
            [],
            new BenchmarkValidation(false, "", null, []),
            new DataQualityValidation(10, 0, 0, 0, 0, []),
            new BiasRiskValidation("test", null, "none", false, false, []));
    }
}
