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
    bool UsesNews,
    decimal? LastAuditedReturnPct,
    decimal? LastAuditedMaxDrawdownPct,
    int? LastAuditedTrades,
    decimal? LastAuditedWinRatePct,
    string? LastAuditedAverageHold,
    string? LastAuditedResultPath);

public sealed record MobilePaperRunRequest(
    string BaseConfigPath,
    string StrategyPath,
    string RunName,
    IReadOnlyList<string> Tickers,
    string? ScreenerFilter,
    bool AllowExtendedHoursTrading,
    bool NewsEnabled,
    string OrderExpiration,
    string EntryOrderType,
    Guid? WishlistId);

public sealed record MobileBacktestRunRequest(
    string BaseConfigPath,
    string RunName,
    int LookbackDays,
    Guid? WishlistId,
    IReadOnlyList<string> StrategyPaths,
    decimal StartingCapital,
    decimal AccountRiskBudgetPct,
    decimal MaxPositionNotionalPct,
    int MaxConcurrentPositions,
    string CachePolicy);

public sealed record MobileNotificationItem(
    DateTimeOffset Timestamp,
    string Severity,
    string Title,
    string Message,
    string? RunName,
    Guid? JobId);

public sealed record MobilePaperPositionResponse(
    string Ticker,
    string Side,
    decimal Qty,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPl);

public sealed record MobileRunningTrade(
    string Source,
    string Ticker,
    decimal Quantity,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPl,
    decimal UnrealizedPlPct,
    string Status,
    string Reference,
    Guid? JobId,
    Guid? SessionId,
    string CloseKind,
    string StrategyName = "",
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null,
    string? ExitReason = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string ProtectionSummary = "");

public sealed record MobileRunningTradesResponse(
    IReadOnlyList<MobileRunningTrade> Trades,
    decimal TotalUnrealizedPl,
    int Count);

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
    string EntryMode = "validate_strategy",
    bool AllowExtendedHoursTrading = false,
    string EntryOrderType = "market");

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

public sealed record MobileWishlistDeskResponse(
    MobileWishlistResponse Wishlist,
    IReadOnlyList<MobileWishlistDeskRowResponse> Rows,
    IReadOnlyList<MobileWishlistSignalResponse> RecentSignals,
    IReadOnlyList<MobileNewsItem> RelatedNews,
    IReadOnlyList<MobileRunningTrade> RunningTrades,
    decimal TotalUnrealizedPl);

public sealed record MobileWishlistDeskRowResponse(
    MobileWishlistItemResponse Item,
    string Ticker,
    string DisplayName,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? MidPrice,
    string DisplayBid,
    string DisplayAsk,
    string DisplayPrice,
    string BuyCaption,
    string SellCaption,
    DateTimeOffset? QuoteTimestamp,
    bool HasQuote,
    bool HasTrade,
    bool HasSignal,
    bool HasNews,
    string EligibilityLabel,
    string EligibilityReason,
    MobileWishlistSignalResponse? LatestSignal,
    MobileNewsItem? LatestNews,
    MobileRunningTrade? Trade);

public sealed record MobileSymbolIntelligenceResponse(
    string Ticker,
    DateTimeOffset FetchedAtUtc,
    MobileTradingFlowDecisionResponse TradingFlowDecision,
    MobileModelIntelligenceResponse ModelIntelligence);

public sealed record MobileTradingFlowDecisionResponse(
    string EligibilityLabel,
    string EligibilityReason,
    bool HasOpenTrade,
    MobileRunningTrade? Trade,
    MobileWishlistSignalResponse? LatestSignal,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? MidPrice,
    DateTimeOffset? QuoteTimestamp);

public sealed record MobileModelIntelligenceResponse(
    string ContractVersion,
    string AvailabilityStatus,
    string? AvailabilityReason,
    bool IsValidPromotedEvidence,
    string Mode,
    string RequestedHorizon,
    string ResolvedHorizon,
    string FinalSignal,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? RequestId,
    string? SnapshotId,
    DateTimeOffset? GeneratedAtUtc,
    string? ModelStatus,
    string? ModelType,
    string? ModelSchemaVersion,
    string? ModelTarget,
    string? ModelArtifactSha256,
    string? ModelTrainingDataEnd,
    MobileSwingIntelligenceResponse? Swing,
    MobileIntradayIntelligenceResponse? Intraday);

public sealed record MobileSwingIntelligenceResponse(
    decimal? Probability,
    decimal? DecisionScore,
    string Signal,
    int? Rank,
    decimal? Return1D,
    decimal? VolumeZ20,
    MobileCatalystIntelligenceResponse Catalyst,
    decimal GlobalContextImpact,
    IReadOnlyList<string> ActiveFlashpoints,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? LatestPriceDate,
    string PriceFeed);

public sealed record MobileIntradayIntelligenceResponse(
    decimal? OpportunityProbability,
    decimal? DownsideProbability,
    decimal? DecisionScore,
    string Signal,
    int? Rank,
    decimal? RelativeVolume,
    decimal? Rsi14,
    decimal? MacdSignalDiff,
    decimal? EntryStopPct,
    decimal? EntryTargetPct,
    MobileCatalystIntelligenceResponse Catalyst,
    string ReadinessStatus,
    IReadOnlyList<string> ReadinessReasons,
    string? LatestPriceDate,
    string PriceFeed);

public sealed record MobileCatalystIntelligenceResponse(
    string Status,
    string Direction,
    decimal Score,
    int EventCount,
    decimal Relevance,
    decimal? MinutesSinceLatest,
    IReadOnlyList<string> Reasons);

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

public sealed record MobileOrderPreviewRequest(
    string Ticker,
    decimal Quantity,
    decimal LimitPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    string Horizon,
    bool AllowExtendedHoursTrading);

public sealed record MobileOrderConfirmRequest(string TicketToken);


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
