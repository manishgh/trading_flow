using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

public sealed class BasicStrategyEvaluator
{
    public bool IsLongEntryCandidate(StrategyDefinition strategy, TradeSignal signal, decimal relativeVolume)
    {
        return GetLongEntryRejection(strategy, signal, relativeVolume) is null;
    }

    public string? GetLongEntryRejection(StrategyDefinition strategy, TradeSignal signal, decimal relativeVolume)
    {
        if (!AllowsLong(strategy))
        {
            return "direction_not_long";
        }

        if (!strategy.Timeframe.Equals(signal.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return "timeframe_mismatch";
        }


        if (relativeVolume < strategy.EntryRules.MinVolumeSpike)
        {
            return $"relative_volume_below_minimum (Actual: {relativeVolume:F2}, Required: {strategy.EntryRules.MinVolumeSpike:F2})";
        }

        if (signal.CurrentRsi < strategy.EntryRules.MinEntryRsi ||
            signal.CurrentRsi > strategy.EntryRules.MaxEntryRsi)
        {
            return $"rsi_outside_range (Actual: {signal.CurrentRsi:F2}, Required: {strategy.EntryRules.MinEntryRsi:F2}-{strategy.EntryRules.MaxEntryRsi:F2})";
        }

        if (!PassesSetupType(strategy.EntryRules.SetupType, signal))
        {
            return $"setup_{strategy.EntryRules.SetupType}_not_triggered";
        }

        var premarketRejection = GetPremarketRejection(strategy, signal);
        if (premarketRejection is not null)
        {
            return premarketRejection;
        }

        var newsRejection = GetNewsSentimentRejection(strategy, signal);
        if (newsRejection is not null)
        {
            return newsRejection;
        }

        var logTrendRejection = GetLogTrendRejection(strategy, signal);
        if (logTrendRejection is not null)
        {
            return logTrendRejection;
        }

        var redVolumeRejection = GetRedVolumeRejection(strategy, signal);
        if (redVolumeRejection is not null)
        {
            return redVolumeRejection;
        }

        if (strategy.EntryRules.MinCloseLocationValue is { } minCloseLocation &&
            (signal.CloseLocationValue is null || signal.CloseLocationValue.Value < minCloseLocation))
        {
            return $"close_location_below_minimum (Actual: {signal.CloseLocationValue?.ToString("F2") ?? "n/a"}, Required: {minCloseLocation:F2})";
        }

        if (strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume &&
            relativeVolume >= strategy.EntryRules.WeakCloseMinRelativeVolume &&
            (signal.CloseLocationValue is null || signal.CloseLocationValue.Value < strategy.EntryRules.WeakCloseMaxLocationValue))
        {
            return $"weak_close_on_high_relative_volume (CloseLocation: {signal.CloseLocationValue?.ToString("F2") ?? "n/a"}, RelativeVolume: {relativeVolume:F2})";
        }

        if (strategy.EntryRules.MinDayGainPct is { } minDayGain &&
            (signal.DayGainPct is null || signal.DayGainPct.Value < minDayGain))
        {
            return $"day_gain_below_minimum (Actual: {signal.DayGainPct?.ToString("F2") ?? "n/a"}, Required: {minDayGain:F2})";
        }

        if (strategy.EntryRules.MinSessionGainPct is { } minSessionGain &&
            (signal.SessionGainPct is null || signal.SessionGainPct.Value < minSessionGain))
        {
            return $"session_gain_below_minimum (Actual: {signal.SessionGainPct?.ToString("F2") ?? "n/a"}, Required: {minSessionGain:F2})";
        }

        if (strategy.EntryRules.MaxPreEntrySessionRangePct is { } maxSessionRange &&
            (signal.PreEntrySessionRangePct is null || signal.PreEntrySessionRangePct.Value > maxSessionRange))
        {
            return $"pre_entry_session_range_too_wide (Actual: {signal.PreEntrySessionRangePct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxSessionRange:F2})";
        }

        if (strategy.EntryRules.MaxEntryPullbackFromSessionHighPct is { } maxPullbackFromHigh &&
            (signal.EntryPullbackFromSessionHighPct is null || signal.EntryPullbackFromSessionHighPct.Value > maxPullbackFromHigh))
        {
            return $"entry_too_far_below_session_high (Actual: {signal.EntryPullbackFromSessionHighPct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxPullbackFromHigh:F2})";
        }

        if (!PassesTrendFilter(strategy.EntryRules.TrendFilter, signal))
        {
            return $"trend_{strategy.EntryRules.TrendFilter}_failed";
        }

        if (strategy.EntryRules.RequirePriceAboveVwap && !signal.IsAboveVwap)
        {
            return "price_not_above_vwap";
        }

        if (strategy.EntryRules.RequirePriceAboveEma20 && !signal.IsPriceAboveEma20)
        {
            return "price_not_above_ema20";
        }

        if (strategy.EntryRules.RequirePriceAboveEma50 && !signal.IsPriceAboveEma50)
        {
            return "price_not_above_ema50";
        }

        if (strategy.EntryRules.RequireEma20AboveEma50 && !signal.IsEma20AboveEma50)
        {
            return "ema20_not_above_ema50";
        }

        if (strategy.EntryRules.RequirePriceAboveSma10 && !signal.IsPriceAboveSma10)
        {
            return "price_not_above_sma10";
        }

        if (strategy.EntryRules.RequirePriceAboveSma20 && !signal.IsPriceAboveSma20)
        {
            return "price_not_above_sma20";
        }

        if (strategy.EntryRules.RequirePriceAboveSma50 && !signal.IsPriceAboveSma50)
        {
            return "price_not_above_sma50";
        }

        if (strategy.EntryRules.RequireSma10AboveSma20 && !signal.IsSma10AboveSma20)
        {
            return "sma10_not_above_sma20";
        }

        if (strategy.EntryRules.RequireSma20AboveSma50 && !signal.IsSma20AboveSma50)
        {
            return "sma20_not_above_sma50";
        }

        if (strategy.EntryRules.RequirePriceAboveAnchoredVwap && !signal.IsAboveAnchoredVwap)
        {
            return "price_not_above_anchored_vwap";
        }

        if (strategy.EntryRules.MaxAnchoredVwapExtensionAtr is { } maxAnchoredExtension &&
            (signal.AnchoredVwapExtensionAtr is null || signal.AnchoredVwapExtensionAtr.Value > maxAnchoredExtension))
        {
            return "anchored_vwap_extension_too_high";
        }

        if (strategy.EntryRules.MaxVwapExtensionAtr is { } maxExtension &&
            (signal.VwapExtensionAtr is null || signal.VwapExtensionAtr.Value > maxExtension))
        {
            return "vwap_extension_too_high";
        }

        if (strategy.EntryRules.RequirePriceAboveBollingerMiddle && !signal.IsAboveBollingerMiddle)
        {
            return "price_not_above_bollinger_middle";
        }

        if (strategy.EntryRules.RequireMacdHistogramPositive && !signal.IsMacdHistogramPositive)
        {
            return "macd_histogram_not_positive";
        }

        if (strategy.EntryRules.MacdFilter.Equals("not_bearish", StringComparison.OrdinalIgnoreCase) &&
            !signal.IsMacdNotBearish)
        {
            return "macd_bearish";
        }

        return null;
    }

    private static string? GetPremarketRejection(StrategyDefinition strategy, TradeSignal signal)
    {
        if (!strategy.EntryRules.EnablePremarketFilter)
        {
            return null;
        }

        if (strategy.EntryRules.RequirePremarketHighBreak && !signal.IsPremarketHighBreak)
        {
            return $"premarket_high_not_broken (PremarketHigh: {signal.PremarketHigh?.ToString("F2") ?? "n/a"}, BufferPct: {strategy.EntryRules.PremarketHighBreakBufferPct:F2})";
        }

        if (strategy.EntryRules.MaxPremarketVwapExtensionPct is { } maxPremarketVwapExtension &&
            (signal.PremarketVwapExtensionPct is null || signal.PremarketVwapExtensionPct.Value > maxPremarketVwapExtension))
        {
            return $"premarket_vwap_extension_too_high (Actual: {signal.PremarketVwapExtensionPct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxPremarketVwapExtension:F2})";
        }

        if (strategy.EntryRules.MaxPremarketRunPct is { } maxPremarketRun &&
            (signal.PremarketRunPct is null || signal.PremarketRunPct.Value > maxPremarketRun))
        {
            return $"premarket_run_too_extended (Actual: {signal.PremarketRunPct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxPremarketRun:F2})";
        }

        if (strategy.EntryRules.MaxOpeningRangePct is { } maxOpeningRange &&
            (signal.PreEntrySessionRangePct is null || signal.PreEntrySessionRangePct.Value > maxOpeningRange))
        {
            return $"opening_range_too_wide (Actual: {signal.PreEntrySessionRangePct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxOpeningRange:F2})";
        }

        if (strategy.EntryRules.MinConsecutiveClosesAboveVwap > 0 &&
            signal.ConsecutiveClosesAboveVwap < strategy.EntryRules.MinConsecutiveClosesAboveVwap)
        {
            return $"insufficient_vwap_hold (Actual: {signal.ConsecutiveClosesAboveVwap}, Required: {strategy.EntryRules.MinConsecutiveClosesAboveVwap})";
        }

        if (strategy.EntryRules.RejectOpeningExhaustion &&
            signal.MinutesAfterRegularOpen is { } minutesAfterOpen &&
            minutesAfterOpen <= strategy.EntryRules.OpeningExhaustionMinutes)
        {
            var dayGainTooHigh = strategy.EntryRules.OpeningExhaustionMaxDayGainPct is { } maxDayGain &&
                signal.DayGainPct is not null &&
                signal.DayGainPct.Value > maxDayGain;
            var rangeTooWide = strategy.EntryRules.OpeningExhaustionMaxSessionRangePct is { } maxSessionRange &&
                signal.PreEntrySessionRangePct is not null &&
                signal.PreEntrySessionRangePct.Value > maxSessionRange;
            var noHealthyPullback = signal.EntryPullbackFromSessionHighPct is null ||
                signal.EntryPullbackFromSessionHighPct.Value < strategy.EntryRules.OpeningExhaustionMinPullbackFromHighPct;

            if ((dayGainTooHigh || rangeTooWide) && noHealthyPullback)
            {
                return "opening_exhaustion_risk " +
                    $"(MinutesAfterOpen: {minutesAfterOpen}, " +
                    $"DayGain: {signal.DayGainPct?.ToString("F2") ?? "n/a"}, " +
                    $"SessionRange: {signal.PreEntrySessionRangePct?.ToString("F2") ?? "n/a"}, " +
                    $"PullbackFromHigh: {signal.EntryPullbackFromSessionHighPct?.ToString("F2") ?? "n/a"})";
            }
        }

        return null;
    }

    public string? GetShortEntryRejection(StrategyDefinition strategy, TradeSignal signal, decimal relativeVolume)
    {
        if (!AllowsShort(strategy))
        {
            return "direction_not_short";
        }

        if (!strategy.Timeframe.Equals(signal.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return "timeframe_mismatch";
        }


        if (relativeVolume < strategy.EntryRules.MinVolumeSpike)
        {
            return $"relative_volume_below_minimum (Actual: {relativeVolume:F2}, Required: {strategy.EntryRules.MinVolumeSpike:F2})";
        }

        if (strategy.EntryRules.MinShortEntryRsi is { } minShortRsi &&
            signal.CurrentRsi < minShortRsi)
        {
            return $"short_rsi_below_range (Actual: {signal.CurrentRsi:F2}, RequiredMin: {minShortRsi:F2})";
        }

        if (strategy.EntryRules.MaxShortEntryRsi is { } maxShortRsi &&
            signal.CurrentRsi > maxShortRsi)
        {
            return $"short_rsi_above_range (Actual: {signal.CurrentRsi:F2}, RequiredMax: {maxShortRsi:F2})";
        }

        if (!PassesShortSetupType(strategy.EntryRules.ShortSetupType, signal))
        {
            return $"setup_{strategy.EntryRules.ShortSetupType}_not_triggered";
        }

        if (strategy.EntryRules.RequirePriceBelowVwapForShort && signal.IsAboveVwap)
        {
            return "short_price_not_below_vwap";
        }

        if (strategy.EntryRules.RequireMacdBearishForShort && signal.IsMacdNotBearish)
        {
            return "short_macd_not_bearish";
        }

        if (strategy.EntryRules.RequirePriceBelowAnchoredVwapForShort && !signal.IsBelowAnchoredVwap)
        {
            return "short_price_not_below_anchored_vwap";
        }

        if (strategy.EntryRules.RequirePriceBelowShortAnchoredVwap && !signal.IsBelowShortAnchoredVwap)
        {
            return "short_price_not_below_short_anchored_vwap";
        }

        if (strategy.EntryRules.MaxAnchoredVwapExtensionAtr is { } maxAnchoredExtension &&
            (signal.AnchoredVwapExtensionAtr is null || signal.AnchoredVwapExtensionAtr.Value > maxAnchoredExtension))
        {
            return "short_anchored_vwap_extension_too_high";
        }

        if (strategy.EntryRules.MaxShortAnchoredVwapExtensionAtr is { } maxShortAnchoredExtension &&
            (signal.ShortAnchoredVwapExtensionAtr is null || signal.ShortAnchoredVwapExtensionAtr.Value > maxShortAnchoredExtension))
        {
            return "short_short_anchored_vwap_extension_too_high";
        }

        if (strategy.EntryRules.MaxShortCloseLocationValue is { } maxCloseLocation &&
            (signal.CloseLocationValue is null || signal.CloseLocationValue.Value > maxCloseLocation))
        {
            return $"short_close_location_too_high (Actual: {signal.CloseLocationValue?.ToString("F2") ?? "n/a"}, RequiredMax: {maxCloseLocation:F2})";
        }

        var shortNewsRejection = GetShortNewsRejection(strategy, signal);
        if (shortNewsRejection is not null)
        {
            return shortNewsRejection;
        }

        return null;
    }

    private static bool PassesSetupType(string setupType, TradeSignal signal)
    {
        return setupType.ToLowerInvariant() switch
        {
            "momentum" => true,
            "vwap_pullback" => signal.IsVwapPullback || signal.IsVwapReclaim,
            "opening_range_breakout" => signal.IsOpeningRangeBreakout,
            "trend_pullback" => signal.IsEma20Pullback || signal.IsVwapPullback,
            "vcp_trend_breakout" => signal.IsRecentHighBreakout && signal.IsVolatilityContraction,
            "log_breakout" => signal.IsRecentHighBreakout,
            "log_vcp_breakout" => signal.IsRecentHighBreakout && signal.IsVolatilityContraction,
            "step_breakout" => signal.IsStepBreakout,
            "swing_reclaim" => signal.IsSwingReclaim,
            "volatile_vwap_reclaim" => signal.IsAboveSessionOpen &&
                (signal.IsVwapReclaim || signal.IsVwapPullback || signal.IsOpeningDriveContinuation),
            "gap_and_go_momentum" => signal.IsAboveSessionOpen &&
                (signal.IsOpeningRangeBreakout || signal.IsRecentHighBreakout),
            "ross_gap_go_bull_flag" => signal.IsAboveSessionOpen &&
                (signal.IsOpeningRangeBreakout ||
                 signal.IsRecentHighBreakout ||
                 signal.IsBullFlagBreakout ||
                 signal.IsFlatTopBreakout),
            "catalyst_vwap_breakout" => signal.IsAboveSessionOpen &&
                (signal.IsVwapReclaim ||
                 signal.IsVwapPullback ||
                 signal.IsOpeningRangeBreakout ||
                 signal.IsRecentHighBreakout ||
                 signal.IsOpeningDriveContinuation),
            "catalyst_technical_entry" => signal.IsAboveSessionOpen &&
                (signal.IsVwapReclaim ||
                 signal.IsVwapPullback ||
                 signal.IsOpeningRangeBreakout ||
                 signal.IsRecentHighBreakout ||
                 signal.IsOpeningDriveContinuation),
            _ => throw new NotSupportedException($"Unsupported setup_type: {setupType}.")
        };
    }

    private static bool PassesShortSetupType(string setupType, TradeSignal signal)
    {
        return setupType.ToLowerInvariant() switch
        {
            "catalyst_vwap_breakdown" => signal.IsBelowSessionOpen &&
                (signal.IsVwapRejection ||
                 signal.IsOpeningRangeBreakdown ||
                 signal.IsRecentLowBreakdown),
            "bear_flag_breakdown" => signal.IsBelowSessionOpen &&
                (signal.IsVwapRejection || signal.IsRecentLowBreakdown),
            "step_breakdown" => signal.IsStepBreakdown,
            "swing_rollover" => signal.IsSwingRollover,
            "opening_range_breakdown" => signal.IsOpeningRangeBreakdown,
            "vwap_rejection" => signal.IsVwapRejection,
            _ => throw new NotSupportedException($"Unsupported short_setup_type: {setupType}.")
        };
    }

    private static string? GetNewsSentimentRejection(StrategyDefinition strategy, TradeSignal signal)
    {
        var catalyst = signal.Catalyst;
        if (catalyst is not null)
        {
            var ageHours = signal.CatalystAgeHours ?? (decimal)Math.Abs((signal.Timestamp - catalyst.Timestamp).TotalHours);
            if (ageHours > strategy.EntryRules.MaxNewsAgeHours)
            {
                catalyst = null;
            }
        }

        if (catalyst is not null &&
            strategy.EntryRules.VetoNewsSentimentBelow is { } vetoThreshold &&
            catalyst.SentimentScore <= vetoThreshold)
        {
            return $"negative_news_sentiment (Actual: {catalyst.SentimentScore:F2}, VetoBelow: {vetoThreshold:F2}, Headline: {catalyst.Headline})";
        }

        if (!strategy.EntryRules.RequirePositiveNews)
        {
            return null;
        }

        if (catalyst is null)
        {
            return $"positive_news_required (MaxAgeHours: {strategy.EntryRules.MaxNewsAgeHours:F1})";
        }

        var minSentiment = strategy.EntryRules.MinNewsSentiment ?? 0.15m;
        if (catalyst.SentimentScore < minSentiment)
        {
            return $"news_sentiment_below_minimum (Actual: {catalyst.SentimentScore:F2}, Required: {minSentiment:F2}, Headline: {catalyst.Headline})";
        }

        if (strategy.EntryRules.MinCatalystPriceMovePct is { } minMove &&
            (signal.CatalystPriceMovePct is null || signal.CatalystPriceMovePct.Value < minMove))
        {
            return $"catalyst_price_move_below_minimum (Actual: {signal.CatalystPriceMovePct?.ToString("F2") ?? "n/a"}, Required: {minMove:F2})";
        }

        if (strategy.EntryRules.MaxCatalystPriceMovePct is { } maxMove &&
            (signal.CatalystPriceMovePct is null || signal.CatalystPriceMovePct.Value > maxMove))
        {
            return $"catalyst_entry_too_extended (Actual: {signal.CatalystPriceMovePct?.ToString("F2") ?? "n/a"}, RequiredMax: {maxMove:F2})";
        }

        return null;
    }

    private static string? GetShortNewsRejection(StrategyDefinition strategy, TradeSignal signal)
    {
        var catalyst = signal.Catalyst;
        if (catalyst is not null)
        {
            var ageHours = signal.CatalystAgeHours ?? (decimal)Math.Abs((signal.Timestamp - catalyst.Timestamp).TotalHours);
            if (ageHours > strategy.EntryRules.MaxNewsAgeHours)
            {
                catalyst = null;
            }
        }

        if (strategy.EntryRules.MaxShortNewsSentiment is { } maxSentiment)
        {
            if (catalyst is null)
            {
                return $"short_news_required (MaxAgeHours: {strategy.EntryRules.MaxNewsAgeHours:F1})";
            }

            if (catalyst.SentimentScore > maxSentiment)
            {
                return $"short_news_sentiment_too_positive (Actual: {catalyst.SentimentScore:F2}, RequiredMax: {maxSentiment:F2}, Headline: {catalyst.Headline})";
            }
        }

        if (strategy.EntryRules.MinShortCatalystDropPct is { } minDrop &&
            (signal.CatalystPriceMovePct is null || signal.CatalystPriceMovePct.Value > -minDrop))
        {
            return $"short_catalyst_drop_below_minimum (Actual: {signal.CatalystPriceMovePct?.ToString("F2") ?? "n/a"}, RequiredDrop: {minDrop:F2})";
        }

        return null;
    }

    private static bool AllowsLong(StrategyDefinition strategy)
    {
        return strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsShort(StrategyDefinition strategy)
    {
        return strategy.EntryRules.EnableShort &&
            (strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetRedVolumeRejection(StrategyDefinition strategy, TradeSignal signal)
    {
        if (!strategy.EntryRules.RejectFallingPriceRisingVolume)
        {
            return null;
        }

        if (signal.PriceLogSlope is null || signal.VolumeLogSlope is null)
        {
            return "red_volume_check_unavailable";
        }

        if (signal.PriceLogSlope.Value <= strategy.EntryRules.RedVolumeMaxPriceSlope &&
            signal.VolumeLogSlope.Value >= strategy.EntryRules.RedVolumeMinVolumeSlope)
        {
            return $"falling_price_rising_volume (PriceLogSlope: {signal.PriceLogSlope.Value:F6}, VolumeLogSlope: {signal.VolumeLogSlope.Value:F6})";
        }

        return null;
    }

    private static string? GetLogTrendRejection(StrategyDefinition strategy, TradeSignal signal)
    {
        if (strategy.EntryRules.RequireLogPriceRising)
        {
            if (signal.PriceLogSlope is null)
            {
                return $"log_price_trend_unavailable (LookbackBars: {strategy.EntryRules.LogPriceLookbackBars})";
            }

            if (signal.PriceLogSlope.Value < strategy.EntryRules.MinLogPriceSlope)
            {
                return $"log_price_slope_below_minimum (Actual: {signal.PriceLogSlope.Value:F6}, Required: {strategy.EntryRules.MinLogPriceSlope:F6}, LookbackBars: {strategy.EntryRules.LogPriceLookbackBars})";
            }

            if (strategy.EntryRules.MinLogPriceR2 is { } minPriceR2 &&
                (signal.PriceLogR2 is null || signal.PriceLogR2.Value < minPriceR2))
            {
                return $"log_price_trend_quality_below_minimum (Actual: {signal.PriceLogR2?.ToString("F2") ?? "n/a"}, Required: {minPriceR2:F2}, LookbackBars: {strategy.EntryRules.LogPriceLookbackBars})";
            }
        }

        if (strategy.EntryRules.RequireLogVolumeRising)
        {
            if (signal.VolumeLogSlope is null)
            {
                return $"log_volume_trend_unavailable (LookbackBars: {strategy.EntryRules.LogVolumeLookbackBars})";
            }

            if (signal.VolumeLogSlope.Value < strategy.EntryRules.MinLogVolumeSlope)
            {
                return $"log_volume_slope_below_minimum (Actual: {signal.VolumeLogSlope.Value:F6}, Required: {strategy.EntryRules.MinLogVolumeSlope:F6}, LookbackBars: {strategy.EntryRules.LogVolumeLookbackBars})";
            }

            if (strategy.EntryRules.MinLogVolumeR2 is { } minVolumeR2 &&
                (signal.VolumeLogR2 is null || signal.VolumeLogR2.Value < minVolumeR2))
            {
                return $"log_volume_trend_quality_below_minimum (Actual: {signal.VolumeLogR2?.ToString("F2") ?? "n/a"}, Required: {minVolumeR2:F2}, LookbackBars: {strategy.EntryRules.LogVolumeLookbackBars})";
            }
        }

        return null;
    }

    private static bool PassesTrendFilter(string trendFilter, TradeSignal signal)
    {
        return trendFilter.ToLowerInvariant() switch
        {
            "none" => true,
            "vwap" => signal.IsAboveVwap,
            "ema20" => signal.IsPriceAboveEma20,
            "ema50" => signal.IsPriceAboveEma50,
            "ema20_above_ema50" => signal.IsPriceAboveEma20 && signal.IsPriceAboveEma50 && signal.IsEma20AboveEma50,
            "sma10_above_sma20" => signal.IsPriceAboveSma10 && signal.IsPriceAboveSma20 && signal.IsSma10AboveSma20,
            "sma_stack_10_20_50" => signal.IsPriceAboveSma10 && signal.IsPriceAboveSma20 && signal.IsPriceAboveSma50 && signal.IsSma10AboveSma20 && signal.IsSma20AboveSma50,
            _ => throw new NotSupportedException($"Unsupported trend_filter: {trendFilter}.")
        };
    }
}
