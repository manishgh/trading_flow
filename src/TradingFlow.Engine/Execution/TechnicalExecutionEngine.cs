using System;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Execution;

public sealed class TechnicalExecutionEngine
{
    /// <summary>Rejects policies whose paired stop/target lifecycle is not supported by the caller.</summary>
    public static void RequireSupportedExitPolicy(StrategyDefinition strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (strategy.ExitRules.ContinuationProtection is not null)
            throw new NotSupportedException(
                "Continuation protection requires the day-trading order lifecycle; this execution path cannot apply its stop and target updates yet.");
    }

    /// <summary>Evaluates supported long exits with existing protection before technical or time exits.</summary>
    public (decimal? ExitPrice, string? ExitReason) EvaluateBarForExit(
        StrategyDefinition strategy,
        OhlcvBar bar,
        IndicatorSnapshot snapshot,
        decimal entryPrice,
        decimal initialStopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset entryTimestamp,
        decimal stopDistance,
        int barsHeld,
        ref decimal currentStopLossPrice,
        ref decimal highestHighSinceEntry,
        decimal? priceLogSlope = null,
        decimal? volumeLogSlope = null,
        IndicatorSnapshot? previousSnapshot = null)
    {
        RequireSupportedExitPolicy(strategy);
        // Update Effective Stop Loss
        currentStopLossPrice = CalculateEffectiveStopLoss(
            strategy,
            snapshot,
            entryPrice,
            stopDistance,
            initialStopLossPrice,
            currentStopLossPrice,
            highestHighSinceEntry);

        // Check Hard Stop Loss
        if (strategy.ExitRules.HasProtectiveStop && bar.Low <= currentStopLossPrice)
        {
            var exitReason = currentStopLossPrice > initialStopLossPrice ? "trailing_stop" : "stop_loss";
            return (currentStopLossPrice, exitReason);
        }

        // Check Hard Take Profit
        if (strategy.ExitRules.HasProfitTarget && bar.High >= takeProfitPrice)
        {
            return (takeProfitPrice, "take_profit");
        }

        var scheduledReason = GetTechnicalOrTimeExitReason(strategy, snapshot, barsHeld,
            bar.Timestamp, entryTimestamp, priceLogSlope, volumeLogSlope, previousSnapshot);
        if (scheduledReason is not null) return (bar.Close, scheduledReason);

        // Update High Watermark for next iteration
        highestHighSinceEntry = Math.Max(highestHighSinceEntry, bar.High);

        return (null, null);
    }

    public string? GetTechnicalOrTimeExitReason(StrategyDefinition strategy,
        IndicatorSnapshot snapshot, int barsHeld, DateTimeOffset sourceTimestamp,
        DateTimeOffset entrySourceTimestamp, decimal? priceLogSlope = null,
        decimal? volumeLogSlope = null, IndicatorSnapshot? previousSnapshot = null)
    {
        var technical = GetTechnicalExitReason(strategy, snapshot, barsHeld,
            priceLogSlope, volumeLogSlope, previousSnapshot);
        if (technical is not null) return technical;
        if (strategy.ExitRules.MaxHoldBars is { } maximum && maximum > 0 && barsHeld >= maximum)
            return "max_hold_bars";
        return sourceTimestamp >= entrySourceTimestamp.AddHours((double)strategy.ExitRules.MaxHoldHours)
            ? "max_hold" : null;
    }

    /// <summary>Calculates the existing ATR stop; continuation protection needs its own stateful lifecycle.</summary>
    public decimal CalculateEffectiveStopLoss(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        decimal entryPrice,
        decimal stopDistance,
        decimal initialStopLossPrice,
        decimal currentStopLossPrice,
        decimal highestHighSinceEntry)
    {
        RequireSupportedExitPolicy(strategy);
        if (!strategy.ExitRules.EnableAtrTrailingStop ||
            snapshot.Atr is null ||
            snapshot.Atr.Value <= 0)
        {
            return currentStopLossPrice;
        }

        var profitR = (highestHighSinceEntry - entryPrice) / stopDistance;
        if (profitR < strategy.ExitRules.TrailingActivationR)
        {
            return currentStopLossPrice;
        }

        var trailingStop = highestHighSinceEntry - (strategy.ExitRules.TrailingStopAtrMultiple * snapshot.Atr.Value);
        return Math.Max(initialStopLossPrice, Math.Max(currentStopLossPrice, trailingStop));
    }

