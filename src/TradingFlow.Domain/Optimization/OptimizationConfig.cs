namespace TradingFlow.Domain.Optimization;

public sealed record OptimizationConfig(
    string RunName,
    string BaseStrategyPath,
    string BacktestConfigPath,
    string Metric,
    int TopNResults,
    IReadOnlyDictionary<string, IReadOnlyList<object>> Parameters);
