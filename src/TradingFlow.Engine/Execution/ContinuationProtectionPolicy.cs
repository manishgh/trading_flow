using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Engine.Execution;

/// <summary>
/// Computes protection changes from completed five-minute candles, without filling
/// orders. Callers must first process the currently active stop/target and all other
/// exits through the decision time, then pass the resulting open-position status.
/// </summary>
public static class ContinuationProtectionPolicy
{
    private static readonly TimeSpan CandleDuration = TimeSpan.FromMinutes(5);

    // Capture actual fill risk once, rejecting missing costs/ticks and impossible initial orders.
    public static ContinuationProtectionState Create(
        string ticker,
        PlannedOrderSide side,
        DateTimeOffset enteredAtUtc,
        decimal entryPrice,
        decimal initialStopPrice,
        decimal estimatedRoundTripCostPerShare,
        decimal tickSize,
        ContinuationProtectionRules rules,
        decimal initialTargetR = 3m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(rules);
        if (!Enum.IsDefined(side) || entryPrice <= 0m || initialStopPrice <= 0m ||
            (side == PlannedOrderSide.Long ? initialStopPrice >= entryPrice : initialStopPrice <= entryPrice))
        {
            throw new ArgumentException("Actual entry and initial stop must define positive directional risk.");
        }

        if (estimatedRoundTripCostPerShare < 0m || tickSize <= 0m || initialTargetR < 3m)
        {
            throw new ArgumentException("Supply nonnegative round-trip per-share costs, a positive valid tick, and an initial target of at least 3R.");
        }

        rules.ValidateParameters(initialTargetR);
        var state = new ContinuationProtectionState(ticker, side, enteredAtUtc, entryPrice,
            initialStopPrice, initialTargetR, estimatedRoundTripCostPerShare, tickSize, rules);
        state = state with { TargetPrice = RoundTargetOutward(side, state.TargetPrice, tickSize) };
        if (state.TargetPrice <= 0m)
        {
            throw new ArgumentException("The initial target must be a positive executable price.");
        }

        return state;
    }

    // Rebase weighted actual fills without loosening earned protection or replaying completed continuation candles.
    public static ContinuationProtectionState RebaseActualFills(ContinuationProtectionState previous,
        decimal weightedEntryPrice, decimal estimatedRoundTripCostPerShare)
    {
        var rebased = Create(previous.Ticker, previous.Side, previous.EnteredAtUtc, weightedEntryPrice,
            previous.InitialStopPrice, estimatedRoundTripCostPerShare, previous.TickSize, previous.Rules, previous.InitialTargetR);
        var direction = previous.Side == PlannedOrderSide.Long ? 1m : -1m;
        var costCovered = weightedEntryPrice + direction *
            (estimatedRoundTripCostPerShare + previous.Rules.CostBufferTicks * previous.TickSize);
        var minimumStop = previous.IsActivated ? (previous.Side == PlannedOrderSide.Long
            ? Math.Ceiling(costCovered / previous.TickSize) : Math.Floor(costCovered / previous.TickSize)) * previous.TickSize
            : rebased.StopPrice;
        var target = weightedEntryPrice + direction *
            (previous.InitialTargetR + previous.CompletedTargetExtensions * previous.Rules.TargetStepR) * rebased.OriginalRiskPerShare;
        return rebased with
        {
            StopPrice = previous.Side == PlannedOrderSide.Long ? Math.Max(previous.StopPrice, minimumStop)
                : Math.Min(previous.StopPrice, minimumStop),
            TargetPrice = RoundTargetOutward(previous.Side, target, previous.TickSize),
            IsActivated = previous.IsActivated, CompletedTargetExtensions = previous.CompletedTargetExtensions,
            ConsecutiveContinuationBars = previous.ConsecutiveContinuationBars,
            PreviousCompletedBar = previous.PreviousCompletedBar, LastEvaluatedAtUtc = previous.LastEvaluatedAtUtc
        };
    }

    // Consume each eligible candle once, after existing orders have had first claim on every event.
    public static ContinuationProtectionDecision Evaluate(
        ContinuationProtectionState state,
        OhlcvBar bar,
        DateTimeOffset asOfUtc,
        bool isPositionOpen,
        DateTimeOffset activeOrdersProcessedThroughUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bar);
        if (!isPositionOpen)
        {
            return Unchanged(state, "position_closed");
        }

        if (activeOrdersProcessedThroughUtc != asOfUtc)
        {
            throw new ArgumentException("Process active orders exactly through the decision time before evaluating protection changes.");
        }

        ValidateBar(state, bar);
        if (bar.Timestamp < state.EnteredAtUtc)
        {
            return Unchanged(state, "candle_began_before_entry");
        }

        if (bar.Timestamp.Add(CandleDuration) > asOfUtc || bar.KnownAtUtc > asOfUtc)
        {
            return Unchanged(state, "candle_not_completed_or_known");
        }

        if (state.PreviousCompletedBar is { } previous && bar.Timestamp <= previous.Timestamp ||
            state.LastEvaluatedAtUtc is { } lastEvaluation && asOfUtc <= lastEvaluation)
        {
            return Unchanged(state, "duplicate_or_out_of_order");
        }

