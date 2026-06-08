namespace TradingFlow.Domain.Strategies;

public sealed record StrategyDefinition(
    string StrategyId,
    string StrategyName,
    string Source,
    int Version,
    string Timeframe,
    string Direction,
    EntryRules EntryRules,
    ConfluenceRules Confluence,
    ExitRules ExitRules,
    ExecutionRules Execution,
    SessionRules Session);

public sealed record EntryRules(
    string SetupType,
    decimal MinVolumeSpike,
    decimal MinEntryRsi,
    decimal MaxEntryRsi,
    string TrendFilter,
    string MacdFilter,
    bool RequirePriceAboveBollingerMiddle,
    bool RequireMacdHistogramPositive,
    bool RequirePriceAboveVwap,
    bool RequirePriceAboveEma20,
    bool RequirePriceAboveEma50,
    bool RequireEma20AboveEma50,
    decimal? MaxVwapExtensionAtr,
    int OpeningRangeMinutes,
    int RecentHighLookbackBars,
    int VolatilityContractionLookbackBars);

public sealed record ExitRules(
    decimal StopAtrMultiple,
    decimal TargetRMultiple,
    decimal MaxHoldHours,
    bool EnableAtrTrailingStop,
    decimal TrailingStopAtrMultiple,
    decimal TrailingActivationR,
    bool ExitOnCloseBelowEma20,
    bool ExitOnCloseBelowVwap,
    bool ExitOnMacdHistogramNegative,
    int MinHoldBarsBeforeTechnicalExit);

public sealed record ConfluenceRules(
    bool Enabled,
    string Timeframe,
    int EmaPeriod,
    string MacdFilter);

public sealed record ExecutionRules(
    string Timeframe,
    decimal SlippageBps);

public sealed record SessionRules(
    string ExchangeTimezone,
    int QuietMinutesAfterOpen,
    int CloseBufferMinutes,
    int FridayCloseBufferMinutes,
    bool IsContinuousMarket = false);
