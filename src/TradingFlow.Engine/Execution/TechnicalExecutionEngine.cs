using System;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Execution;

public sealed class TechnicalExecutionEngine
{
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
        if (bar.Low <= currentStopLossPrice)
        {
            var exitReason = currentStopLossPrice > initialStopLossPrice ? "trailing_stop" : "stop_loss";
            return (currentStopLossPrice, exitReason);
        }

        // Check Hard Take Profit
        if (bar.High >= takeProfitPrice)
        {
            return (takeProfitPrice, "take_profit");
        }

        // Check Technical Exits (e.g. crossing below EMA/VWAP)
        var technicalExitReason = GetTechnicalExitReason(strategy, snapshot, barsHeld, priceLogSlope, volumeLogSlope, previousSnapshot);
        if (technicalExitReason is not null)
        {
            // We usually can't execute at the EXACT bar close easily in real life, but for standard technical exits we signal it here.
            // In a real execution, we'd exit at the next open, but we return the signal now.
            return (bar.Close, technicalExitReason); 
        }

        // Check Max Hold Timeout
        var maxExitTimestamp = entryTimestamp.AddHours((double)strategy.ExitRules.MaxHoldHours);
        if (bar.Timestamp >= maxExitTimestamp)
        {
            return (bar.Close, "max_hold");
        }

        // Update High Watermark for next iteration
        highestHighSinceEntry = Math.Max(highestHighSinceEntry, bar.High);

        return (null, null);
    }

    public decimal CalculateEffectiveStopLoss(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        decimal entryPrice,
        decimal stopDistance,
        decimal initialStopLossPrice,
        decimal currentStopLossPrice,
        decimal highestHighSinceEntry)
    {
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
            snapshot.CurrentPrice < snapshot.Ema20.Value)
        {
            return "technical_exit_below_ema20";
        }

        if (strategy.ExitRules.ExitOnCloseBelowVwap &&
            snapshot.Vwap is not null &&
            snapshot.CurrentPrice < snapshot.Vwap.Value)
        {
            return "technical_exit_below_vwap";
        }

        if (strategy.ExitRules.ExitOnMacdHistogramNegative &&
            snapshot.MacdHistogram is not null &&
            snapshot.MacdHistogram.Value < 0)
        {
            return "technical_exit_macd_histogram_negative";
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
