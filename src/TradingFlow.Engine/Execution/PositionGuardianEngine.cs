using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Execution;

/// <summary>
/// Evaluates whether an already-open long position should be held, closed, or have its stop raised.
/// This is intentionally independent from broker APIs so paper, live, and app-triggered entries can
/// reuse the same exit math without duplicating trading logic in UI or orchestration code.
/// </summary>
public sealed class PositionGuardianEngine
{
    private readonly TechnicalExecutionEngine technicalExecutionEngine = new();

    public PositionGuardianDecision EvaluateLong(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> executionBars,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots,
        DateTimeOffset entryTimestamp,
        decimal entryPrice,
        decimal initialStopLossPrice,
        decimal takeProfitPrice)
    {
        if (executionBars.Count == 0 || executionSnapshots.Count == 0)
        {
            return PositionGuardianDecision.Hold("market_state_unavailable", initialStopLossPrice);
        }

        var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
        if (entryIndex >= executionBars.Count)
        {
            return PositionGuardianDecision.Hold("waiting_for_post_entry_bar", initialStopLossPrice);
        }

        var stopDistance = entryPrice - initialStopLossPrice;
        if (entryPrice <= 0m || stopDistance <= 0m)
        {
            return PositionGuardianDecision.Hold("invalid_entry_or_stop_distance", initialStopLossPrice);
        }

        var currentStopLossPrice = initialStopLossPrice;
        var highestHighSinceEntry = entryPrice;

        for (var index = entryIndex; index < executionBars.Count; index++)
        {
            var bar = executionBars[index];
            var snapshot = executionSnapshots[Math.Min(index, executionSnapshots.Count - 1)];
            var previousSnapshot = index > 0 && index - 1 < executionSnapshots.Count
                ? executionSnapshots[index - 1]
                : null;

            var priceLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var volumeLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);

            if (technicalExecutionEngine.ShouldExitLongOnConfirmedVwapFailure(
                    strategy,
                    executionSnapshots,
                    index,
                    entryIndex,
                    entryPrice,
                    stopDistance,
                    Math.Max(highestHighSinceEntry, bar.High),
                    index - entryIndex))
            {
                return PositionGuardianDecision.Exit(
                    "confirmed_vwap_failure",
                    bar.Close,
                    bar.Timestamp,
                    currentStopLossPrice,
                    highestHighSinceEntry,
                    index - entryIndex);
            }

            var (candidateExitPrice, candidateExitReason) = technicalExecutionEngine.EvaluateBarForExit(
                strategy,
                bar,
                snapshot,
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                index - entryIndex,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                priceLogTrend?.Slope,
                volumeLogTrend?.Slope,
                previousSnapshot);

            if (candidateExitReason is not null)
            {
                return PositionGuardianDecision.Exit(
                    candidateExitReason,
                    candidateExitPrice,
                    bar.Timestamp,
                    currentStopLossPrice,
                    highestHighSinceEntry,
                    index - entryIndex);
            }
        }

        return PositionGuardianDecision.Hold("strategy_still_valid", currentStopLossPrice, highestHighSinceEntry);
    }

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }
}

public sealed record PositionGuardianDecision(
    bool ShouldExit,
    string Reason,
    decimal? ExitPrice,
    DateTimeOffset? ExitSignalTimestamp,
    decimal CurrentStopLossPrice,
    decimal HighestHighSinceEntry,
    int BarsHeld)
{
    public static PositionGuardianDecision Hold(
        string reason,
        decimal currentStopLossPrice,
        decimal? highestHighSinceEntry = null)
    {
        return new PositionGuardianDecision(
            false,
            reason,
            null,
            null,
            currentStopLossPrice,
            highestHighSinceEntry ?? 0m,
            0);
    }

    public static PositionGuardianDecision Exit(
        string reason,
        decimal? exitPrice,
        DateTimeOffset exitSignalTimestamp,
        decimal currentStopLossPrice,
        decimal highestHighSinceEntry,
        int barsHeld)
    {
        return new PositionGuardianDecision(
            true,
            reason,
            exitPrice,
            exitSignalTimestamp,
            currentStopLossPrice,
            highestHighSinceEntry,
            barsHeld);
    }
}
