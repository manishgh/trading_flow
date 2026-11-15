using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.Optimization;

public sealed record OptimizationResult(
    string RunName,
    string BaseStrategyId,
    string Metric,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int TotalPermutations,
    IReadOnlyList<OptimizationRun> TopRuns,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OptimizationPermutationEvidence>? FrozenPermutationEvidence = null);

public sealed record OptimizationPermutationEvidence(string VariantKey, decimal MetricValue, string ResultPath, string ResultSha256,
    ResearchPublicationEvidence Publication);

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
