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
    SessionRules Session,
    RegimeRules? Regime = null);

/// <summary>
/// Layer-2 market regime gate (docs/strategy-design-doctrine.md §2). A strategy only takes new
/// entries on days the benchmark passes the rule (e.g. SPY above its 50/200-day SMA). Evaluated
/// no-lookahead: a day's regime uses only benchmark bars dated before that day. Null/inactive
/// means the strategy is always eligible, preserving legacy behavior.
/// </summary>
public sealed record RegimeRules(
    string BenchmarkSymbol,
    string Rule,
    int SmaPeriod)
{
    public const string PriceAboveSma = "price_above_sma";
    public const string Off = "off";

    public bool IsActive =>
        !string.IsNullOrWhiteSpace(BenchmarkSymbol) &&
        Rule.Equals(PriceAboveSma, StringComparison.OrdinalIgnoreCase) &&
        SmaPeriod > 0;
}

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
    int VolatilityContractionLookbackBars,
    decimal OpeningRangeBreakBuffer = 0m,
    bool RequireLogPriceRising = false,
    bool RequireLogVolumeRising = false,
    int LogPriceLookbackBars = 12,
    int LogVolumeLookbackBars = 12,
    decimal MinLogPriceSlope = 0m,
    decimal MinLogVolumeSlope = 0m,
    decimal? MinLogPriceR2 = null,
    decimal? MinLogVolumeR2 = null,
    bool RejectFallingPriceRisingVolume = false,
    decimal RedVolumeMaxPriceSlope = -0.0001m,
    decimal RedVolumeMinVolumeSlope = 0.003m,
    int VwapHoldBars = 2,
    int MaxEntriesPerTickerPerDay = 0,
    decimal? MinCloseLocationValue = null,
    bool RejectWeakCloseOnHighRelativeVolume = false,
    decimal WeakCloseMaxLocationValue = 0.40m,
    decimal WeakCloseMinRelativeVolume = 1.0m,
    decimal? MinDayGainPct = null,
    decimal? MinSessionGainPct = null,
    decimal? MaxPreEntrySessionRangePct = null,
    decimal? MaxEntryPullbackFromSessionHighPct = null,
    bool RequirePositiveNews = false,
    decimal? MinNewsSentiment = null,
    decimal? VetoNewsSentimentBelow = null,
    decimal MaxNewsAgeHours = 72m,
    decimal? MinCatalystPriceMovePct = null,
    decimal? MaxCatalystPriceMovePct = null,
    bool EnableShort = false,
    string ShortSetupType = "catalyst_vwap_breakdown",
    decimal? MaxShortNewsSentiment = null,
    decimal? MinShortCatalystDropPct = null,
    decimal? MaxShortEntryRsi = null,
    decimal? MinShortEntryRsi = null,
    decimal? MaxShortCloseLocationValue = null,
    bool RequirePriceBelowVwapForShort = true,
    bool RequireMacdBearishForShort = true,
    decimal? MinBullFlagPoleMovePct = null,
    int BullFlagPoleMaxBars = 10,
    int BullFlagPullbackMinBars = 2,
    int BullFlagPullbackMaxBars = 5,
    decimal BullFlagMaxDepthPctOfPole = 50m,
    decimal? BullFlagPullbackVolumeRatioMax = 0.70m,
    decimal? BullFlagBreakoutVolumeRatioMin = 1.50m,
    bool EnablePremarketFilter = false,
    bool RequirePremarketHighBreak = false,
    decimal PremarketHighBreakBufferPct = 0.25m,
    decimal? MaxPremarketVwapExtensionPct = null,
    decimal? MaxPremarketRunPct = null,
    decimal? MaxOpeningRangePct = null,
    bool RejectOpeningExhaustion = false,
    int OpeningExhaustionMinutes = 20,
    decimal? OpeningExhaustionMaxDayGainPct = null,
    decimal? OpeningExhaustionMaxSessionRangePct = null,
    decimal OpeningExhaustionMinPullbackFromHighPct = 0.50m,
    int MinConsecutiveClosesAboveVwap = 0,
    bool EnableEntryBarConfirmation = false,
    decimal MinEntryBarCloseLocationValue = 0.55m,
    bool RejectEntryBarCloseLocationBelowMinimum = false,
    bool RejectEntryBarBreaksSignalMidpoint = false,
    decimal? MaxVwapExtensionPctForDirectEntry = null,
    decimal? ExtendedVwapMinEntryBarCloseLocationValue = null,
    decimal? MaxBollingerPositionForDirectEntry = null,
    decimal? ExtendedBollingerMinEntryBarCloseLocationValue = null,
    bool RequirePriceAboveSma10 = false,
    bool RequirePriceAboveSma20 = false,
    bool RequirePriceAboveSma50 = false,
    bool RequireSma10AboveSma20 = false,
    bool RequireSma20AboveSma50 = false,
    string AnchoredVwapMode = "none",
    int AnchoredVwapLookbackBars = 20,
    bool RequirePriceAboveAnchoredVwap = false,
    bool RequirePriceBelowAnchoredVwapForShort = false,
    decimal? MaxAnchoredVwapExtensionAtr = null,
    string ShortAnchoredVwapMode = "none",
    int ShortAnchoredVwapLookbackBars = 20,
    bool RequirePriceBelowShortAnchoredVwap = false,
    decimal? MaxShortAnchoredVwapExtensionAtr = null,
    int StepPriorMoveLookbackBars = 60,
    int StepConsolidationMinBars = 10,
    int StepConsolidationMaxBars = 40,
    decimal MinStepPriorMovePct = 30m,
    decimal MinStepPriorDeclinePct = 20m,
    decimal MaxStepBaseDepthPct = 35m,
    decimal? MaxStepBaseVolumeRatio = null,
    decimal? MinStepBreakoutVolumeRatio = null,
    bool RequireStepHigherLows = false,
    bool RequireStepLowerHighsForShort = false,
    bool RequireStepBollingerContraction = false,
    decimal StepBollingerWidthRatioMax = 0.85m,
    int ReclaimLookbackBars = 20,
    decimal MinReclaimPullbackDepthPct = 8m,
    decimal MaxReclaimPullbackDepthPct = 35m,
    bool RequireReclaimLowAboveSma50 = true,
    bool RequireReclaimCloseAboveSma10 = true,
    bool RequireReclaimCloseAboveSma20 = true,
    int RolloverLookbackBars = 20,
    decimal MinRolloverAdvancePct = 12m,
    decimal MinRolloverDropFromHighPct = 3m,
    decimal MaxRolloverDropFromHighPct = 25m,
    bool RequireRolloverCloseBelowSma10 = true,
    bool RequireRolloverCloseBelowSma20 = true,
    bool RequireRolloverCloseBelowSma50 = false,
    int RolloverConsecutiveLowerCloseBars = 1,
    bool RequireRolloverCloseBelowPriorLow = false,
    bool RequirePriceAboveEma10 = false,
    bool RequireEma10AboveEma20 = false,
    decimal? MinSessionRelativeVolume = null,
    decimal? MinGapUpPct = null,
    string? AnchorType = null,
    decimal? AvwapProximityPct = null,
    bool RequirePullbackVolumeDryup = false,
    bool RequirePriceAboveSma50Daily = false,
    bool RequirePriceAboveSma200Daily = false,
    bool RequirePriceAboveSma150 = false,
    bool RequirePriceAboveSma200 = false,
    bool RequireSma50AboveSma150 = false,
    bool RequireSma150AboveSma200 = false,
    decimal? MinPriceVs52WeekLowPct = null,
    decimal? MaxPriceVs52WeekHighPct = null,
    int? MinContractions = null,
    int? MaxContractions = null,
    bool RequireVolatilityHalvingLeftToRight = false,
    bool RequireVolumeDryUpPreBreakout = false,
    decimal? MinBreakoutVolumeRatio = null,
    bool RequirePriceAboveEma5_65m = false,
    decimal? MinBounceVolumeRatio = null,
    int? RequirePriorFlushBelowVwapBars = null,
    int? VwapReclaimMaxBarsSinceFlush = null,
    decimal? MinReclaimVolumeRatio = null,
    bool RequireVolumeSmaRising = false,
    int VolumeSmaPeriod = 5,
    int VolumeSmaRisingLookbackBars = 3,
    decimal? MinVolumeSmaRisePct = null,
    bool EnablePerTickerDailyLossGuard = false,
    int MaxPerTickerDailyFailedTrades = 0,
    decimal? MaxPerTickerDailyLossR = null,
    decimal? MaxPerTickerDailyLossPctOfAccount = null,
    string MinVolumeSpikeSource = "cumulative_same_time",
    string VolumeConfirmationMode = "hard_gate",
    decimal? MinVolumeLiquidityFloor = null,
    int? MaxCatalystConfirmationBars = null,
    decimal? GapVariantMinPct = null,
    decimal? MinAdx = null,
    bool RequireAdxRising = false,
    int AdxRisingLookbackBars = 3,
    bool RequireObvRising = false,
    int ObvRisingLookbackBars = 3,
    decimal? MinObvChange = null,
    decimal? MaxMacdHistogram = null,
    int PriorEntryGainLookbackBars = 5,
    decimal? MaxPriorEntryGainPct = null,
    bool RequirePriorInsideDay = false,
    bool RequirePriorNr7 = false,
    string PriorCompressionMode = "none",
    int PriorNr7LookbackDays = 7,
    decimal? MinVwapDistanceAtrForDivergence = null,
    int DivergenceLookbackBars = 20,
    int DivergenceStartHour = 12,
    // Archetype C mean-reversion (doctrine §6C): a stretch is any of >=N consecutive down closes,
    // RSI(2) below MaxReversionRsi2, or a close below the lower Bollinger band, occurring within the
    // lookback before today's reclaim of the prior-day high. VetoFreshNewsHours vetoes the entry when
    // ANY catalyst is fresher than that many hours (Chan's no-news condition — the reversion edge).
    int ReversionStretchLookbackBars = 5,
    int MinConsecutiveDownClosesForStretch = 0,
    decimal? MaxReversionRsi2 = null,
    bool EnableLowerBollingerStretch = false,
    decimal? VetoFreshNewsHours = null,
    int VcpPivotStrengthBars = 2,
    decimal VcpMaximumDepthRatioToPrevious = 0.90m,
    decimal VcpMinimumLowRisePct = 0m,
    decimal VcpMaximumContractionToAdvanceVolumeRatio = 0.70m,
    bool VcpRequireProgressiveContractionVolume = true,
    decimal VcpMaximumVolumeRatioToPreviousContraction = 1m,
    int VcpMinimumVolumeReferenceBars = 1,
    decimal VcpBreakoutBufferPct = 0m,
    string PortfolioRankMode = "none");

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
    int MinHoldBarsBeforeTechnicalExit,
    bool ExitOnLogPriceFade = false,
    int ExitLogPriceLookbackBars = 6,
    int ExitLogVolumeLookbackBars = 6,
    decimal MaxExitLogPriceSlope = -0.0001m,
    decimal MinExitLogVolumeSlope = 0.003m,
    bool RequireRisingVolumeForLogFadeExit = false,
    bool RequireBelowVwapForLogFadeExit = false,
    bool EnableConfirmedVwapExit = false,
    int ConfirmedVwapExitBars = 2,
    decimal ConfirmedVwapExitAtrBuffer = 0m,
    decimal? DisableConfirmedVwapExitAfterR = null,
    bool ExitOnSma10NearSma20 = false,
    decimal Sma10NearSma20Pct = 0.25m,
    bool ExitOnSma10CrossBelowSma20 = false,
    bool ExitShortOnSma10CrossAboveSma20 = false,
    bool ExitOnEma10CrossBelowEma20 = false,
    bool AllowSameBarStopTarget = true,
    string InitialStopMode = "atr",
    string ProfitTargetMode = "r_multiple",
    bool EnableFailedBreakoutCircuitBreaker = false,
    int FailedBreakoutBars = 3,
    decimal FailedBreakoutMinR = 0m,
    decimal StopTickBuffer = 0.01m,
    // Doctrine §6A L5: require two consecutive closes below EMA20 before the EMA20 loss exit fires,
    // instead of a single-touch exit that shakes out trend riders on one-bar dips. Default false
    // preserves the existing single-close behaviour for every other strategy.
    bool RequireConfirmedEma20Exit = false,
    int? MaxHoldBars = null,
    decimal? ExitOnRsi2Above = null);

