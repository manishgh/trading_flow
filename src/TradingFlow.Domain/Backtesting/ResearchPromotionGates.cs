namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// A gate's outcome. <see cref="NotEvaluated"/> is a first-class state, not a
/// soft pass: a gate with no evaluator has told us nothing, and rendering it as
/// either pass or fail would be a claim the system cannot support.
/// </summary>
public enum GateOutcome
{
    Passed,
    Failed,
    NotEvaluated
}

/// <summary>One preregistered promotion gate and what it actually observed.</summary>
/// <param name="Key">Stable key.</param>
/// <param name="Label">Operator-facing name.</param>
/// <param name="Outcome">Pass, fail, or not evaluated.</param>
/// <param name="Reason">
/// What was measured or asserted. For a not-evaluated gate this states what is
/// missing, so the gap is legible rather than silent.
/// </param>
public sealed record PromotionGate(
    string Key,
    string Label,
    GateOutcome Outcome,
    string Reason);

/// <summary>
/// The integrated research decision for one strategy run.
/// </summary>
/// <remarks>
/// Fails closed. A missing gate keeps the decision at
/// <c>RETAIN_RESEARCH</c>; only a run where every gate has actually passed can
/// read as promoted. A profitable run is not a promotion, and this record is
/// what stops the screen implying otherwise.
/// </remarks>
public sealed record ResearchPromotionAssessment(
    IReadOnlyList<PromotionGate> Gates,
    string Decision)
{
    public const string RetainResearch = "RETAIN_RESEARCH";
    public const string PaperShadow = "PAPER_SHADOW";
    public const string Promoted = "PROMOTED";
    public const string FailedGate = "FAILED_GATE";

    public int PassedCount => Gates.Count(gate => gate.Outcome == GateOutcome.Passed);

    public int NotEvaluatedCount => Gates.Count(gate => gate.Outcome == GateOutcome.NotEvaluated);
}

/// <summary>
/// Evaluates the seven preregistered gates from
/// <c>docs/operating-boundaries.md</c> against one strategy's run.
///
/// Three of the seven are measurable from the run itself, two are assertions
/// about how the run was configured, and two need work that does not exist yet.
/// Those last two return <see cref="GateOutcome.NotEvaluated"/> rather than a
/// guess, because a gates panel with invented values is worse than no panel: it
/// would license a promotion nobody actually checked.
/// </summary>
public static class ResearchPromotionGates
{
    /// <summary>
    /// Share of a strategy's net profit the single best ticker may contribute
    /// before the "edge" is really one lucky name.
    /// </summary>
    public const decimal ConcentrationCeilingPct = 40m;

    public static ResearchPromotionAssessment Evaluate(
        StrategyBacktestResult strategy,
        BacktestRunConfig? config,
        UniversePromotionEligibility? universePromotion)
    {
        var gates = new List<PromotionGate>
        {
            EvaluateUniverse(universePromotion),
            EvaluateTiming(config),
            EvaluateExecution(config),
            NotEvaluated(
                "statistical",
                "Statistical",
                "No out-of-sample or walk-forward significance test has been run for this strategy."),
            NotEvaluated(
                "cost_stress",
                "Cost stress",
                "No fee, slippage or spread stress sweep has been run for this strategy."),
            EvaluateConcentration(strategy),
            NotEvaluated(
                "paper_shadow",
                "Paper shadow",
                "No paper-shadow period has been recorded against this run.")
        };

        return new ResearchPromotionAssessment(gates, ResolveDecision(gates));
    }

    /// <summary>
    /// A current wishlist is not point-in-time membership. A run built on one
    /// carries survivorship the result cannot be separated from, so it is
    /// research-only whatever its return.
    /// </summary>
    private static PromotionGate EvaluateUniverse(UniversePromotionEligibility? universePromotion)
    {
        if (universePromotion is null)
        {
            return new PromotionGate(
                "universe",
                "Universe",
                GateOutcome.Failed,
                "Membership came from a current wishlist rather than point-in-time evidence, so the run is research-only.");
        }

        return universePromotion.IsEligible
            ? new PromotionGate("universe", "Universe", GateOutcome.Passed,
                "Point-in-time membership evidence is on file for every symbol in the run.")
            : new PromotionGate("universe", "Universe", GateOutcome.Failed,
                String.Join("; ", universePromotion.Failures));
    }