        var next = state with { PreviousCompletedBar = bar, LastEvaluatedAtUtc = asOfUtc };
        return !state.IsActivated
            ? EvaluateActivation(next, bar, asOfUtc)
            : EvaluateContinuation(state, next, bar, asOfUtc);
    }

    // Trigger cost-covered protection on the completed close, never on an intrabar high/low.
    private static ContinuationProtectionDecision EvaluateActivation(
        ContinuationProtectionState state,
        OhlcvBar bar,
        DateTimeOffset asOfUtc)
    {
        var direction = state.Side == PlannedOrderSide.Long ? 1m : -1m;
        var closeProfit = direction * (bar.Close - state.EntryPrice);
        if (closeProfit < state.Rules.ActivationR * state.OriginalRiskPerShare)
        {
            return Unchanged(state, "activation_close_not_reached");
        }

        var costBuffer = state.EstimatedRoundTripCostPerShare + state.Rules.CostBufferTicks * state.TickSize;
        var rawStop = state.EntryPrice + direction * costBuffer;
        var stop = state.Side == PlannedOrderSide.Long
            ? Math.Ceiling(rawStop / state.TickSize) * state.TickSize
            : Math.Floor(rawStop / state.TickSize) * state.TickSize;
        if (!IsTighterExecutableStop(state, stop, bar.Close))
        {
            return Unchanged(state, "cost_covered_stop_not_executable");
        }

        return new(state with { IsActivated = true, StopPrice = stop }, true, asOfUtc, "cost_covered_stop_activated");
    }

    // Require fresh adjacent candles after activation/extension before moving both orders together.
    private static ContinuationProtectionDecision EvaluateContinuation(
        ContinuationProtectionState previousState,
        ContinuationProtectionState next,
        OhlcvBar bar,
        DateTimeOffset asOfUtc)
    {
        var previous = previousState.PreviousCompletedBar!;
        if (bar.Timestamp != previous.Timestamp.Add(CandleDuration) || !ContinuesTrend(next.Side, previous, bar))
        {
            return Unchanged(next with { ConsecutiveContinuationBars = 0 }, "continuation_reset");
        }

        var count = previousState.ConsecutiveContinuationBars + 1;
        if (count < next.Rules.ConfirmationBars)
        {
            return Unchanged(next with { ConsecutiveContinuationBars = count }, "continuation_waiting");
        }

        // A completed confirmation group is consumed even if it cannot improve an executable stop.
        next = next with { ConsecutiveContinuationBars = 0 };
        // Keep the stop outside the candle extreme when that extreme falls between valid ticks.
        var stop = next.Side == PlannedOrderSide.Long
            ? Math.Floor(bar.Low / next.TickSize) * next.TickSize
            : Math.Ceiling(bar.High / next.TickSize) * next.TickSize;
        if (!IsTighterExecutableStop(next, stop, bar.Close))
        {
            return Unchanged(next, "continuation_stop_not_tighter_or_executable");
        }

        var direction = next.Side == PlannedOrderSide.Long ? 1m : -1m;
        var rawTarget = next.TargetPrice + direction * next.Rules.TargetStepR * next.OriginalRiskPerShare;
        var target = RoundTargetOutward(next.Side, rawTarget, next.TickSize);
        if (target <= 0m)
        {
            return Unchanged(next, "extended_target_not_executable");
        }

        return new(next with { StopPrice = stop, TargetPrice = target,
            CompletedTargetExtensions = previousState.CompletedTargetExtensions + 1 }, true, asOfUtc, "continuation_stop_and_target_extended");
    }

    // Strict comparisons require all three directional candle features to progress together.
    private static bool ContinuesTrend(PlannedOrderSide side, OhlcvBar previous, OhlcvBar current) =>
        side == PlannedOrderSide.Long
            ? current.High > previous.High && current.Low > previous.Low && current.Close > previous.Close
            : current.High < previous.High && current.Low < previous.Low && current.Close < previous.Close;

    // Reject looser stops and stops that would already be marketable at the decision candle close.
    private static bool IsTighterExecutableStop(ContinuationProtectionState state, decimal stop, decimal close) =>
        stop > 0m && (state.Side == PlannedOrderSide.Long
            ? stop > state.StopPrice && stop < close
            : stop < state.StopPrice && stop > close);

    // Round targets away from entry so tick alignment cannot shorten an original-R target step.
    private static decimal RoundTargetOutward(PlannedOrderSide side, decimal target, decimal tickSize) =>
        side == PlannedOrderSide.Long
            ? Math.Ceiling(target / tickSize) * tickSize
            : Math.Floor(target / tickSize) * tickSize;

    // Accept only the position's valid five-minute OHLC evidence; timestamps are candle-open times.
    private static void ValidateBar(ContinuationProtectionState state, OhlcvBar bar)
    {
        if (!String.Equals(bar.Ticker, state.Ticker, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(bar.Timeframe, "5m", StringComparison.OrdinalIgnoreCase) ||
            bar.Open <= 0m || bar.Low <= 0m || bar.Close <= 0m ||
            bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close) ||
            bar.Volume < 0m)
        {
            throw new ArgumentException("Supply valid OHLC prices and nonnegative volume for this position's five-minute candle.");
        }
    }

    // Preserve current levels and expose the reason even when only candle sequencing advances.
    private static ContinuationProtectionDecision Unchanged(ContinuationProtectionState state, string reason) =>
        new(state, false, null, reason);
}
