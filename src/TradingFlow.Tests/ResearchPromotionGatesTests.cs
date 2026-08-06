using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Tests;

/// <summary>
/// The gates exist to stop a profitable run reading as a promotable one. These
/// cover the two properties that matter: an unevaluated gate never becomes a
/// pass, and the integrated decision fails closed.
/// </summary>
public sealed class ResearchPromotionGatesTests
{
    [Fact]
    public void Evaluate_ReturnsTheSevenPreregisteredGates()
    {
        var assessment = ResearchPromotionGates.Evaluate(Strategy(), Config(), null);

        Assert.Equal(
            ["universe", "timing", "execution", "statistical", "cost_stress", "concentration", "paper_shadow"],
            assessment.Gates.Select(gate => gate.Key));
    }

    [Fact]
    public void Evaluate_MarksGatesWithNoEvaluatorAsNotEvaluated()
    {
        // A gates panel with invented values would license a promotion nobody
        // checked. The three without an evaluator have to say so.
        var assessment = ResearchPromotionGates.Evaluate(Strategy(), Config(), Eligible());

        foreach (var key in new[] { "statistical", "cost_stress", "paper_shadow" })
        {
            var gate = assessment.Gates.Single(item => item.Key == key);
            Assert.Equal(GateOutcome.NotEvaluated, gate.Outcome);
            Assert.False(String.IsNullOrWhiteSpace(gate.Reason));
        }
    }

    [Fact]
    public void Evaluate_FailsTheUniverseGateWhenMembershipIsNotPointInTime()
    {
        // A current wishlist carries survivorship the result cannot be separated
        // from, so the run is research-only whatever its return.
        var assessment = ResearchPromotionGates.Evaluate(Strategy(), Config(), universePromotion: null);

        var universe = assessment.Gates.Single(gate => gate.Key == "universe");
        Assert.Equal(GateOutcome.Failed, universe.Outcome);
        Assert.Contains("research-only", universe.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ResearchPromotionAssessment.FailedGate, assessment.Decision);
    }

    [Fact]
    public void Evaluate_FailsConcentrationWhenOneTickerCarriesTheResult()
    {
        var strategy = Strategy(
            Trade("AAA", net: 900m),
            Trade("BBB", net: 100m));

        var concentration = ResearchPromotionGates
            .Evaluate(strategy, Config(), Eligible())
            .Gates.Single(gate => gate.Key == "concentration");

        Assert.Equal(GateOutcome.Failed, concentration.Outcome);
        Assert.Contains("AAA", concentration.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_PassesConcentrationWhenProfitIsSpread()
    {
        var strategy = Strategy(
            Trade("AAA", net: 300m),
            Trade("BBB", net: 300m),
            Trade("CCC", net: 400m));

        var concentration = ResearchPromotionGates
            .Evaluate(strategy, Config(), Eligible())
            .Gates.Single(gate => gate.Key == "concentration");

        Assert.Equal(GateOutcome.Passed, concentration.Outcome);
    }

    [Fact]
    public void Evaluate_FailsExecutionWhenNoTransactionCostIsModelled()
    {
        var execution = ResearchPromotionGates
            .Evaluate(Strategy(), Config(buyFee: 0m, sellFee: 0m), Eligible())
            .Gates.Single(gate => gate.Key == "execution");

        Assert.Equal(GateOutcome.Failed, execution.Outcome);
        Assert.Contains("gross", execution.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_FailsTimingWhenIndicatorsAreNotWarmedBeforeScoring()
    {
        var timing = ResearchPromotionGates
            .Evaluate(Strategy(), Config(warmupBars: 0), Eligible())
            .Gates.Single(gate => gate.Key == "timing");

        Assert.Equal(GateOutcome.Failed, timing.Outcome);
    }

    [Fact]
    public void Evaluate_KeepsTheDecisionInResearchWhileAnyGateIsUnevaluated()
    {
        // Everything measurable passes, but three gates have no evaluator. The
        // decision fails closed rather than reading as promoted.
        var assessment = ResearchPromotionGates.Evaluate(
            Strategy(Trade("AAA", net: 300m), Trade("BBB", net: 300m), Trade("CCC", net: 400m)),
            Config(),
            Eligible());

        Assert.DoesNotContain(assessment.Gates, gate => gate.Outcome == GateOutcome.Failed);
        Assert.Equal(3, assessment.NotEvaluatedCount);
        Assert.Equal(ResearchPromotionAssessment.RetainResearch, assessment.Decision);
    }

    private static UniversePromotionEligibility Eligible() => new(true, []);

    private static StrategyBacktestResult Strategy(params BacktestTrade[] trades) => new(
        "strategy.v1",
        "Strategy v1",
        "configs/strategies/strategy.v1.yaml",
        100_000m,
        101_000m,
        1_000m,
        1.0m,
        0.05m,
        20,
        2.5m,
        12,
        trades.Length,
        4,
        trades.Count(trade => trade.NetProfit > 0m),
        trades.Count(trade => trade.NetProfit < 0m),
        trades);

    private static BacktestTrade Trade(string ticker, decimal net) => new(
        ticker,
        "Strategy v1",
        "long",
        new DateTimeOffset(2026, 3, 2, 14, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 4, 20, 0, 0, TimeSpan.Zero),
        100,
        10m,
        11m,
        9m,
        13m,
        "target",
        net,
        0m,
        net);

    private static BacktestRunConfig Config(
        int warmupBars = 150,
        decimal buyFee = 1m,
        decimal sellFee = 1m) => new(
            "run",
            "backtest",
            new EngineConfig("tpl", 0, 1000, warmupBars, 60, true),
            new TimeWindowConfig("rolling", 90, null, null, 60),
            [],
            "alpaca",
            ["5m"],
            "raw",
            "normalized",
            "results",
            "use_cache",
            new DerivedTimeframeConfig("1m"),
            new ValidationConfig(
                new OutOfSampleConfig(false, 0m),
                new WalkForwardConfig(false, 0, 0),
                new BenchmarkConfig(false, "SPY"),
                new DataQualityConfig(false, 0, 0, 0m),
                new BiasRiskConfig("wishlist", null, "alpaca_sip")),
            new ProviderConfig(new AlpacaProviderConfig("sip")),
            new PortfolioConfig(100_000m, 1m, 20m, 5, buyFee, sellFee, 1, true, 0m, 0m, 0m, 0m),
            new SignalSourceConfig("internal_candles", false, "", 300, 60),
            new ExecutionConfig("backtest", "none", true, false, "bracket", "gtc", "limit", false),
            new NewsConfig(false, "none", 60, -0.5m),
            new ScreenerConfig(false, "finviz", []),
            new ArtifactRetentionConfig("full"),
            []);
}
