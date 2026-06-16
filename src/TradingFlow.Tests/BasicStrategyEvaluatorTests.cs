using System;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using Xunit;

namespace TradingFlow.Tests;

public class BasicStrategyEvaluatorTests
{
    private readonly BasicStrategyEvaluator _evaluator;

    public BasicStrategyEvaluatorTests()
    {
        _evaluator = new BasicStrategyEvaluator();
    }

    private StrategyDefinition CreateBaseStrategy()
    {
        return new StrategyDefinition(
            StrategyId: "test_strat",
            StrategyName: "Test Strat",
            Source: "test",
            Version: 1,
            Timeframe: "15m",
            Direction: "long",
            EntryRules: new EntryRules(
                SetupType: "momentum",
                MinVolumeSpike: 1.5m,
                MinEntryRsi: 40.0m,
                MaxEntryRsi: 70.0m,
                TrendFilter: "none",
                MacdFilter: "none",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: false,
                RequirePriceAboveVwap: false,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                OpeningRangeMinutes: 0,
                RecentHighLookbackBars: 20,
                VolatilityContractionLookbackBars: 10
            ),
            Confluence: null!,
            ExitRules: null!,
            Execution: null!,
            Session: null!
        );
    }

    private TradeSignal CreateBaseSignal(decimal rsi = 50m)
    {
        return new TradeSignal(
            Ticker: "AAPL",
            Timestamp: DateTimeOffset.UtcNow,
            Timeframe: "15m",
            CurrentPrice: 150m,
            CurrentVolume: 10000m,
            CurrentRsi: rsi,
            CurrentAtr: 2.5m,
            IsAboveVwap: true,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsVwapRejection: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsOpeningRangeBreakdown: false,
            IsRecentHighBreakout: false,
            IsRecentLowBreakdown: false,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: true,
            IsPriceAboveEma50: true,
            IsEma20AboveEma50: true,
            VwapExtensionAtr: null,
            IsAboveBollingerMiddle: true,
            IsMacdHistogramPositive: true,
            IsMacdNotBearish: true,
            IsPriceAboveEma10: true,
            IsEma10AboveEma20: true
        );
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeIsLiquidityFloor_AllowsSoftConfirmationBelowTarget()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                MinVolumeSpike = 2.0m,
                VolumeConfirmationMode = "liquidity_floor",
                MinVolumeLiquidityFloor = 0.15m
            }
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, CreateBaseSignal(), relativeVolume: 0.60m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeIsBelowLiquidityFloor_ReturnsFloorReason()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                MinVolumeSpike = 2.0m,
                VolumeConfirmationMode = "liquidity_floor",
                MinVolumeLiquidityFloor = 0.15m
            }
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, CreateBaseSignal(), relativeVolume: 0.10m);

        Assert.Equal("volume_liquidity_floor_below_minimum (Actual: 0.10, Required: 0.15)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenPriceMustBeAboveEma10AndIsNot_ReturnsEma10Reason()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                RequirePriceAboveEma10 = true
            }
        };
        var signal = CreateBaseSignal() with { IsPriceAboveEma10 = false };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Equal("price_not_above_ema10", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenEma10MustBeAboveEma20AndIsNot_ReturnsEmaStackReason()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                RequireEma10AboveEma20 = true
            }
        };
        var signal = CreateBaseSignal() with { IsEma10AboveEma20 = false };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Equal("ema10_not_above_ema20", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenValid_ReturnsNull()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal();

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Null(rejection);
        Assert.True(_evaluator.IsLongEntryCandidate(strategy, signal, 2.0m));
    }

    [Fact]
    public void GetLongEntryRejection_WhenDirectEntryIsTooExtendedFromVwap_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MaxVwapExtensionPctForDirectEntry = 4.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            CurrentPrice = 10m,
            CurrentAtr = 1m,
            VwapExtensionAtr = 0.75m,
            CloseLocationValue = 0.90m,
            IsVwapPullback = false,
            IsVwapReclaim = false
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Equal("extended_vwap_direct_entry_not_allowed (VwapExtensionPct: 7.50, RequiredMax: 4.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenExtendedEntryHasVwapPullbackStructure_DoesNotRejectAsChase()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MaxVwapExtensionPctForDirectEntry = 4.0m,
                ExtendedVwapMinEntryBarCloseLocationValue = 0.70m
            }
        };
        var signal = CreateBaseSignal() with
        {
            CurrentPrice = 10m,
            CurrentAtr = 1m,
            VwapExtensionAtr = 0.75m,
            CloseLocationValue = 0.90m,
            IsVwapPullback = true
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Null(rejection);
    }
    [Fact]
    public void GetLongEntryRejection_WhenVolumeTooLow_ReturnsFormattedString()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal();

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 1.0m);

        Assert.NotNull(rejection);
        Assert.Equal("relative_volume_below_minimum (Actual: 1.00, Required: 1.50)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenSessionRelativeVolumeTooLow_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.0m,
                MinSessionRelativeVolume = 2.0m
            }
        };
        var signal = CreateBaseSignal() with { SessionRelativeVolume = 1.2m };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 3.0m);

        Assert.Equal("session_relative_volume_below_minimum (Actual: 1.20, Required: 2.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenSessionRelativeVolumeMeetsMinimum_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.0m,
                MinSessionRelativeVolume = 2.0m
            }
        };
        var signal = CreateBaseSignal() with { SessionRelativeVolume = 2.1m };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.5m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeSmaMustRiseAndIsFlat_ReturnsVolumeSmaReason()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.0m,
                RequireVolumeSmaRising = true,
                VolumeSmaRisingLookbackBars = 3
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsVolumeSmaRising = false,
            VolumeSma = 1000m,
            PreviousVolumeSma = 1000m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.5m);

        Assert.Equal("volume_sma_not_rising (CurrentSma: 1000, PreviousSma: 1000, LookbackBars: 3)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeSmaRiseBelowMinimum_ReturnsVolumeSmaRiseReason()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.0m,
                RequireVolumeSmaRising = true,
                MinVolumeSmaRisePct = 20m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsVolumeSmaRising = true,
            VolumeSmaRisePct = 12.5m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.5m);

        Assert.Equal("volume_sma_rise_below_minimum (Actual: 12.50, Required: 20.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRsiTooLow_ReturnsFormattedString()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal(rsi: 30.0m);

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.NotNull(rejection);
        Assert.Equal("rsi_outside_range (Actual: 30.00, Required: 40.00-70.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenLogPriceSlopeTooLow_ReturnsFormattedString()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                SetupType = "log_breakout",
                MinVolumeSpike = 0.5m,
                RequireLogPriceRising = true,
                LogPriceLookbackBars = 12,
                MinLogPriceSlope = 0.001m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsRecentHighBreakout = true,
            PriceLogSlope = 0.0002m,
            PriceLogR2 = 0.65m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("log_price_slope_below_minimum (Actual: 0.000200, Required: 0.001000, LookbackBars: 12)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenLogBreakoutAndTrendPass_ReturnsNull()
    {
        var strategy = CreateBaseStrategy() with
        {
            EntryRules = CreateBaseStrategy().EntryRules with
            {
                SetupType = "log_breakout",
                MinVolumeSpike = 0.5m,
                RequireLogPriceRising = true,
                RequireLogVolumeRising = true,
                LogPriceLookbackBars = 12,
                LogVolumeLookbackBars = 12,
                MinLogPriceSlope = 0.0005m,
                MinLogVolumeSlope = 0.01m,
                MinLogPriceR2 = 0.20m,
                MinLogVolumeR2 = 0.10m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsRecentHighBreakout = true,
            PriceLogSlope = 0.001m,
            PriceLogR2 = 0.72m,
            VolumeLogSlope = 0.04m,
            VolumeLogR2 = 0.52m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolatileVwapReclaimOpeningDrivePasses_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "volatile_vwap_reclaim",
                MinVolumeSpike = 0.5m,
                TrendFilter = "vwap",
                RequirePriceAboveVwap = true,
                RequireLogPriceRising = true,
                RequireLogVolumeRising = true,
                MinLogPriceSlope = 0.0001m,
                MinLogVolumeSlope = 0.001m,
                RejectFallingPriceRisingVolume = true
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsOpeningDriveContinuation = true,
            IsAboveSessionOpen = true,
            PriceLogSlope = 0.001m,
            VolumeLogSlope = 0.01m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenFallingPriceHasRisingVolume_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                RejectFallingPriceRisingVolume = true,
                RedVolumeMaxPriceSlope = -0.0001m,
                RedVolumeMinVolumeSlope = 0.003m
            }
        };
        var signal = CreateBaseSignal() with
        {
            PriceLogSlope = -0.0005m,
            VolumeLogSlope = 0.01m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("falling_price_rising_volume (PriceLogSlope: -0.000500, VolumeLogSlope: 0.010000)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenCloseLocationTooWeak_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                MinCloseLocationValue = 0.60m
            }
        };
        var signal = CreateBaseSignal() with
        {
            CloseLocationValue = 0.37m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("close_location_below_minimum (Actual: 0.37, Required: 0.60)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenWeakCloseHasHighRelativeVolume_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                RejectWeakCloseOnHighRelativeVolume = true,
                WeakCloseMaxLocationValue = 0.40m,
                WeakCloseMinRelativeVolume = 1.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            CloseLocationValue = 0.37m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 1.2m);

        Assert.Equal("weak_close_on_high_relative_volume (CloseLocation: 0.37, RelativeVolume: 1.20)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenDayGainTooLow_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                MinDayGainPct = 4.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            DayGainPct = 1.25m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("day_gain_below_minimum (Actual: 1.25, Required: 4.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenSessionRangeAlreadyDamaged_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                MaxPreEntrySessionRangePct = 6.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            PreEntrySessionRangePct = 8.25m,
            EntryPullbackFromSessionHighPct = 1.0m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("pre_entry_session_range_too_wide (Actual: 8.25, RequiredMax: 6.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenEntryTooFarBelowSessionHigh_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                MaxEntryPullbackFromSessionHighPct = 2.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            PreEntrySessionRangePct = 4.0m,
            EntryPullbackFromSessionHighPct = 3.15m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("entry_too_far_below_session_high (Actual: 3.15, RequiredMax: 2.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenGapAndGoMomentumPasses_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "gap_and_go_momentum",
                MinVolumeSpike = 0.5m,
                TrendFilter = "vwap",
                RequirePriceAboveVwap = true,
                RequireLogPriceRising = true,
                RequireLogVolumeRising = true,
                MinLogPriceSlope = 0.0001m,
                MinLogVolumeSlope = 0.001m,
                MinDayGainPct = 4.0m,
                MinSessionGainPct = 1.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsRecentHighBreakout = true,
            IsAboveSessionOpen = true,
            PriceLogSlope = 0.001m,
            VolumeLogSlope = 0.01m,
            DayGainPct = 8.5m,
            SessionGainPct = 2.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenGapAndGoHasNoBreakout_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "gap_and_go_momentum",
                MinVolumeSpike = 0.5m,
                TrendFilter = "vwap",
                RequirePriceAboveVwap = true,
                RequireLogPriceRising = true,
                RequireLogVolumeRising = true,
                MinLogPriceSlope = 0.0001m,
                MinLogVolumeSlope = 0.001m,
                MinDayGainPct = 4.0m,
                MinSessionGainPct = 1.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsOpeningDriveContinuation = true,
            IsAboveSessionOpen = true,
            PriceLogSlope = 0.001m,
            VolumeLogSlope = 0.01m,
            DayGainPct = 8.5m,
            SessionGainPct = 2.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("setup_gap_and_go_momentum_not_triggered", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRossBullFlagBreakoutPasses_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "ross_gap_go_bull_flag",
                MinVolumeSpike = 2.0m,
                TrendFilter = "vwap",
                MacdFilter = "not_bearish",
                RequirePriceAboveVwap = true,
                RequirePriceAboveEma20 = true,
                MinCloseLocationValue = 0.60m,
                MinDayGainPct = 4.0m,
                MinSessionGainPct = 1.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsAboveSessionOpen = true,
            IsBullFlagBreakout = true,
            IsAboveVwap = true,
            IsPriceAboveEma20 = true,
            IsMacdNotBearish = true,
            CloseLocationValue = 0.72m,
            DayGainPct = 8.5m,
            SessionGainPct = 2.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.3m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRossPatternMissing_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "ross_gap_go_bull_flag",
                MinVolumeSpike = 2.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsAboveSessionOpen = true
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.3m);

        Assert.Equal("setup_ross_gap_go_bull_flag_not_triggered", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRossOpeningExhaustion_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "ross_gap_go_bull_flag",
                MinVolumeSpike = 2.0m,
                EnablePremarketFilter = true,
                RejectOpeningExhaustion = true,
                OpeningExhaustionMinutes = 20,
                OpeningExhaustionMaxDayGainPct = 18.0m,
                OpeningExhaustionMaxSessionRangePct = 10.0m,
                OpeningExhaustionMinPullbackFromHighPct = 0.50m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsAboveSessionOpen = true,
            IsRecentHighBreakout = true,
            MinutesAfterRegularOpen = 5,
            DayGainPct = 29.6m,
            PreEntrySessionRangePct = 9.7m,
            EntryPullbackFromSessionHighPct = 0.06m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 5.57m);

        Assert.Equal("opening_exhaustion_risk (MinutesAfterOpen: 5, DayGain: 29.60, SessionRange: 9.70, PullbackFromHigh: 0.06)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRossPremarketVwapExtensionTooHigh_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "ross_gap_go_bull_flag",
                MinVolumeSpike = 2.0m,
                EnablePremarketFilter = true,
                MaxPremarketVwapExtensionPct = 12.0m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsAboveSessionOpen = true,
            IsBullFlagBreakout = true,
            PremarketVwapExtensionPct = 18.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.3m);

        Assert.Equal("premarket_vwap_extension_too_high (Actual: 18.20, RequiredMax: 12.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenFreshNegativeNewsBelowVetoThreshold_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                VetoNewsSentimentBelow = -0.50m,
                MaxNewsAgeHours = 24m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            Catalyst = new CatalystEvent(
                "AAPL",
                timestamp.AddHours(-1),
                CatalystType.NewsReport,
                "Company cuts guidance after weak demand",
                -0.82m,
                "alpaca",
                "news-1")
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("negative_news_sentiment (Actual: -0.82, VetoBelow: -0.50, Headline: Company cuts guidance after weak demand)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenNegativeNewsIsStale_DoesNotVeto()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                VetoNewsSentimentBelow = -0.50m,
                MaxNewsAgeHours = 2m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            Catalyst = new CatalystEvent(
                "AAPL",
                timestamp.AddHours(-6),
                CatalystType.NewsReport,
                "Old negative article",
                -0.82m)
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenPositiveNewsRequiredButMissing_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                RequirePositiveNews = true,
                MinNewsSentiment = 0.25m,
                MaxNewsAgeHours = 12m
            }
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, CreateBaseSignal(), relativeVolume: 0.8m);

        Assert.Equal("positive_news_required (MaxAgeHours: 12.0)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenPositiveNewsRequiredAndPresent_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                MinVolumeSpike = 0.5m,
                RequirePositiveNews = true,
                MinNewsSentiment = 0.25m,
                MaxNewsAgeHours = 12m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            Catalyst = new CatalystEvent(
                "AAPL",
                timestamp.AddHours(-1),
                CatalystType.NewsReport,
                "Company wins new datacenter contract",
                0.78m)
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenCatalystTechnicalEntryHasNoTechnicalTrigger_ReturnsSetupRejection()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "catalyst_vwap_breakout",
                MinVolumeSpike = 0.5m,
                RequirePositiveNews = true,
                MinNewsSentiment = 0.25m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            IsAboveSessionOpen = true,
            IsVwapReclaim = false,
            IsVwapPullback = false,
            IsOpeningRangeBreakout = false,
            IsRecentHighBreakout = false,
            IsOpeningDriveContinuation = false,
            Catalyst = new CatalystEvent("AAPL", timestamp.AddMinutes(-30), CatalystType.NewsReport, "Good news", 0.75m),
            CatalystAgeHours = 0.5m,
            CatalystPriceMovePct = 3.0m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("setup_catalyst_vwap_breakout_not_triggered", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenCatalystEntryAlreadyTooExtended_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "catalyst_vwap_breakout",
                MinVolumeSpike = 0.5m,
                RequirePositiveNews = true,
                MinNewsSentiment = 0.25m,
                MaxCatalystPriceMovePct = 12.0m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            IsAboveSessionOpen = true,
            IsVwapReclaim = true,
            Catalyst = new CatalystEvent("AAPL", timestamp.AddMinutes(-30), CatalystType.NewsReport, "Good news", 0.75m),
            CatalystAgeHours = 0.5m,
            CatalystPriceMovePct = 18.4m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("catalyst_entry_too_extended (Actual: 18.40, RequiredMax: 12.00)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenCatalystTechnicalEntryPasses_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "vwap_pullback",
                MinVolumeSpike = 0.5m,
                TrendFilter = "vwap",
                MacdFilter = "not_bearish",
                RequirePriceAboveVwap = true,
                RequirePositiveNews = true,
                MinNewsSentiment = 0.25m,
                MinCatalystPriceMovePct = 0.5m,
                MaxCatalystPriceMovePct = 12.0m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal() with
        {
            Timestamp = timestamp,
            IsAboveSessionOpen = true,
            IsVwapReclaim = true,
            IsAboveVwap = true,
            IsMacdNotBearish = true,
            Catalyst = new CatalystEvent("AAPL", timestamp.AddMinutes(-30), CatalystType.NewsReport, "Good news", 0.75m),
            CatalystAgeHours = 0.5m,
            CatalystPriceMovePct = 4.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVwapTrapRulesPass_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "vwap_reclaim_trap",
                MinVolumeSpike = 0.0m,
                MinSessionRelativeVolume = 1.0m,
                RequirePriceAboveVwap = true,
                RequirePriorFlushBelowVwapBars = 3,
                VwapReclaimMaxBarsSinceFlush = 12,
                MinReclaimVolumeRatio = 1.50m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsVwapReclaimTrap = true,
            PriorFlushBelowVwapBars = 3,
            BarsSinceVwapFlush = 4,
            ReclaimVolumeRatio = 2.0m,
            IsAboveVwap = true,
            SessionRelativeVolume = 1.2m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenAvwapDailyRuleRunsOnIntradaySignal_ReturnsUnavailableReason()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            Timeframe = "65m",
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "avwap_pullback_bounce",
                MinVolumeSpike = 0.0m,
                AvwapProximityPct = 1.5m,
                RequirePriceAboveSma50Daily = true
            }
        };
        var signal = CreateBaseSignal() with
        {
            Timeframe = "65m",
            IsAnchoredVwapBounce = true,
            AnchoredVwapProximityPct = 0.5m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Equal("daily_sma50_check_unavailable", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVcpRulesPass_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            EntryRules = baseStrategy.EntryRules with
            {
                SetupType = "volatility_contraction_pattern",
                MinVolumeSpike = 0.0m,
                RequirePriceAboveSma150 = true,
                RequirePriceAboveSma200 = true,
                RequireSma50AboveSma150 = true,
                RequireSma150AboveSma200 = true,
                MinPriceVs52WeekLowPct = 30.0m,
                MaxPriceVs52WeekHighPct = -25.0m,
                MinContractions = 2,
                MaxContractions = 4,
                RequireVolatilityHalvingLeftToRight = true,
                RequireVolumeDryUpPreBreakout = true,
                MinBreakoutVolumeRatio = 1.50m
            }
        };
        var signal = CreateBaseSignal() with
        {
            IsVcpBreakout = true,
            IsPriceAboveSma150 = true,
            IsPriceAboveSma200 = true,
            IsSma50AboveSma150 = true,
            IsSma150AboveSma200 = true,
            PriceVs52WeekLowPct = 45.0m,
            PriceVs52WeekHighPct = -10.0m,
            VolatilityContractions = 3,
            IsVolatilityHalving = true,
            IsVolumeDryUp = true,
            BreakoutVolumeRatio = 2.0m
        };

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetShortEntryRejection_WhenCatalystVwapBreakdownPasses_ReturnsNull()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            Direction = "long_short",
            EntryRules = baseStrategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "catalyst_vwap_breakdown",
                MinVolumeSpike = 0.5m,
                RequirePriceBelowVwapForShort = true,
                RequireMacdBearishForShort = true,
                MaxShortEntryRsi = 55m,
                MaxShortCloseLocationValue = 0.45m,
                MaxShortNewsSentiment = 0.10m,
                MinShortCatalystDropPct = 1.0m,
                MaxNewsAgeHours = 12m
            }
        };
        var timestamp = DateTimeOffset.Parse("2026-06-09T15:00:00Z");
        var signal = CreateBaseSignal(rsi: 42m) with
        {
            Timestamp = timestamp,
            IsAboveVwap = false,
            IsVwapRejection = true,
            IsBelowSessionOpen = true,
            IsMacdNotBearish = false,
            CloseLocationValue = 0.25m,
            Catalyst = new CatalystEvent("AAPL", timestamp.AddMinutes(-40), CatalystType.NewsReport, "Company announces weak demand", -0.40m),
            CatalystAgeHours = 0.67m,
            CatalystPriceMovePct = -2.3m
        };

        var rejection = _evaluator.GetShortEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Null(rejection);
    }

    [Fact]
    public void GetShortEntryRejection_WhenMacdNotBearish_ReturnsFormattedString()
    {
        var baseStrategy = CreateBaseStrategy();
        var strategy = baseStrategy with
        {
            Direction = "long_short",
            EntryRules = baseStrategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "vwap_rejection",
                MinVolumeSpike = 0.5m,
                RequirePriceBelowVwapForShort = true,
                RequireMacdBearishForShort = true
            }
        };
        var signal = CreateBaseSignal(rsi: 42m) with
        {
            IsAboveVwap = false,
            IsVwapRejection = true,
            IsMacdNotBearish = true
        };

        var rejection = _evaluator.GetShortEntryRejection(strategy, signal, relativeVolume: 0.8m);

        Assert.Equal("short_macd_not_bearish", rejection);
    }

}
