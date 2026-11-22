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
/// Market-regime gate. A strategy only takes new
/// entries on days the benchmark passes the rule (e.g. SPY above its 50/200-day SMA). Evaluated
/// no-lookahead: a day's regime uses only benchmark bars dated before that day. Null/inactive
/// means the strategy is always eligible, preserving legacy behavior.
/// </summary>
public sealed record RegimeRules(
    string BenchmarkSymbol,
    string Rule,
    int SmaPeriod,
    // 0 = off. Otherwise the average must also be higher than it was this many sessions earlier (a rising average).
    // Omitted from serialized definitions when off, so existing strategies keep their recorded content.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int SmaRisingLookbackBars = 0,
    // Home-market fund for Nasdaq-listed stocks (option A of the 18 Nov 2026 design: "QQQ"). When set, a stock listed on
    // Nasdaq also needs this fund to pass the same rule; other listings need only the benchmark. Omitted when unset.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NasdaqListingBenchmark = null,
    // VIX sizing (checkpoint 3 of the 18 Nov 2026 design), long strategies only. The VIX close of the session before the
    // entry (the same session whose benchmark close the regime rule reads) decides: below VixFullRiskBelow full per-trade
    // risk; from VixFullRiskBelow to VixNoLongsAbove (both included) the per-trade risk times VixReducedRiskScale; above
    // VixNoLongsAbove no new entry. All four are set together or not at all; omitted when unset so other strategies keep
    // their recorded content.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? VixSeries = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VixFullRiskBelow = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VixNoLongsAbove = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VixReducedRiskScale = null,
    // Bearish regime (Checkpoint 7 of the 19 Nov 2026 design): 0 = off. Otherwise the average must also be lower than it
    // was this many sessions earlier (a falling average) alongside Close < SMA. Omitted when off so existing hashes stay exact.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int SmaFallingLookbackBars = 0,
    // Short VIX sizing (Checkpoint 7, direction: short only): below VixNoShortsBelow no new short entry; between
    // VixNoShortsBelow and VixShortFullRiskAbove (inclusive) per-trade risk times VixReducedRiskScale; above
    // VixShortFullRiskAbove full per-trade risk. Omitted when unset so existing strategies keep their content hashes.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VixNoShortsBelow = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VixShortFullRiskAbove = null)
{
    /// <summary>Alpaca's exchange code for a Nasdaq listing, as in its asset records and the security master.</summary>
    public const string NasdaqExchange = "NASDAQ";

    /// <summary>The only VIX series supported: FRED's daily CBOE Volatility Index close.</summary>
    public const string FredVixSeries = "VIXCLS";

    public const string PriceAboveSma = "price_above_sma";
    public const string PriceBelowFallingSma = "price_below_falling_sma";
    public const string Off = "off";

    /// <summary>True when this regime rule uses the reversed bearish rule (<see cref="PriceBelowFallingSma"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBearishRule => Rule.Equals(PriceBelowFallingSma, StringComparison.OrdinalIgnoreCase);

    /// <summary>The active SMA slope lookback sessions (falling lookback for bearish rules, rising lookback for bullish rules).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int SlopeLookbackBars => IsBearishRule ? SmaFallingLookbackBars : SmaRisingLookbackBars;

    /// <summary>True when long VIX sizing thresholds are configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesLongVix => !String.IsNullOrWhiteSpace(VixSeries) && VixFullRiskBelow is not null && VixNoLongsAbove is not null;

    /// <summary>True when short VIX sizing thresholds are configured.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesShortVix => !String.IsNullOrWhiteSpace(VixSeries) && VixNoShortsBelow is not null && VixShortFullRiskAbove is not null;

    /// <summary>True when the previous session's VIX close also sizes or refuses new entries.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesVix => !String.IsNullOrWhiteSpace(VixSeries);

    /// <summary>True when the stock's own listing market also has to pass the rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesNasdaqHomeMarket => !String.IsNullOrWhiteSpace(NasdaqListingBenchmark);

    /// <summary>Completed daily closes one decision needs: the average's window plus the earlier average it is compared with.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RequiredCloses => SmaPeriod + Math.Max(0, SlopeLookbackBars);

    /// <summary>
    /// The rule part of a regime evidence key ("SPY:50", or "SPY:50:rising20" with a rising average, or
    /// "SPY:50:below:falling20" for a bearish falling average), so two strategies with different rules on the same fund
    /// never share a recorded result. Keys of existing bullish rules are unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EvidenceRule =>
        (IsBearishRule
            ? (SmaFallingLookbackBars > 0
                ? $"{BenchmarkSymbol}:{SmaPeriod}:below:falling{SmaFallingLookbackBars}"
                : $"{BenchmarkSymbol}:{SmaPeriod}:below")
            : (SmaRisingLookbackBars > 0
                ? $"{BenchmarkSymbol}:{SmaPeriod}:rising{SmaRisingLookbackBars}"
                : $"{BenchmarkSymbol}:{SmaPeriod}")) +
        (UsesNasdaqHomeMarket ? $":nasdaq{NasdaqListingBenchmark}" : "") +
        (UsesShortVix
            ? FormattableString.Invariant($":vixshort{VixSeries}:{VixNoShortsBelow}:{VixShortFullRiskAbove}:{VixReducedRiskScale}")
            : UsesVix
                ? FormattableString.Invariant($":vix{VixSeries}:{VixFullRiskBelow}:{VixNoLongsAbove}:{VixReducedRiskScale}")
                : "");

    public bool IsActive =>
        !string.IsNullOrWhiteSpace(BenchmarkSymbol) &&
        (Rule.Equals(PriceAboveSma, StringComparison.OrdinalIgnoreCase) || IsBearishRule) &&
        SmaPeriod > 0;

    /// <summary>
    /// Checks all regime invariants (rule name, rising/falling SMA look-back, home-market symbol, and long/short VIX
    /// sizing completeness, bounds, mutual exclusion, and direction compatibility). Throws
    /// <see cref="InvalidOperationException"/> with a concrete explanation when any setting is inconsistent.
    /// </summary>
    public void Validate(string? direction = null)
    {
        if (!String.IsNullOrWhiteSpace(BenchmarkSymbol) &&
            !String.IsNullOrWhiteSpace(Rule) &&
            !Rule.Equals(PriceAboveSma, StringComparison.OrdinalIgnoreCase) &&
            !Rule.Equals(PriceBelowFallingSma, StringComparison.OrdinalIgnoreCase) &&
            !Rule.Equals(Off, StringComparison.OrdinalIgnoreCase) &&
            !Rule.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported regime.rule '{Rule}'; expected {PriceAboveSma}, {PriceBelowFallingSma}, {Off}, or none.");
        if (SmaRisingLookbackBars < 0)
            throw new InvalidOperationException("regime.sma_rising_lookback_bars must be zero or positive.");
        if (SmaFallingLookbackBars < 0)
            throw new InvalidOperationException("regime.sma_falling_lookback_bars must be zero or positive.");
        if (IsBearishRule && SmaRisingLookbackBars > 0)
            throw new InvalidOperationException("regime.sma_rising_lookback_bars cannot be used with regime.rule price_below_falling_sma; use regime.sma_falling_lookback_bars.");
        if (!IsBearishRule && SmaFallingLookbackBars > 0)
            throw new InvalidOperationException("regime.sma_falling_lookback_bars requires regime.rule price_below_falling_sma.");
        if (NasdaqListingBenchmark is not null &&
            (String.IsNullOrWhiteSpace(NasdaqListingBenchmark) ||
             NasdaqListingBenchmark.Trim().Equals(BenchmarkSymbol?.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("regime.nasdaq_listing_benchmark must name a fund other than regime.benchmark, for example QQQ.");

        var hasSeries = !String.IsNullOrWhiteSpace(VixSeries);
        var hasLongVix = VixFullRiskBelow is not null || VixNoLongsAbove is not null;
        var hasShortVix = VixNoShortsBelow is not null || VixShortFullRiskAbove is not null;
        var hasAnyVix = hasSeries || hasLongVix || hasShortVix || VixReducedRiskScale is not null;
        if (!hasAnyVix) return;

        if (hasLongVix && hasShortVix)
            throw new InvalidOperationException("Cannot mix long VIX sizing (vix_full_risk_below, vix_no_new_longs_above) with short VIX sizing (vix_no_new_shorts_below, vix_short_full_risk_above).");

        if (!hasSeries || VixReducedRiskScale is null)
            throw new InvalidOperationException("VIX sizing requires regime.vix_series and regime.vix_reduced_risk_scale alongside either long or short VIX thresholds.");

        var normalizedSeries = VixSeries!.Trim().ToUpperInvariant();
        if (normalizedSeries != FredVixSeries)
            throw new InvalidOperationException($"regime.vix_series must be {FredVixSeries} (FRED's daily VIX close); got '{VixSeries}'.");
        if (VixReducedRiskScale <= 0m || VixReducedRiskScale >= 1m)
            throw new InvalidOperationException("regime.vix_reduced_risk_scale must be above 0 and below 1 (for example 0.5 for half risk).");

        if (hasShortVix)
        {
            if (VixNoShortsBelow is null || VixShortFullRiskAbove is null)
                throw new InvalidOperationException("Short VIX sizing needs all of regime.vix_series, regime.vix_no_new_shorts_below, " +
                    "regime.vix_short_full_risk_above and regime.vix_reduced_risk_scale.");
            if (VixNoShortsBelow <= 0m || VixShortFullRiskAbove < VixNoShortsBelow)
                throw new InvalidOperationException("regime.vix_no_new_shorts_below must be positive and no higher than regime.vix_short_full_risk_above.");
            if (direction is not null && !direction.Equals("short", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Short VIX sizing (vix_no_new_shorts_below / vix_short_full_risk_above) requires direction: short.");
            return;
        }

        if (VixFullRiskBelow is null || VixNoLongsAbove is null)
            throw new InvalidOperationException("VIX sizing needs all of regime.vix_series, regime.vix_full_risk_below, " +
                "regime.vix_no_new_longs_above and regime.vix_reduced_risk_scale.");
        if (VixFullRiskBelow <= 0m || VixNoLongsAbove < VixFullRiskBelow)
            throw new InvalidOperationException("regime.vix_full_risk_below must be positive and no higher than regime.vix_no_new_longs_above.");
        if (direction is not null && !direction.Equals("long", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Long VIX sizing (vix_full_risk_below / vix_no_new_longs_above) requires direction: long.");
    }
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
    int RecentHighLookbackBars,
    int VolatilityContractionLookbackBars,
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
    decimal? MinCloseLocationValue = null,
    bool RejectWeakCloseOnHighRelativeVolume = false,
    decimal WeakCloseMaxLocationValue = 0.40m,
    decimal WeakCloseMinRelativeVolume = 1.0m,
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
    bool EnableEntryBarConfirmation = false,
    decimal MinEntryBarCloseLocationValue = 0.55m,
    bool RejectEntryBarCloseLocationBelowMinimum = false,
    bool RejectEntryBarBreaksSignalMidpoint = false,
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
    bool RequireVolumeSmaRising = false,
    int VolumeSmaPeriod = 5,
    int VolumeSmaRisingLookbackBars = 3,
    decimal? MinVolumeSmaRisePct = null,
    RelativeVolumeMeasure MinVolumeSpikeSource = RelativeVolumeMeasure.CumulativeSameTime,
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
    string PortfolioRankMode = "none",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int Sma200RisingLookbackBars = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinBreakoutVolumeVs50DayAverage = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TechnicalFactorRules? TechnicalFactor = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireOrderedReclaimPullback = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DrawdownQualificationRules? DrawdownQualification = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? HistoricalNewsDiagnostic = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OpeningRangeBreakoutRules? OpeningRangeBreakout = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinCloseBelowSmaPct = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? CloseBelowSmaPeriod = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinShortReversionRsi2 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequirePriceBelowSma200ForShort = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinCloseAboveSmaPct = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? CloseAboveSmaPeriod = null,
    // Sessions on which new entries may be decided (DecisionSchedules); null decides on every completed session.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DecisionSchedule = null,
    // Bollinger squeeze breakout setup: sessions whose band widths rank the previous session's width (0 = off) and the
    // highest rank (0-1, share of those widths at or below it) that still counts as a squeeze.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int BollingerSqueezeLookbackBars = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MaxBollingerWidthPercentile = null,
    // Strategy-owned ADR% (by AverageRangeMethod below) and 20-session average dollar volume floor,
    // shared across backtests, paper trading and the Desk. Omitted from serialized definitions when unset.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinAverageRangePct = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinAverageDollarVolume = null,
    // Rising SMA(50) trend filter: SMA(50) on bar D must exceed SMA(50) this many completed sessions earlier (0 = off).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int Sma50RisingLookbackBars = 0,
    // 21-EMA trend pullback reclaim setup: lookback window (1..N sessions before D), ATR proximity band around EMA(21),
    // maximum relative volume on the pullback bar (dry-up), and whether bar lows from pullback through trigger stay >= SMA(50).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int PullbackLookbackBars = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MaxEma21PullbackAtr = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MaxPullbackRelativeVolume = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequirePullbackLowAboveSma50 = false,
    // Rubber-band reclaim: when true, mean_reversion_reclaim requires a prior stretch bar satisfying BOTH Close < BollingerLower
    // AND Rsi2 < MaxReversionRsi2 (instead of the default OR stretch logic).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireCombinedBollingerAndRsi2Stretch = false,
    // ATR-normalized opening gap threshold: (Open_D - Close_{D-1}) / ATR(14)_{D-1} >= MinGapUpAtr.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinGapUpAtr = null,
    // Daily sub-session opening-range confirmation derived from completed regular-session 5-minute bars of session D
    // (0 = disabled). Shared by long Swing ORB (news_orb_swing) and Checkpoint 8's short mirror (news_orb_breakdown).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int OpeningRangeMinutes = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinOpeningRelativeVolume = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireDailyCloseAboveOpeningRangeHigh = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireDailyCloseAboveSessionVwap = false,
    // Frozen conservative historical news contract: receipt delay added to max(created, updated, observed) and
    // comma-separated allowed Tier-1 catalyst categories (earnings, guidance, fda, contract, upgrade, downgrade).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? CatalystReceiptDelayMinutes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AllowedCatalystCategories = null,
    // Short mirror setup fields (Checkpoint 8 of the 19 Nov 2026 design). All default/null so existing hashes are unchanged.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int RecentLowLookbackBars = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinGapDownAtr = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireOpeningStayedAtOrBelowVwap = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireDailyCloseBelowOpeningRangeLow = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireDailyCloseBelowSessionVwap = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? VetoShortNewsSentimentAtOrAbove = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequirePriceBelowSma50ForShort = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int Sma50FallingLookbackBars = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireBounceHighBelowSma50 = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinParabolicAtrAboveEma21 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinShootingStarWickToBody = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MaxShootingStarCloseLocation = null,
    // How ADR% is measured (AverageRangeMethods). Null is latest_close, the original formula; omitted when unset so every
    // existing strategy keeps its content hash. A new strategy version may choose per_day.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? AverageRangeMethod = null);

/// <summary>The supported ways to measure a strategy's 20-session average daily range percentage (ADR%).</summary>
public static class AverageRangeMethods
{
    /// <summary>Mean(high − low) ÷ the latest close. The default, and the daily screen's formula.</summary>
    public const string LatestClose = "latest_close";

    /// <summary>Mean of each day's own high ÷ low − 1, so a price trend over the window does not distort the result.</summary>
    public const string PerDay = "per_day";

    /// <summary>True for the two supported method names.</summary>
    public static bool IsKnown(string method) => method is LatestClose or PerDay;
}

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
    decimal StopTickBuffer = 0.01m,
    // Doctrine §6A L5: require two consecutive closes below EMA20 before the EMA20 loss exit fires,
    // instead of a single-touch exit that shakes out trend riders on one-bar dips. Default false
    // preserves the existing single-close behaviour for every other strategy.
    bool RequireConfirmedEma20Exit = false,
    int? MaxHoldBars = null,
    decimal? ExitOnRsi2Above = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ExitOnCloseAboveSma5 = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ExitOnCloseBelowSma5 = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool CheckExitsFromFillDay = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ContinuationProtectionRules? ContinuationProtection = null,
    // Close the position, at the next open, when the entry rule's technical factor is at or below this value on a completed bar.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? ExitOnEntryFactorAtOrBelow = null,
    // Sessions on which that factor exit is checked (DecisionSchedules); null checks every completed session.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? EntryFactorExitSchedule = null,
    // On each session of the entry decision schedule, close the position at the next open when the run's research list does
    // not name it for that next session (a fund that dropped out of a ranked list, such as the rotation's top 3).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ExitWhenOffList = false,
    // Volatility unit for initial stop sizing: null or "atr14" uses Wilder ATR(14); "mtr14" uses median of 14 True Ranges
    // (Experiment V2-01). Omitted from serialized definitions when unset so existing strategies keep their content hashes.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? VolatilityUnit = null)
{
    /// <summary>Published rules such as Connors RSI-2 place no protective stop; long-only research backtests size by position notional instead.</summary>
    public const string NoStop = "none";

    /// <summary>No profit-target order; the position leaves only through its technical, time or session exits.</summary>
    public const string NoProfitTarget = "none";

    /// <summary>Wilder ATR(14) initial stop volatility unit (default when <see cref="VolatilityUnit"/> is null).</summary>
    public const string Atr14VolatilityUnit = "atr14";

    /// <summary>Median of 14 completed True Ranges initial stop volatility unit (Experiment V2-01).</summary>
    public const string Mtr14VolatilityUnit = "mtr14";

    /// <summary>True when the strategy places a protective stop order.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasProtectiveStop => !InitialStopMode.Equals(NoStop, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the strategy places a profit-target order.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasProfitTarget => !ProfitTargetMode.Equals(NoProfitTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when initial stop sizing uses MTR-14 instead of Wilder ATR(14).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesMtr14 => VolatilityUnit is not null && VolatilityUnit.Equals(Mtr14VolatilityUnit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the indicator volatility unit configured for this strategy's stops and trailing stops
    /// (<c>Mtr14</c> when <see cref="UsesMtr14"/> is true, otherwise Wilder <c>Atr</c>).
    /// </summary>
    public decimal? ResolveVolatilityUnit(TradingFlow.Domain.Market.IndicatorSnapshot snapshot) =>
        UsesMtr14 ? snapshot.Mtr14 : snapshot.Atr;

    /// <summary>Validates <see cref="VolatilityUnit"/> when explicitly specified (must be "atr14" or "mtr14").</summary>
    public void ValidateVolatilityUnit()
    {
        if (VolatilityUnit is null) return;
        var normalized = VolatilityUnit.Trim();
        if (!normalized.Equals(Atr14VolatilityUnit, StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals(Mtr14VolatilityUnit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"exit_rules.volatility_unit must be '{Atr14VolatilityUnit}' or '{Mtr14VolatilityUnit}'; got '{VolatilityUnit}'.");
        }
    }
}

public sealed record ConfluenceRules(
    bool Enabled,
    string Timeframe,
    int EmaPeriod,
    string MacdFilter);


public sealed record ExecutionRules(
    string Timeframe,
    decimal SlippageBps,
    ExecutionConfirmationRules? Confirmation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? OpeningGapPolicy = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    EntryWindowRules? EntryWindow = null,
    // Annualized short-borrow cost charged in backtest portfolio accounting (entryNotional * aprPct / 100 * max(1, daysHeld) / 365).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? ShortBorrowAprPct = null,
    // SEC Rule 201 short-sale circuit breaker: refuses next-open short entry when bar D's low or D+1 open drops >= 10% from prior close.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool EnforceSecRule201 = false)
{
    /// <summary>Plan from the prior close, keep a trade that opens beyond its stop, and exit it at the next eligible open.</summary>
    public const string KeepAndExitNextOpen = "keep_and_exit_next_open";

    /// <summary>Admission timing for daily strategies that keep opening gaps; otherwise identical to the next-slice market-exit timing.</summary>
    public const string KeptGapTimingPolicy = "prior-close-plan-next-open-fill-kept-gap-exits-v4";

    /// <summary>
    /// The same kept-gap timing on as-traded (unadjusted) Alpaca SIP daily bars, for whole-market research screens whose
    /// stock-days with a split in the look-back are left out. Accepts raw bars only; adjusted bars stay with the policy above.
    /// </summary>
    public const string KeptGapAsTradedTimingPolicy = "prior-close-plan-next-open-fill-kept-gap-exits-as-traded-v1";

    /// <summary>
    /// Decision-hour timing on as-traded bars (approved 12 November 2026): each session's daily rules are evaluated on the
    /// prior completed daily closes plus the 15:00 New York hourly close standing in for that day's close, and entries and
    /// exits fill in the 15:00-16:00 hourly bar (on a half day, the last hourly bar before the early close). Implemented for
    /// backtests (BacktestRunner.DecisionHour.cs); the paper/live runner does not yet decide at the decision hour.
    /// </summary>
    public const string DecisionHourAsTradedTimingPolicy = "decision-hour-close-last-hour-fill-as-traded-v1";

    /// <summary>The bar timeframe decision-hour orders fill on and their stops and targets are checked on.</summary>
    public const string DecisionHourFillTimeframe = "1h";

    /// <summary>
    /// True for the timings that read as-traded (unadjusted) daily bars in whole-market research screens: their holdings are
    /// closed before a split's ex-date and their screens must leave out stock-days with a split in the look-back.
    /// </summary>
    public static bool IsAsTradedResearchTiming(string policy) =>
        policy is KeptGapAsTradedTimingPolicy or DecisionHourAsTradedTimingPolicy;

    public ExecutionConfirmationRules EffectiveConfirmation =>
        Confirmation ?? ExecutionConfirmationRules.Disabled;

    public bool KeepsOpeningGaps() => OpeningGapPolicy == KeepAndExitNextOpen;
}

/// <summary>Explicit, immutable command deadline; never an implied permission to trade a strategy.</summary>
public sealed record EntryWindowRules(string Policy, int Version, int GraceSeconds)
{
    public const string NextRegularOpen = "next_regular_open_window";

    // Reject unknown versions and non-daily/extended setups instead of silently choosing another clock.
    public void Validate(StrategyDefinition strategy)
    {
        if (Policy != NextRegularOpen || Version != 1 || GraceSeconds <= 0 || GraceSeconds > 86400 ||
            strategy.Timeframe != "1d" || strategy.Session.IsContinuousMarket || strategy.Session.UseExtendedHours)
            throw new InvalidOperationException("Entry window requires next_regular_open_window version 1, a positive grace of at most one day and a regular-session daily setup.");
    }
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
