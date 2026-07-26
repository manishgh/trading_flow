using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Tests;

public sealed class PromotionEvaluatorTests
{
    [Fact]
    public void Evaluate_RobustStrategy_IsEligible()
    {
        var strategy = StrategyResult(
            returnPct: 12m,
            drawdownPct: 4m,
            acceptedTrades: 40,
            trades: SpreadTrades(("AAA", 300m), ("BBB", 300m), ("CCC", 400m)));
        var validation = Validation(oosReturn: 5m, oosTrades: 12, benchmarkExcess: 6m, walkForward: new[] { 2m, 1m, 3m });

        var assessment = PromotionEvaluator.Evaluate(
            strategy,
            validation,
            universePromotion: EligibleUniverse());

        Assert.True(assessment.Eligible, string.Join("; ", assessment.FailedChecks));
    }

    [Fact]
    public void Evaluate_TooFewTradesAndOneTickerCarriesIt_IsRejected()
    {
        var strategy = StrategyResult(
            returnPct: 20m,
            drawdownPct: 3m,
            acceptedTrades: 8,
            trades: SpreadTrades(("AAA", 950m), ("BBB", 50m)));
        var validation = Validation(oosReturn: 5m, oosTrades: 4, benchmarkExcess: 2m, walkForward: new[] { 1m, 2m });

        var assessment = PromotionEvaluator.Evaluate(strategy, validation);

        Assert.False(assessment.Eligible);
        Assert.Contains(assessment.FailedChecks, reason => reason.StartsWith("sample_size"));
        Assert.Contains(assessment.FailedChecks, reason => reason.StartsWith("ticker_concentration"));
    }

    [Fact]
    public void Evaluate_MissingOutOfSampleAndBenchmark_IsRejected()
    {
        var strategy = StrategyResult(
            returnPct: 15m,
            drawdownPct: 5m,
            acceptedTrades: 50,
            trades: SpreadTrades(("AAA", 500m), ("BBB", 500m)));
        var validation = new BacktestValidationReport(
            new OutOfSampleValidation(false, 0m, null, Array.Empty<StrategyPartitionResult>()),
            Array.Empty<WalkForwardWindowResult>(),
            new BenchmarkValidation(false, "SPY", null, Array.Empty<StrategyBenchmarkComparison>()),
            new DataQualityValidation(0, 0, 0, 0, 0, Array.Empty<string>()),
            new BiasRiskValidation("static", null, "none", true, false, Array.Empty<string>()));

        var assessment = PromotionEvaluator.Evaluate(strategy, validation);

        Assert.False(assessment.Eligible);
        Assert.Contains(assessment.FailedChecks, reason => reason.StartsWith("out_of_sample"));
        Assert.Contains(assessment.FailedChecks, reason => reason.StartsWith("benchmark"));
        Assert.Contains(assessment.FailedChecks, reason => reason.StartsWith("walk_forward_windows"));
    }

    [Fact]
    public void Evaluate_MissingPointInTimeUniverseEvidence_IsRejected()
    {
        var strategy = StrategyResult(
            returnPct: 12m,
            drawdownPct: 4m,
            acceptedTrades: 40,
            trades: SpreadTrades(("AAA", 300m), ("BBB", 300m), ("CCC", 400m)));
        var validation = Validation(oosReturn: 5m, oosTrades: 12, benchmarkExcess: 6m, walkForward: new[] { 2m, 1m, 3m });

        var assessment = PromotionEvaluator.Evaluate(strategy, validation);

        Assert.False(assessment.Eligible);
        Assert.Contains("point_in_time_universe_evidence_missing", assessment.FailedChecks);
    }

    private static UniversePromotionEligibility EligibleUniverse() =>
        new(true, Array.Empty<string>());

    private static IReadOnlyList<BacktestTrade> SpreadTrades(params (string Ticker, decimal Net)[] tickerNets)
    {
        return tickerNets
            .Select(item => new BacktestTrade(
                item.Ticker,
                "S",
                "long",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                1,
                10m,
                11m,
                9m,
                13m,
                "take_profit",
                item.Net,
                0m,
                item.Net))
            .ToArray();
    }

    private static StrategyBacktestResult StrategyResult(
        decimal returnPct,
        decimal drawdownPct,
        int acceptedTrades,
        IReadOnlyList<BacktestTrade> trades)
    {
        return new StrategyBacktestResult(
            "strat-1",
            "Strategy 1",
            "runtime",
            10000m,
            10000m + (10000m * returnPct / 100m),
            10000m * returnPct / 100m,
            returnPct,
            returnPct / 30m,
            30,
            drawdownPct,
            acceptedTrades,
            acceptedTrades,
            0,
            trades.Count(trade => trade.NetProfit > 0),
            trades.Count(trade => trade.NetProfit <= 0),
            trades);
    }

    private static BacktestValidationReport Validation(
        decimal oosReturn,
        int oosTrades,
        decimal benchmarkExcess,
        IReadOnlyList<decimal> walkForward)
    {
        var oos = new OutOfSampleValidation(
            true,
            30m,
            DateTimeOffset.UtcNow,
            new[]
            {
                new StrategyPartitionResult(
                    "strat-1",
                    "Strategy 1",
                    "runtime",
                    new TradePartitionResult(30, 100m, 8m, 45m, 3m),
                    new TradePartitionResult(oosTrades, oosReturn * 10m, oosReturn, 40m, 3m))
            });

        var windows = walkForward
            .Select((ret, index) => new WalkForwardWindowResult(
                DateTimeOffset.UtcNow.AddDays(index * 30),
                DateTimeOffset.UtcNow.AddDays(index * 30 + 30),
                new[] { new StrategyWindowResult("strat-1", "Strategy 1", "runtime", 10, ret * 10m, ret, 2m) }))
            .ToArray();

        var benchmark = new BenchmarkValidation(
            true,
            "SPY",
            5m,
            new[] { new StrategyBenchmarkComparison("strat-1", "Strategy 1", "runtime", 5m + benchmarkExcess, benchmarkExcess) });

        return new BacktestValidationReport(
            oos,
            windows,
            benchmark,
            new DataQualityValidation(0, 0, 0, 0, 0, Array.Empty<string>()),
            new BiasRiskValidation("static", null, "none", true, false, Array.Empty<string>()));
    }
}