public sealed record ConfluenceRules(
    bool Enabled,
    string Timeframe,
    int EmaPeriod,
    string MacdFilter);


public sealed record ExecutionRules(
    string Timeframe,
    decimal SlippageBps,
    ExecutionConfirmationRules? Confirmation = null)
{
    public ExecutionConfirmationRules EffectiveConfirmation =>
        Confirmation ?? ExecutionConfirmationRules.Disabled;
}

/// <summary>
/// Defines the completed execution-timeframe evidence required after a setup
/// becomes available. A confirming bar is evidence only; backtests fill on the
/// next eligible bar and live trading can route only after this bar has closed.
/// </summary>
public sealed record ExecutionConfirmationRules(
    bool Enabled,
    int MaxBarsAfterSetup,
    string PriceFilter,
    string TrendFilter,
    string MomentumFilter,
    decimal? MinCloseLocationValue = null,
    decimal? MaxCloseLocationValue = null)
{
    public static ExecutionConfirmationRules Disabled { get; } =
        new(false, 0, "none", "none", "none");

    public const string NoFilter = "none";
    public const string CloseAboveSetupClose = "close_above_setup_close";
    public const string CloseBelowSetupClose = "close_below_setup_close";
    public const string CloseAbovePreviousHigh = "close_above_previous_high";
    public const string CloseBelowPreviousLow = "close_below_previous_low";
    public const string Ema10AboveEma20 = "ema10_above_ema20";
    public const string Ema10BelowEma20 = "ema10_below_ema20";
    public const string MacdHistogramPositive = "macd_histogram_positive";
    public const string MacdHistogramNegative = "macd_histogram_negative";
}

public sealed record SessionRules(
    string ExchangeTimezone,
    int QuietMinutesAfterOpen,
    int CloseBufferMinutes,
    int FridayCloseBufferMinutes,
    bool IsContinuousMarket = false,
    bool UseExtendedHours = false);