    public string? GetTechnicalExitReason(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        int barsHeld,
        decimal? priceLogSlope = null,
        decimal? volumeLogSlope = null,
        IndicatorSnapshot? previousSnapshot = null)
    {
        if (barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit)
        {
            return null;
        }

        if (strategy.ExitRules.ExitOnCloseBelowEma20 &&
            snapshot.Ema20 is not null &&
            snapshot.CurrentPrice < snapshot.Ema20.Value &&
            (!strategy.ExitRules.RequireConfirmedEma20Exit ||
                (previousSnapshot is { Ema20: { } previousEma20 } && previousSnapshot.CurrentPrice < previousEma20)))
        {
            // With RequireConfirmedEma20Exit, a single close below EMA20 is only a warning; the prior
            // bar must also have closed below its EMA20 (two-close confirmation) before we exit.
            return "technical_exit_below_ema20";
        }

        // If confirmed VWAP failure is enabled, a single close below VWAP is only a warning.
        // The caller should use ShouldExitLongOnConfirmedVwapFailure before evaluating standard exits.
        if (strategy.ExitRules.ExitOnCloseBelowVwap &&
            !strategy.ExitRules.EnableConfirmedVwapExit &&
            snapshot.Vwap is not null &&
            snapshot.CurrentPrice < snapshot.Vwap.Value)
        {
            return "technical_exit_below_vwap";
        }

        if (strategy.ExitRules.ExitOnEma10CrossBelowEma20 &&
            IsEma10CrossedBelowEma20(snapshot, previousSnapshot))
        {
            return "technical_exit_ema10_cross_below_ema20";
        }

        if (strategy.ExitRules.ExitOnMacdHistogramNegative &&
            snapshot.MacdHistogram is not null &&
            snapshot.MacdHistogram.Value < 0)
        {
            return "technical_exit_macd_histogram_negative";
        }

        if (strategy.ExitRules.ExitOnCloseAboveSma5 && snapshot.Sma5 is { } sma5 && snapshot.CurrentPrice > sma5)
            return "technical_exit_above_sma5";

        if (strategy.ExitRules.ExitOnRsi2Above is { } maxRsi2 &&
            snapshot.Rsi2 is { } rsi2 &&
            rsi2 > maxRsi2)
        {
            return "technical_exit_rsi2_recovered";
        }

        if (strategy.ExitRules.ExitOnSma10NearSma20 &&
            IsSma10NearSma20(snapshot, strategy.ExitRules.Sma10NearSma20Pct))
        {
            return "technical_exit_sma10_near_sma20";
        }

        if (strategy.ExitRules.ExitOnSma10CrossBelowSma20 &&
            previousSnapshot is not null &&
            previousSnapshot.Sma10 is { } previousSma10 &&
            previousSnapshot.Sma20 is { } previousSma20 &&
            snapshot.Sma10 is { } currentSma10 &&
            snapshot.Sma20 is { } currentSma20 &&
            previousSma10 >= previousSma20 &&
            currentSma10 < currentSma20)
        {
            return "technical_exit_sma10_cross_below_sma20";
        }

        if (strategy.ExitRules.ExitOnLogPriceFade &&
            priceLogSlope is { } slope &&
            slope <= strategy.ExitRules.MaxExitLogPriceSlope &&
            (!strategy.ExitRules.RequireRisingVolumeForLogFadeExit ||
                (volumeLogSlope is { } volumeSlope && volumeSlope >= strategy.ExitRules.MinExitLogVolumeSlope)) &&
            (!strategy.ExitRules.RequireBelowVwapForLogFadeExit ||
                (snapshot.Vwap is { } vwap && snapshot.CurrentPrice < vwap)))
        {
            return "technical_exit_log_price_fade";
        }

        return null;
    }

