namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Gate that decides whether a backtested strategy is allowed into the promoted
/// runtime catalog. It exists to stop the v1..v10 overfitting treadmill: a strategy
/// tuned on a tiny basket can show a great full-window number while failing every
/// honest robustness check. See docs/edge-recovery-master-plan.md phase 0.2.
/// </summary>
public sealed record PromotionCriteria(
    int MinAcceptedTrades = 30,
    decimal MaxSingleTickerProfitSharePct = 40m,
    decimal MinTotalReturnPct = 0m,
    decimal MinReturnToDrawdownRatio = 1.0m,
    bool RequireOutOfSample = true,
    bool RequirePositiveOutOfSample = true,
    bool RequireBenchmarkBeat = true,
    int MinWalkForwardWindows = 2,
    decimal MinWalkForwardWinRatePct = 50m)
{
    public static PromotionCriteria Default { get; } = new();
}

public sealed record PromotionAssessment(
    string StrategyId,
    string StrategyName,
    bool Eligible,
    IReadOnlyList<string> FailedChecks,
    IReadOnlyList<string> PassedChecks);

public static class PromotionEvaluator
{
    public static PromotionAssessment Evaluate(
        StrategyBacktestResult strategy,
        BacktestValidationReport validation,
        PromotionCriteria? criteria = null)
    {
        criteria ??= PromotionCriteria.Default;
        var failed = new List<string>();
        var passed = new List<string>();

        Check(
            strategy.AcceptedTradeCount >= criteria.MinAcceptedTrades,
            $"sample_size ({strategy.AcceptedTradeCount} accepted, need >= {criteria.MinAcceptedTrades})");

        Check(
            strategy.TotalReturnPct > criteria.MinTotalReturnPct,
            $"total_return ({strategy.TotalReturnPct:F2}% <= {criteria.MinTotalReturnPct:F2}%)");

        var ratio = strategy.MaxDrawdownPct > 0m
            ? strategy.TotalReturnPct / strategy.MaxDrawdownPct
            : (strategy.TotalReturnPct > 0m ? decimal.MaxValue : 0m);
        Check(
            ratio >= criteria.MinReturnToDrawdownRatio,
            $"return_to_drawdown ({FormatRatio(ratio)} < {criteria.MinReturnToDrawdownRatio:F2})");

        Check(
            SingleTickerSharePct(strategy) <= criteria.MaxSingleTickerProfitSharePct,
            $"ticker_concentration (top ticker {SingleTickerSharePct(strategy):F1}% of net profit, max {criteria.MaxSingleTickerProfitSharePct:F1}%)");

        if (criteria.RequireOutOfSample)
        {
            EvaluateOutOfSample(strategy, validation, criteria, failed, passed);
        }

        if (criteria.RequireBenchmarkBeat)
        {
            EvaluateBenchmark(strategy, validation, failed, passed);
        }

        if (criteria.MinWalkForwardWindows > 0)
        {
            EvaluateWalkForward(strategy, validation, criteria, failed, passed);
        }

        return new PromotionAssessment(strategy.StrategyId, strategy.StrategyName, failed.Count == 0, failed, passed);

        void Check(bool ok, string label)
        {
            (ok ? passed : failed).Add(label);
        }
    }

    // Share of the strategy's net profit contributed by its single most profitable ticker.
    // A high value means the "edge" is really one lucky name, not a repeatable rule.
    private static decimal SingleTickerSharePct(StrategyBacktestResult strategy)
    {
        var totalNet = strategy.CompletedTrades.Sum(trade => trade.NetProfit);
        if (totalNet <= 0m)
        {
            // Not led by a single winner if there is no aggregate profit to lead.
            return 0m;
        }

        var topTickerNet = strategy.CompletedTrades
            .GroupBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Sum(trade => trade.NetProfit))
            .DefaultIfEmpty(0m)
            .Max();
        return topTickerNet <= 0m ? 0m : topTickerNet / totalNet * 100m;
    }

    private static void EvaluateOutOfSample(
        StrategyBacktestResult strategy,
        BacktestValidationReport validation,
        PromotionCriteria criteria,
        List<string> failed,
        List<string> passed)
    {
        if (!validation.OutOfSample.Enabled)
        {
            failed.Add("out_of_sample (not configured; enable validation.out_of_sample)");
            return;
        }

        var partition = validation.OutOfSample.StrategyPartitions
            .FirstOrDefault(item => item.StrategyId == strategy.StrategyId);
        if (partition is null)
        {
            failed.Add("out_of_sample (no partition recorded for strategy)");
            return;
        }

        if (criteria.RequirePositiveOutOfSample && partition.OutOfSample.TotalReturnPct <= 0m)
        {
            failed.Add($"out_of_sample_return ({partition.OutOfSample.TotalReturnPct:F2}% over {partition.OutOfSample.TradeCount} OOS trades)");
            return;
        }

        passed.Add($"out_of_sample ({partition.OutOfSample.TotalReturnPct:F2}% over {partition.OutOfSample.TradeCount} OOS trades)");
    }

    private static void EvaluateBenchmark(
        StrategyBacktestResult strategy,
        BacktestValidationReport validation,
        List<string> failed,
        List<string> passed)
    {
        if (!validation.Benchmark.Enabled)
        {
            failed.Add("benchmark (not configured; enable validation.benchmark)");
            return;
        }

        var comparison = validation.Benchmark.StrategyComparisons
            .FirstOrDefault(item => item.StrategyId == strategy.StrategyId);
        if (comparison?.ExcessReturnPct is not { } excess)
        {
            failed.Add("benchmark (no comparison recorded for strategy)");
            return;
        }

        if (excess <= 0m)
        {
            failed.Add($"benchmark_excess ({excess:F2}% vs {validation.Benchmark.Ticker} buy-and-hold)");
            return;
        }

        passed.Add($"benchmark_excess (+{excess:F2}% vs {validation.Benchmark.Ticker})");
    }

    private static void EvaluateWalkForward(
        StrategyBacktestResult strategy,
        BacktestValidationReport validation,
        PromotionCriteria criteria,
        List<string> failed,
        List<string> passed)
    {
        var windows = validation.WalkForwardWindows
            .SelectMany(window => window.StrategyResults)
            .Where(result => result.StrategyId == strategy.StrategyId)
            .ToArray();

        if (windows.Length < criteria.MinWalkForwardWindows)
        {
            failed.Add($"walk_forward_windows ({windows.Length} recorded, need >= {criteria.MinWalkForwardWindows})");
            return;
        }

        var positive = windows.Count(result => result.TotalReturnPct > 0m);
        var winRate = (decimal)positive / windows.Length * 100m;
        if (winRate < criteria.MinWalkForwardWinRatePct)
        {
            failed.Add($"walk_forward_consistency ({positive}/{windows.Length} windows positive = {winRate:F0}%, need >= {criteria.MinWalkForwardWinRatePct:F0}%)");
            return;
        }

        passed.Add($"walk_forward ({positive}/{windows.Length} windows positive)");
    }

    private static string FormatRatio(decimal ratio) =>
        ratio == decimal.MaxValue ? "inf" : ratio.ToString("F2");
}
