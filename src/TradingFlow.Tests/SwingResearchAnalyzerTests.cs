using TradingFlow.Backtesting.Research;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;

namespace TradingFlow.Tests;

public sealed class SwingResearchAnalyzerTests
{
    [Fact]
    public void Analyze_BucketsSwingTradeIntoMarkToMarketWeeks()
    {
        var bars = BuildDailyBars("APP", 100m, 104m, 112m, 120m, 116m, 130m);
        var trade = new BacktestTrade(
            "APP",
            "Swing",
            "long",
            bars[0].Timestamp,
            bars[^1].Timestamp,
            10,
            100m,
            130m,
            95m,
            140m,
            "take_profit",
            300m,
            2m,
            298m);
        var result = CreateResult("Swing", 10000m, 10298m, [trade]);

        var report = new SwingResearchAnalyzer().Analyze(
            result,
            "Swing",
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["APP"] = bars },
            new SwingResearchOptions(MinimumOpportunityMovePct: 10m));

        Assert.Equal(2, report.WeeklyEquity.Count);
        Assert.Equal(10160m, report.WeeklyEquity[0].WeekEndEquity);
        Assert.Equal(1.6m, report.WeeklyEquity[0].GainPct);
        Assert.Equal(10298m, report.WeeklyEquity[1].WeekEndEquity);
        Assert.Equal(1.38m, report.WeeklyEquity[1].GainPct);
    }

    [Fact]
    public void Analyze_ClassifiesCapturedAndMissedOpportunities()
    {
        var appBars = BuildDailyBars("APP", 100m, 106m, 112m, 118m, 125m, 130m);
        var msftBars = BuildDailyBars("MSFT", 100m, 102m, 105m, 112m, 115m, 117m);
        var trade = new BacktestTrade(
            "APP",
            "Swing",
            "long",
            appBars[1].Timestamp,
            appBars[^1].Timestamp,
            10,
            106m,
            130m,
            100m,
            140m,
            "take_profit",
            240m,
            2m,
            238m);
        var result = CreateResult("Swing", 10000m, 10238m, [trade]);

        var report = new SwingResearchAnalyzer().Analyze(
            result,
            "Swing",
            new Dictionary<string, IReadOnlyList<OhlcvBar>>
            {
                ["APP"] = appBars,
                ["MSFT"] = msftBars
            },
            new SwingResearchOptions(MinimumOpportunityMovePct: 10m));

        var appOpportunity = report.Tickers.Single(x => x.Ticker == "APP").LongOpportunities.Single();
        var msftOpportunity = report.Tickers.Single(x => x.Ticker == "MSFT").LongOpportunities.Single();

        Assert.True(appOpportunity.WasCaptured);
        Assert.Equal("captured", appOpportunity.Classification);
        Assert.False(msftOpportunity.WasCaptured);
        Assert.Equal("missed", msftOpportunity.Classification);
    }

    private static IReadOnlyList<OhlcvBar> BuildDailyBars(string ticker, params decimal[] closes)
    {
        var start = DateTimeOffset.Parse("2026-05-04T04:00:00Z");
        return closes.Select((close, index) =>
        {
            var timestamp = start.AddDays(index < 5 ? index : index + 2);
            return new OhlcvBar(
                ticker,
                timestamp,
                "1d",
                close - 1m,
                close + 2m,
                close - 2m,
                close,
                1_000_000m + index);
        }).ToArray();
    }

    private static BacktestResult CreateResult(string strategyName, decimal startingCapital, decimal endingCapital, IReadOnlyList<BacktestTrade> trades)
    {
        var started = DateTimeOffset.Parse("2026-05-04T04:00:00Z");
        var strategy = new StrategyBacktestResult(
            "strategy-1",
            strategyName,
            "test",
            startingCapital,
            endingCapital,
            endingCapital - startingCapital,
            Decimal.Round(((endingCapital - startingCapital) / startingCapital) * 100m, 4),
            0m,
            6,
            0m,
            trades.Count,
            trades.Count,
            0,
            trades.Count(x => x.NetProfit > 0),
            trades.Count(x => x.NetProfit < 0),
            trades);
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

        return new BacktestResult(
            "test-run",
            "test-run.json",
            started,
            started.AddDays(7),
            startingCapital,
            endingCapital,
            endingCapital - startingCapital,
            strategy.TotalReturnPct,
            0m,
            6,
            0m,
            winner,
            6,
            trades.Count,
            trades.Count,
            0,
            strategy.WinningTradeCount,
            strategy.LosingTradeCount,
            [strategy],
            [TickerBacktestResult.Completed("APP", 6, trades.Count)],
            CreateValidation(),
            trades,
            [],
            []);
    }

    private static BacktestValidationReport CreateValidation()
    {
        return new BacktestValidationReport(
            new OutOfSampleValidation(false, 0m, null, []),
            [],
            new BenchmarkValidation(false, "", null, []),
            new DataQualityValidation(0, 0, 0, 0, 0, []),
            new BiasRiskValidation("test", null, "none", false, false, []));
    }
}
