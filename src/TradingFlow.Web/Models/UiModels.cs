using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Models;

public sealed record StrategyOption(
    string Path,
    string FileName,
    StrategyDefinition Definition,
    StrategyAuditSummary? Audit);

public sealed record StrategyAuditSummary(
    decimal ReturnPct,
    decimal MaxDrawdownPct,
    int Trades,
    decimal WinRatePct,
    string AverageHold,
    string? ResultPath);

public sealed record RunConfigSummary(
    string Path,
    string FileName,
    BacktestRunConfig Config,
    IReadOnlyList<StrategyOption> Strategies);

public sealed record BacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
    Guid? WishlistId,
    string? WishlistName,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> StrategyPaths,
    decimal StartingCapital,
    decimal RiskPerTradePct,
    decimal MaxPositionValuePct,
    int MaxConcurrentPositions,
    string CachePolicy,
    IReadOnlyList<StrategyParameterOverride> StrategyOverrides);

public sealed record StrategyParameterOverride(
    string StrategyPath,
    decimal MinVolumeSpike,
    decimal MinEntryRsi,
    decimal MaxEntryRsi,
    decimal? MaxVwapExtensionAtr,
    bool ConfluenceEnabled,
    string ConfluenceTimeframe,
    int ConfluenceEmaPeriod,
    decimal StopAtrMultiple,
    decimal TargetRMultiple,
    decimal MaxHoldHours,
    bool EnableAtrTrailingStop,
    decimal TrailingStopAtrMultiple,
    decimal TrailingActivationR,
    int MinHoldBarsBeforeTechnicalExit,
    string ExecutionTimeframe,
    decimal SlippageBps);

public sealed record BacktestJobSnapshot(
    Guid JobId,
    string RunName,
    string ConfigPath,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    string CurrentStage,
    int CompletedTickerCount,
    int TotalTickerCount,
    IReadOnlyList<string> Events,
    BacktestResult? Result,
    IReadOnlyList<BacktestStrategyRunGroup>? StrategyGroups = null);

public sealed record BacktestStrategyRunGroup(
    string StrategyName,
    string Status,
    int CompletedTickerCount,
    int TotalTickerCount,
    string? CurrentTicker,
    IReadOnlyList<string> RecentEvents);

public sealed record PaperEnvironmentSnapshot(
    RunConfigSummary Config,
    bool AlpacaKeyIdPresent,
    bool AlpacaSecretKeyPresent,
    IReadOnlyDictionary<string, object?>? AlpacaReadOnlyCheck);

public sealed record OptimizationRunRequest(
    string BaseStrategyPath,
    string BacktestConfigPath,
    string RunName,
    string Metric,
    string TargetRMultipleCsv,
    string StopAtrMultipleCsv,
    string MinVolumeSpikeCsv);

public sealed record OptimizationJobSnapshot(
    Guid JobId,
    string RunName,
    string OptimizationConfigPath,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    string CurrentStage,
    IReadOnlyList<string> Events,
    OptimizationRunSnapshot? CurrentRun,
    IReadOnlyList<OptimizationRunSnapshot> CompletedRuns,
    TradingFlow.Domain.Optimization.OptimizationResult? Result);

public sealed record OptimizationRunSnapshot(
    int Permutation,
    int TotalPermutations,
    string StrategyName,
    IReadOnlyDictionary<string, object> ParameterValues,
    decimal? MetricValue,
    decimal? TotalReturnPct,
    decimal? AverageDailyReturnPct,
    decimal? NetProfit,
    decimal? MaxDrawdownPct,
    int? WinningTradeCount,
    int? LosingTradeCount,
    string Status);