    public static bool IsSma10NearSma20(IndicatorSnapshot snapshot, decimal nearPct)
    {
        if (snapshot.Sma10 is null || snapshot.Sma20 is null || snapshot.Sma20.Value == 0m)
        {
            return false;
        }

        var distancePct = Math.Abs(snapshot.Sma10.Value - snapshot.Sma20.Value) / Math.Abs(snapshot.Sma20.Value) * 100m;
        return distancePct <= nearPct;
    }

    public static bool IsSma10CrossedAboveSma20(IndicatorSnapshot snapshot, IndicatorSnapshot? previousSnapshot)
    {
        return previousSnapshot is not null &&
            previousSnapshot.Sma10 is { } previousSma10 &&
            previousSnapshot.Sma20 is { } previousSma20 &&
            snapshot.Sma10 is { } currentSma10 &&
            snapshot.Sma20 is { } currentSma20 &&
            previousSma10 <= previousSma20 &&
            currentSma10 > currentSma20;
    }

    public static bool IsEma10CrossedBelowEma20(IndicatorSnapshot snapshot, IndicatorSnapshot? previousSnapshot)
    {
        return previousSnapshot is not null &&
            previousSnapshot.Ema10 is { } previousEma10 &&
            previousSnapshot.Ema20 is { } previousEma20 &&
            snapshot.Ema10 is { } currentEma10 &&
            snapshot.Ema20 is { } currentEma20 &&
            previousEma10 >= previousEma20 &&
            currentEma10 < currentEma20;
    }

    // A runner can dip below session VWAP and still continue. This treats VWAP loss as actionable
    // only after consecutive closes below VWAP by a configurable ATR buffer before the trade has proven itself.
    public bool ShouldExitLongOnConfirmedVwapFailure(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int entryIndex,
        decimal entryPrice,
        decimal stopDistance,
        decimal highestHighSinceEntry,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableConfirmedVwapExit ||
            barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit ||
            stopDistance <= 0)
        {
            return false;
        }

        if (strategy.ExitRules.DisableConfirmedVwapExitAfterR is { } disableAfterR &&
            ((highestHighSinceEntry - entryPrice) / stopDistance) >= disableAfterR)
        {
            return false;
        }

        var confirmationBars = Math.Max(strategy.ExitRules.ConfirmedVwapExitBars, 1);
        if (index - confirmationBars + 1 < entryIndex)
        {
            return false;
        }

        for (var i = index - confirmationBars + 1; i <= index; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Vwap is null || snapshot.Atr is null)
            {
                return false;
            }

            var failureLevel = snapshot.Vwap.Value - (snapshot.Atr.Value * strategy.ExitRules.ConfirmedVwapExitAtrBuffer);
            if (snapshot.CurrentPrice >= failureLevel)
            {
                return false;
            }
        }

        return true;
    }

    public static (decimal Slope, decimal R2)? ComputeLogTrend(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int lookback,
        Func<OhlcvBar, decimal> valueSelector,
        bool addOne)
    {
        if (lookback < 2 || index < lookback - 1)
        {
            return null;
        }

        var startIndex = index - lookback + 1;
        var n = lookback;
        var sumX = 0d;
        var sumY = 0d;
        var sumX2 = 0d;
        var sumXY = 0d;
        var yValues = new double[n];

        for (var offset = 0; offset < n; offset++)
        {
            var rawValue = (double)valueSelector(bars[startIndex + offset]);
            if (addOne)
            {
                rawValue += 1d;
            }

            if (rawValue <= 0d)
            {
                return null;
            }

            var x = (double)offset;
            var y = Math.Log(rawValue);
            yValues[offset] = y;
            sumX += x;
            sumY += y;
            sumX2 += x * x;
            sumXY += x * y;
        }

        var denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < Double.Epsilon)
        {
            return null;
        }

        var slope = (n * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;
        var meanY = sumY / n;
        var sse = 0d;
        var sst = 0d;

        for (var offset = 0; offset < n; offset++)
        {
            var predicted = intercept + slope * offset;
            var residual = yValues[offset] - predicted;
            sse += residual * residual;

            var variance = yValues[offset] - meanY;
            sst += variance * variance;
        }

        var r2 = sst <= Double.Epsilon
            ? 1d
            : Math.Clamp(1d - sse / sst, 0d, 1d);

        return ((decimal)slope, (decimal)r2);
    }
}
