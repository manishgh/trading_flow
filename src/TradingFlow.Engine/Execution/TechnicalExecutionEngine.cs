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
        ref decimal highestHighSinceEntry)
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
        var technicalExitReason = GetTechnicalExitReason(strategy, snapshot, barsHeld);
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
        int barsHeld)
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

        return null;
    }
}
