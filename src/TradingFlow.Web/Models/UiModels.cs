using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Models;

public sealed record StrategyOption(
    string Path,
    string FileName,
    StrategyDefinition Definition,
    StrategyArtifactIdentity Identity,
    StrategyLifecycleState Lifecycle,
    StrategyAuditSummary? Audit,
    StrategyArtifact Artifact);

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
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
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
    decimal SlippageBps,
    string? DerivedSemanticVersion = null,
    string Actor = "web-operator");

public sealed class PaperExperimentParameterInput
{
    public bool ApplyOverrides { get; set; }
    public decimal MinVolumeSpike { get; set; }
    public decimal MinEntryRsi { get; set; }
    public decimal MaxEntryRsi { get; set; }
    public decimal? MaxVwapExtensionAtr { get; set; }
    public bool ConfluenceEnabled { get; set; }
    public string ConfluenceTimeframe { get; set; } = String.Empty;
    public int ConfluenceEmaPeriod { get; set; }
    public decimal StopAtrMultiple { get; set; }
    public decimal TargetRMultiple { get; set; }
    public decimal MaxHoldHours { get; set; }
    public bool EnableAtrTrailingStop { get; set; }
    public decimal TrailingStopAtrMultiple { get; set; }
    public decimal TrailingActivationR { get; set; }
    public int MinHoldBarsBeforeTechnicalExit { get; set; }
    public string ExecutionTimeframe { get; set; } = String.Empty;
    public decimal SlippageBps { get; set; }

    public static PaperExperimentParameterInput From(StrategyDefinition definition) =>
        new()
        {
            MinVolumeSpike = definition.EntryRules.MinVolumeSpike,
            MinEntryRsi = definition.EntryRules.MinEntryRsi,
            MaxEntryRsi = definition.EntryRules.MaxEntryRsi,
            MaxVwapExtensionAtr = definition.EntryRules.MaxVwapExtensionAtr,
            ConfluenceEnabled = definition.Confluence.Enabled,
            ConfluenceTimeframe = definition.Confluence.Timeframe,
            ConfluenceEmaPeriod = definition.Confluence.EmaPeriod,
            StopAtrMultiple = definition.ExitRules.StopAtrMultiple,
            TargetRMultiple = definition.ExitRules.TargetRMultiple,
            MaxHoldHours = definition.ExitRules.MaxHoldHours,
            EnableAtrTrailingStop = definition.ExitRules.EnableAtrTrailingStop,
            TrailingStopAtrMultiple = definition.ExitRules.TrailingStopAtrMultiple,
            TrailingActivationR = definition.ExitRules.TrailingActivationR,
            MinHoldBarsBeforeTechnicalExit = definition.ExitRules.MinHoldBarsBeforeTechnicalExit,
            ExecutionTimeframe = definition.Execution.Timeframe,
            SlippageBps = definition.Execution.SlippageBps
        };

    public StrategyParameterOverride ToOverride(
        string strategyPath,
        string semanticVersion,
        string actor) =>
        new(
            strategyPath,
            MinVolumeSpike,
            MinEntryRsi,
            MaxEntryRsi,
            MaxVwapExtensionAtr,
            ConfluenceEnabled,
            ConfluenceTimeframe,
            ConfluenceEmaPeriod,
            StopAtrMultiple,
            TargetRMultiple,
            MaxHoldHours,
            EnableAtrTrailingStop,
            TrailingStopAtrMultiple,
            TrailingActivationR,
            MinHoldBarsBeforeTechnicalExit,
            ExecutionTimeframe,
            SlippageBps,
            semanticVersion,
            actor);
}

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
    IReadOnlyList<BacktestStrategyRunGroup>? StrategyGroups = null,
    DateTimeOffset? CancellationRequestedAt = null);

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

