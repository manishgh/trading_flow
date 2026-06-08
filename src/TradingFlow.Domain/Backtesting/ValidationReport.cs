namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestValidationReport(
    OutOfSampleValidation OutOfSample,
    IReadOnlyList<WalkForwardWindowResult> WalkForwardWindows,
    BenchmarkValidation Benchmark,
    DataQualityValidation DataQuality,
    BiasRiskValidation BiasRisk);

public sealed record OutOfSampleValidation(
    bool Enabled,
    decimal OutOfSamplePercent,
    DateTimeOffset? SplitTimestamp,
    IReadOnlyList<StrategyPartitionResult> StrategyPartitions);

public sealed record StrategyPartitionResult(
    string StrategyId,
    string StrategyName,
    string Source,
    TradePartitionResult InSample,
    TradePartitionResult OutOfSample);

public sealed record TradePartitionResult(
    int TradeCount,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal WinRatePct,
    decimal MaxDrawdownPct);

public sealed record WalkForwardWindowResult(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<StrategyWindowResult> StrategyResults);

public sealed record StrategyWindowResult(
    string StrategyId,
    string StrategyName,
    string Source,
    int TradeCount,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal MaxDrawdownPct);

public sealed record BenchmarkValidation(
    bool Enabled,
    string Ticker,
    decimal? BenchmarkReturnPct,
    IReadOnlyList<StrategyBenchmarkComparison> StrategyComparisons);

public sealed record StrategyBenchmarkComparison(
    string StrategyId,
    string StrategyName,
    string Source,
    decimal StrategyReturnPct,
    decimal? ExcessReturnPct);

public sealed record DataQualityValidation(
    int TotalBarCount,
    int DuplicateBarCount,
    int InvalidOhlcCount,
    int NegativeVolumeCount,
    int ZeroVolumeCount,
    IReadOnlyList<string> Warnings);

public sealed record BiasRiskValidation(
    string UniverseSource,
    DateOnly? UniverseAsOfDate,
    string PriceAdjustmentPolicy,
    bool SurvivorshipBiasWarning,
    bool CorporateActionAdjustmentWarning,
    IReadOnlyList<string> Warnings);
