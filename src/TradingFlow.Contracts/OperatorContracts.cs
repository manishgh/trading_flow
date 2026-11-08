namespace TradingFlow.Contracts.Operator;

public sealed record MobileCatalogResponse(
    IReadOnlyList<MobileRunConfigOption> BacktestConfigs,
    IReadOnlyList<MobileRunConfigOption> PaperConfigs,
    IReadOnlyList<MobileStrategyOption> Strategies,
    IReadOnlyList<MobileStrategyOption> PaperExperimentStrategies,
    IReadOnlyList<MobileStrategyOption> PaperShadowStrategies);

public sealed record MobileRunConfigOption(
    string Path,
    string FileName,
    string RunName,
    string Mode,
    string Provider,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Intervals)
{
    public override string ToString()
    {
        var mode = string.IsNullOrWhiteSpace(Mode) ? "run" : Mode;
        var provider = string.IsNullOrWhiteSpace(Provider) ? "provider" : Provider;
        var intervals = Intervals.Count == 0 ? "auto" : string.Join(",", Intervals);
        return $"{mode} / {provider} / {intervals}";
    }
}

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
    string? LastAuditedResultPath = null)
{
    public override string ToString()
    {
        var audit = LastAuditedReturnPct is null
            ? "not audited"
            : $"{LastAuditedReturnPct:0.##}% / DD {LastAuditedMaxDrawdownPct:0.##}% / {LastAuditedAverageHold}";
        return $"{StrategyName} {SemanticVersion} [{Lifecycle}] ({Timeframe}->{ExecutionTimeframe}) - {audit}";
    }
}

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
