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
        if (!strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
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

    private static bool PassesSetupType(string setupType, TradeSignal signal)
    {
        return setupType.ToLowerInvariant() switch
        {
            "momentum" => true,
            "vwap_pullback" => signal.IsVwapPullback || signal.IsVwapReclaim,
            "opening_range_breakout" => signal.IsOpeningRangeBreakout,
            "trend_pullback" => signal.IsEma20Pullback || signal.IsVwapPullback,
            "vcp_trend_breakout" => signal.IsRecentHighBreakout && signal.IsVolatilityContraction,
            _ => throw new NotSupportedException($"Unsupported setup_type: {setupType}.")
        };
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
            _ => throw new NotSupportedException($"Unsupported trend_filter: {trendFilter}.")
        };
    }
}
