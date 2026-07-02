namespace TradingFlow.Web.Models;

public sealed record MobileCatalogResponse(
    IReadOnlyList<MobileRunConfigOption> BacktestConfigs,
    IReadOnlyList<MobileRunConfigOption> PaperConfigs,
    IReadOnlyList<MobileStrategyOption> Strategies);

public sealed record MobileRunConfigOption(
    string Path,
    string FileName,
    string RunName,
    string Mode,
    string Provider,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> Intervals);

public sealed record MobileStrategyOption(
    string Path,
    string FileName,
    string StrategyId,
    string StrategyName,
    int Version,
    string Direction,
    string Timeframe,
    string ExecutionTimeframe,
    string SetupType,
    bool UsesNews);

public sealed record MobilePaperRunRequest(
    string BaseConfigPath,
    string StrategyPath,
    string RunName,
    IReadOnlyList<string> Tickers,
    string? ScreenerFilter,
    bool ExtendedHours,
    bool NewsEnabled,
    string OrderExpiration,
    string EntryOrderType,
    Guid? WishlistId);

public sealed record MobileBacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> StrategyPaths,
    decimal StartingCapital,
    decimal RiskPerTradePct,
    decimal MaxPositionValuePct,
    int MaxConcurrentPositions,
    string CachePolicy);

public sealed record MobileNotificationItem(
    DateTimeOffset Timestamp,
    string Severity,
    string Title,
    string Message,
    string? RunName,
    Guid? JobId);

public sealed record MobileNewsFeedResponse(
    bool Enabled,
    string Provider,
    string? Message,
    IReadOnlyList<MobileNewsItem> Items);

public sealed record MobileNewsItem(
    string Ticker,
    DateTimeOffset Timestamp,
    string Headline,
    decimal SentimentScore,
    string? Provider,
    string? Source,
    string? Url,
    string? Summary);

public sealed record MobileAutomationStartRequest(
    string ConfigPath,
    string StrategyPath,
    string Ticker,
    string RunName,
    string Source,
    string? SourcePackage,
    string? SourceTitle,
    string? SourceMessage,
    string EntryMode = "validate_strategy");

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
    string? SourceMessage);

public sealed record MobileWishlistResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsDefault,
    bool IncludeExtendedHours,
    bool IsObserved,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MobileWishlistItemResponse> Items);

public sealed record MobileWishlistItemResponse(
    Guid Id,
    Guid WishlistId,
    string Ticker,
    string? DisplayName,
    string? Notes,
    bool Active,
    DateTimeOffset AddedAtUtc);

public sealed record MobileWishlistSignalResponse(
    Guid Id,
    Guid WishlistId,
    string Ticker,
    string SignalType,
    string Severity,
    DateTimeOffset DetectedAtUtc,
    decimal Price,
    string Reason,
    string SnapshotJson,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider,
    bool Acknowledged);

public sealed record MobileWishlistSaveRequest(
    Guid? Id,
    string Name,
    string? Description,
    bool? IsDefault,
    bool? IncludeExtendedHours,
    bool? IsObserved);

public sealed record MobileWishlistObserveRequest(bool IsObserved);

public sealed record MobileWishlistItemRequest(
    string Ticker,
    string? DisplayName,
    string? Notes);


public sealed record MobileWishlistMonitorSnapshotRequest(
    string Ticker,
    DateTimeOffset? Timestamp,
    string? Timeframe,
    decimal CurrentPrice,
    decimal CurrentVolume,
    decimal? Vwap,
    decimal? Atr,
    decimal? Ema10,
    decimal? Ema20,
    decimal? MacdHistogram,
    decimal? PreviousMacdHistogram,
    decimal? PreviousVolume,
    decimal? SessionRelativeVolume,
    decimal? RecentHigh,
    decimal? SessionOpen,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider,
    decimal? NewsSentiment);

public sealed record MobileWishlistMonitorRequest(
    IReadOnlyList<MobileWishlistMonitorSnapshotRequest> Snapshots);

public sealed record MobileWishlistMonitorResponse(
    IReadOnlyList<MobileWishlistMonitorEvaluationResponse> Evaluations,
    IReadOnlyList<MobileWishlistSignalResponse> PersistedSignals);

public sealed record MobileWishlistMonitorEvaluationResponse(
    string Ticker,
    bool ShouldAlert,
    string SignalType,
    string Severity,
    decimal Price,
    string Reason,
    decimal Score,
    decimal? SessionGainPct,
    decimal? SessionRelativeVolume,
    decimal? VwapExtensionAtr,
    string? NewsHeadline,
    string? NewsUrl,
    string? NewsProvider);
