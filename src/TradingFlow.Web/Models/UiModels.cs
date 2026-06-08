using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Models;

public sealed record StrategyOption(
    string Path,
    string FileName,
    StrategyDefinition Definition);

public sealed record RunConfigSummary(
    string Path,
    string FileName,
    BacktestRunConfig Config,
    IReadOnlyList<StrategyOption> Strategies);

public sealed record BacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
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
    BacktestResult? Result);

public sealed record PaperEnvironmentSnapshot(
    RunConfigSummary Config,
    bool EtoroApiKeyPresent,
    bool EtoroUserKeyPresent,
    IReadOnlyDictionary<string, object?>? EtoroReadOnlyCheck,
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
    TradingFlow.Domain.Optimization.OptimizationResult? Result);
