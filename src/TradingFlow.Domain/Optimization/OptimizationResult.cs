using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Domain.Optimization;

public sealed record OptimizationResult(
    string RunName,
    string BaseStrategyId,
    string Metric,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int TotalPermutations,
    IReadOnlyList<OptimizationRun> TopRuns);

public sealed record OptimizationRun(
    int Rank,
    IReadOnlyDictionary<string, object> ParameterValues,
    decimal MetricValue,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    decimal NetProfit,
    decimal MaxDrawdownPct,
    int WinningTradeCount,
    int LosingTradeCount,
    BacktestResult BacktestResult);

public sealed record OptimizationProgress(
    string Kind,
    int CurrentPermutation,
    int TotalPermutations,
    string StrategyName,
    IReadOnlyDictionary<string, object> ParameterValues,
    OptimizationRun? CompletedRun,
    IReadOnlyList<OptimizationRun> CurrentTopRuns,
    string Message);
