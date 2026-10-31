namespace TradingFlow.Contracts.Operator;

public sealed record MobileCatalogResponse(
    IReadOnlyList<MobileRunConfigOption> BacktestConfigs,
    IReadOnlyList<MobileRunConfigOption> PaperConfigs,
    IReadOnlyList<MobileStrategyOption> Strategies,
    IReadOnlyList<MobileStrategyOption> PaperExperimentStrategies,
    IReadOnlyList<MobileStrategyOption> PaperShadowStrategies,
    IReadOnlyList<TradingFlow.Contracts.V1.DecisionContextEvidenceChoice>? EvidenceChoices = null,
    IReadOnlyList<BacktestExecutionReplayChoice>? ExecutionReplayChoices = null);

/// <summary>A server-supported research execution assumption; it does not authorize trading.</summary>
public sealed record BacktestExecutionReplayChoice(string PolicyId, string Name, string Horizon)
{
    /// <summary>Shows the declared fill assumption in the existing native picker.</summary>
    public override string ToString() => Name;
}

public sealed record MobileRunConfigOption(
    string Path,
    string FileName,
    string RunName,
    string Mode,
    string Provider,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Intervals,
    RunProfileSettings? Settings = null)
{
    /// <summary>Identifies the server profile's mode, provider and timeframes.</summary>
    public override string ToString()
    {
        var mode = string.IsNullOrWhiteSpace(Mode) ? "run" : Mode;
        var provider = string.IsNullOrWhiteSpace(Provider) ? "provider" : Provider;
        var intervals = Intervals.Count == 0 ? "auto" : string.Join(",", Intervals);
        return $"{mode} / {provider} / {intervals}";
    }
}

/// <summary>Server-resolved profile values; clients may edit choices but do not supply their own fallback defaults.</summary>
public sealed record RunProfileSettings(int LookbackDays, decimal StartingCapital, decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct, int MaxConcurrentPositions, string CachePolicy, string? ExecutionReplayPolicyId);

public sealed record MobileStrategyOption(
    string Path,
    string FileName,
    string ArtifactStrategyId,
    string SemanticVersion,
    string ContentSha256,
    string Lifecycle,
    string StrategyId,
    string StrategyName,
    int Version,
    string Direction,
    string Timeframe,
    string ExecutionTimeframe,
    string SetupType,
    bool UsesNews,
    decimal? LastAuditedReturnPct = null,
    decimal? LastAuditedMaxDrawdownPct = null,
    int? LastAuditedTrades = null,
    decimal? LastAuditedWinRatePct = null,
    string? LastAuditedAverageHold = null,
    string? LastAuditedResultPath = null,
    TradingFlow.Contracts.V1.StrategyReference? DerivedFromIdentity = null,
    StrategyAdmissionProfileResponse? AdmissionProfile = null,
    string Horizon = "swing")
{
    /// <summary>Describes a strategy without presenting an accumulated-period audit return as a monthly result.</summary>
    public override string ToString()
    {
        var audit = LastAuditedReturnPct is null
            ? "not audited"
            : $"monthly audit report required / {LastAuditedTrades} trades";
        return $"{StrategyName} {SemanticVersion} [{Lifecycle}] ({Timeframe}->{ExecutionTimeframe}) - {audit}";
    }
}

/// <summary>The registered strategy's server-owned admission identity and native execution policy.</summary>
public sealed record StrategyAdmissionProfileResponse(string ProfileId, string ProfileVersion,
    string ExecutionTimingPolicy, string RiskPolicy);

public sealed record MobileAutomationStartRequest(
    string ConfigPath,
    string StrategyPath,
    string Ticker,
    string RunName,
    string Source,
    string? SourcePackage,
    string? SourceTitle,
    string? SourceMessage,
    string EntryMode = "validate_strategy",
    bool AllowExtendedHoursTrading = false,
    string EntryOrderType = "limit");

public sealed record MobileAutomationSessionSnapshot(
    Guid SessionId,
    string RunName,
    string ConfigPath,
    string StrategyPath,
    string Ticker,
    string Source,
    string? SourcePackage,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? EntrySubmittedAt,
    DateTimeOffset? ExitSubmittedAt,
    string? ErrorMessage,
    string CurrentStage,
    string? EntryOrderId,
    decimal? EntryPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    int? ShareQuantity,
    decimal? LastObservedPrice,
    decimal? UnrealizedPl,
    string? ExitReason,
    IReadOnlyList<string> Events,
    string? SourceTitle,
    string? SourceMessage,
    bool ExitSafetyOrdersSubmitted = false,
    string? ExecutionConfigHash = null,
    string? ExecutionCodeVersion = null)
{
    public string ProgressText => $"{Ticker} {Status} - {CurrentStage}";
    public string LatestEvent => Events.LastOrDefault() ?? "No events yet.";
    public string ShortSessionId => SessionId.ToString("N")[..8];
    public string DisplayTitle => $"{Ticker} - {Source} #{ShortSessionId}";
    public string StrategyFileName => Path.GetFileName(StrategyPath);
    public string RunDetail => $"{RunName} | {Status} | {StrategyFileName}";
}