    /// <summary>
    /// Timing is a property of the run configuration, so it is asserted rather
    /// than measured: a completed-bar signal with a later-bar confirmation and a
    /// warm-up window is either configured or it is not.
    /// </summary>
    private static PromotionGate EvaluateTiming(BacktestRunConfig? config)
    {
        if (config is null)
        {
            return NotEvaluated("timing", "Timing", "The run configuration was not recorded with this result.");
        }

        var warmupBars = config.Engine.IndicatorWarmupBars;
        var warmupDays = config.TimeWindow.WarmupLookbackDays;
        var ok = warmupBars > 0 && warmupDays > 0;
        return new PromotionGate(
            "timing",
            "Timing",
            ok ? GateOutcome.Passed : GateOutcome.Failed,
            ok
                ? $"Completed-bar features with {warmupBars} warm-up bars and a {warmupDays}-day warm-up window."
                : $"Indicators are not warmed before scoring: {warmupBars} warm-up bars over {warmupDays} warm-up days.");
    }

    private static PromotionGate EvaluateExecution(BacktestRunConfig? config)
    {
        if (config is null)
        {
            return NotEvaluated("execution", "Execution", "The run configuration was not recorded with this result.");
        }

        // Fees on both sides are the minimum honest cost model. A run with none
        // is reporting a gross number as if it were net.
        var modelsCost = config.Portfolio.FixedBuyFee > 0m || config.Portfolio.FixedSellFee > 0m;
        return new PromotionGate(
            "execution",
            "Execution",
            modelsCost ? GateOutcome.Passed : GateOutcome.Failed,
            modelsCost
                ? $"Next-bar execution with {config.Portfolio.FixedBuyFee:C2} buy and {config.Portfolio.FixedSellFee:C2} sell fees applied."
                : "No transaction cost is modelled, so the reported result is gross rather than net.");
    }

    /// <summary>
    /// Measured directly from the completed trades: what share of the net profit
    /// came from the single best ticker.
    /// </summary>
    private static PromotionGate EvaluateConcentration(StrategyBacktestResult strategy)
    {
        var totalNet = strategy.CompletedTrades.Sum(trade => trade.NetProfit);
        if (strategy.CompletedTrades.Count == 0)
        {
            return NotEvaluated("concentration", "Concentration", "The strategy produced no completed trades to measure.");
        }

        if (totalNet <= 0m)
        {
            // Nothing to be concentrated in: a losing run is not carried by one
            // winner, and reporting a share here would be arithmetic noise.
            return new PromotionGate(
                "concentration",
                "Concentration",
                GateOutcome.Passed,
                "The run is not profitable overall, so no single ticker is carrying the result.");
        }

        var topTicker = strategy.CompletedTrades
            .GroupBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Ticker = group.Key, Net = group.Sum(trade => trade.NetProfit) })
            .OrderByDescending(item => item.Net)
            .First();
        var sharePct = topTicker.Net <= 0m ? 0m : topTicker.Net / totalNet * 100m;
        var ok = sharePct <= ConcentrationCeilingPct;
        return new PromotionGate(
            "concentration",
            "Concentration",
            ok ? GateOutcome.Passed : GateOutcome.Failed,
            $"{topTicker.Ticker} contributed {sharePct:F1}% of net profit against a {ConcentrationCeilingPct:F0}% ceiling.");
    }

    private static PromotionGate NotEvaluated(string key, string label, string reason) =>
        new(key, label, GateOutcome.NotEvaluated, reason);

    /// <summary>
    /// Fails closed. Any failing gate is a refusal; any unevaluated gate keeps
    /// the run in research. Only a run where all seven actually passed reads as
    /// promoted, and a run that clears everything except the shadow period is
    /// ready for that period rather than for live.
    /// </summary>
    private static string ResolveDecision(IReadOnlyList<PromotionGate> gates)
    {
        if (gates.Any(gate => gate.Outcome == GateOutcome.Failed))
        {
            return ResearchPromotionAssessment.FailedGate;
        }

        if (gates.All(gate => gate.Outcome == GateOutcome.Passed))
        {
            return ResearchPromotionAssessment.Promoted;
        }

        var shadowOutstanding = gates
            .Where(gate => gate.Outcome != GateOutcome.Passed)
            .All(gate => gate.Key == "paper_shadow");
        return shadowOutstanding
            ? ResearchPromotionAssessment.PaperShadow
            : ResearchPromotionAssessment.RetainResearch;
    }
}
