using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Engine.Execution;

public sealed record CompletedBarExecutionRequest(
    StrategyDefinition Strategy,
    TradeSignal SetupSignal,
    PlannedOrderSide Side,
    IReadOnlyList<OhlcvBar> ExecutionBars,
    IReadOnlyList<IndicatorSnapshot> ExecutionSnapshots,
    Func<DateTimeOffset, bool> IsExecutionBarEligible,
    bool RequireKnownFillBar);

public sealed record CompletedBarExecutionPlan(
    bool IsReady,
    int? ConfirmationIndex,
    int? StopContextIndex,
    int? EntryIndex,
    string? RejectionReason)
{
    public static CompletedBarExecutionPlan Ready(
        int confirmationIndex,
        int stopContextIndex,
        int? entryIndex) =>
        new(true, confirmationIndex, stopContextIndex, entryIndex, null);

    public static CompletedBarExecutionPlan Rejected(string reason) =>
        new(false, null, null, null, reason);
}

/// <summary>
/// Plans execution from completed bars only. The setup bar is never a fill bar,
/// a confirmation bar is never a fill bar in backtests, and stop context always
/// ends before the fill.
/// </summary>
public sealed class CompletedBarExecutionPlanner
{
    public CompletedBarExecutionPlan Plan(CompletedBarExecutionRequest request)
    {
        var alignmentFailure = ValidateAlignment(
            request.ExecutionBars,
            request.ExecutionSnapshots);
        if (alignmentFailure is not null)
        {
            return CompletedBarExecutionPlan.Rejected(alignmentFailure);
        }

        var setupAvailableAt = request.SetupSignal.Timestamp.Add(
            TimeframeParser.Parse(request.Strategy.Timeframe));
        var confirmation = request.Strategy.Execution.EffectiveConfirmation;

        if (!confirmation.Enabled)
        {
            return PlanWithoutConfirmation(request, setupAvailableAt);
        }

        if (confirmation.MaxBarsAfterSetup <= 0)
        {
            return CompletedBarExecutionPlan.Rejected(
                "execution_confirmation_max_bars_invalid");
        }

        var ruleFailure = ValidateRules(confirmation);
        if (ruleFailure is not null)
        {
            return CompletedBarExecutionPlan.Rejected(ruleFailure);
        }

        var eligibleBarsExamined = 0;
        string? lastFailure = null;
        for (var index = 0; index < request.ExecutionBars.Count; index++)
        {
            var bar = request.ExecutionBars[index];
            if (bar.Timestamp < setupAvailableAt ||
                !request.IsExecutionBarEligible(bar.Timestamp))
            {
                continue;
            }

            eligibleBarsExamined++;
            if (eligibleBarsExamined > confirmation.MaxBarsAfterSetup)
            {
                break;
            }

            lastFailure = GetConfirmationFailure(
                request,
                confirmation,
                index);
            if (lastFailure is not null)
            {
                continue;
            }

            if (!request.RequireKnownFillBar)
            {
                return CompletedBarExecutionPlan.Ready(index, index, null);
            }

            var entryIndex = FindNextEligibleIndex(request, index + 1);
            if (entryIndex >= request.ExecutionBars.Count)
            {
                return CompletedBarExecutionPlan.Rejected(
                    "no_next_bar_after_execution_confirmation");
            }

            return CompletedBarExecutionPlan.Ready(index, index, entryIndex);
        }

        var detail = String.IsNullOrWhiteSpace(lastFailure)
            ? String.Empty
            : $": {lastFailure}";
        return CompletedBarExecutionPlan.Rejected(
            $"execution_confirmation_not_triggered{detail}");
    }

    private static CompletedBarExecutionPlan PlanWithoutConfirmation(
        CompletedBarExecutionRequest request,
        DateTimeOffset setupAvailableAt)
    {
        if (!request.RequireKnownFillBar)
        {
            var contextIndex = FindLastEligibleIndexAtOrBefore(
                request,
                setupAvailableAt);
            return contextIndex < 0
                ? CompletedBarExecutionPlan.Rejected(
                    "completed_pre_fill_stop_context_unavailable")
                : CompletedBarExecutionPlan.Ready(
                    contextIndex,
                    contextIndex,
                    null);
        }

        var entryIndex = FindFirstEligibleIndexAtOrAfter(
            request,
            setupAvailableAt);
        if (entryIndex >= request.ExecutionBars.Count)
        {
            return CompletedBarExecutionPlan.Rejected("no_next_execution_bar");
        }

        var stopContextIndex = FindPreviousEligibleIndex(
            request,
            entryIndex - 1);
        if (stopContextIndex < 0)
        {
            return CompletedBarExecutionPlan.Rejected(
                "completed_pre_fill_stop_context_unavailable");
        }

        return CompletedBarExecutionPlan.Ready(
            stopContextIndex,
            stopContextIndex,
            entryIndex);
    }

    private static string? GetConfirmationFailure(
        CompletedBarExecutionRequest request,
        ExecutionConfirmationRules rules,
        int index)
    {
        var bar = request.ExecutionBars[index];
        var snapshot = request.ExecutionSnapshots[index];
        var previousBar = index > 0 ? request.ExecutionBars[index - 1] : null;

        var priceFailure = Normalize(rules.PriceFilter) switch
        {
            ExecutionConfirmationRules.NoFilter => null,
            ExecutionConfirmationRules.CloseAboveSetupClose
                when bar.Close <= request.SetupSignal.CurrentPrice =>
                "confirmation_close_not_above_setup_close",
            ExecutionConfirmationRules.CloseBelowSetupClose
                when bar.Close >= request.SetupSignal.CurrentPrice =>
                "confirmation_close_not_below_setup_close",
            ExecutionConfirmationRules.CloseAbovePreviousHigh
                when previousBar is null || bar.Close <= previousBar.High =>
                "confirmation_close_not_above_previous_high",
            ExecutionConfirmationRules.CloseBelowPreviousLow
                when previousBar is null || bar.Close >= previousBar.Low =>
                "confirmation_close_not_below_previous_low",
            _ => null
        };
        if (priceFailure is not null)
        {
            return priceFailure;
        }

        var trendFailure = Normalize(rules.TrendFilter) switch
        {
            ExecutionConfirmationRules.NoFilter => null,
            ExecutionConfirmationRules.Ema10AboveEma20
                when snapshot.Ema10 is null ||
                     snapshot.Ema20 is null ||
                     snapshot.Ema10 <= snapshot.Ema20 =>
                "confirmation_ema10_not_above_ema20",
            ExecutionConfirmationRules.Ema10BelowEma20
                when snapshot.Ema10 is null ||
                     snapshot.Ema20 is null ||
                     snapshot.Ema10 >= snapshot.Ema20 =>
                "confirmation_ema10_not_below_ema20",
            _ => null
        };
        if (trendFailure is not null)
        {
            return trendFailure;
        }

        var momentumFailure = Normalize(rules.MomentumFilter) switch
        {
            ExecutionConfirmationRules.NoFilter => null,
            ExecutionConfirmationRules.MacdHistogramPositive
                when snapshot.MacdHistogram is null ||
                     snapshot.MacdHistogram <= 0m =>
                "confirmation_macd_histogram_not_positive",
            ExecutionConfirmationRules.MacdHistogramNegative
                when snapshot.MacdHistogram is null ||
                     snapshot.MacdHistogram >= 0m =>
                "confirmation_macd_histogram_not_negative",
            _ => null
        };
        if (momentumFailure is not null)
        {
            return momentumFailure;
        }

        var closeLocation = ComputeCloseLocationValue(bar);
        if (rules.MinCloseLocationValue is { } minimum &&
            (closeLocation is null || closeLocation < minimum))
        {
            return "confirmation_close_location_below_minimum";
        }

        if (rules.MaxCloseLocationValue is { } maximum &&
            (closeLocation is null || closeLocation > maximum))
        {
            return "confirmation_close_location_above_maximum";
        }

        return null;
    }

    private static string? ValidateAlignment(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots)
    {
        if (bars.Count == 0 || snapshots.Count == 0)
        {
            return "execution_market_state_empty";
        }

        if (bars.Count != snapshots.Count)
        {
            return "execution_bar_snapshot_count_mismatch";
        }

        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp != snapshots[index].Timestamp)
            {
                return "execution_bar_snapshot_timestamp_mismatch";
            }
        }

        return null;
    }

    private static string? ValidateRules(ExecutionConfirmationRules rules)
    {
        if (!IsOneOf(
                rules.PriceFilter,
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.CloseAboveSetupClose,
                ExecutionConfirmationRules.CloseBelowSetupClose,
                ExecutionConfirmationRules.CloseAbovePreviousHigh,
                ExecutionConfirmationRules.CloseBelowPreviousLow))
        {
            return $"unsupported_execution_confirmation_price_filter ({rules.PriceFilter})";
        }

        if (!IsOneOf(
                rules.TrendFilter,
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.Ema10AboveEma20,
                ExecutionConfirmationRules.Ema10BelowEma20))
        {
            return $"unsupported_execution_confirmation_trend_filter ({rules.TrendFilter})";
        }

        if (!IsOneOf(
                rules.MomentumFilter,
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.MacdHistogramPositive,
                ExecutionConfirmationRules.MacdHistogramNegative))
        {
            return $"unsupported_execution_confirmation_momentum_filter ({rules.MomentumFilter})";
        }

        if (rules.MinCloseLocationValue is < 0m or > 1m ||
            rules.MaxCloseLocationValue is < 0m or > 1m ||
            (rules.MinCloseLocationValue is { } minimum &&
             rules.MaxCloseLocationValue is { } maximum &&
             minimum > maximum))
        {
            return "execution_confirmation_close_location_invalid";
        }

        return null;
    }

    private static int FindFirstEligibleIndexAtOrAfter(
        CompletedBarExecutionRequest request,
        DateTimeOffset timestamp)
    {
        for (var index = 0; index < request.ExecutionBars.Count; index++)
        {
            var bar = request.ExecutionBars[index];
            if (bar.Timestamp >= timestamp &&
                request.IsExecutionBarEligible(bar.Timestamp))
            {
                return index;
            }
        }

        return request.ExecutionBars.Count;
    }

    private static int FindLastEligibleIndexAtOrBefore(
        CompletedBarExecutionRequest request,
        DateTimeOffset timestamp)
    {
        for (var index = request.ExecutionBars.Count - 1; index >= 0; index--)
        {
            var bar = request.ExecutionBars[index];
            if (bar.Timestamp <= timestamp &&
                request.IsExecutionBarEligible(bar.Timestamp))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindNextEligibleIndex(
        CompletedBarExecutionRequest request,
        int startIndex)
    {
        for (var index = Math.Max(0, startIndex);
             index < request.ExecutionBars.Count;
             index++)
        {
            if (request.IsExecutionBarEligible(
                    request.ExecutionBars[index].Timestamp))
            {
                return index;
            }
        }

        return request.ExecutionBars.Count;
    }

    private static int FindPreviousEligibleIndex(
        CompletedBarExecutionRequest request,
        int startIndex)
    {
        for (var index = Math.Min(
                 startIndex,
                 request.ExecutionBars.Count - 1);
             index >= 0;
             index--)
        {
            if (request.IsExecutionBarEligible(
                    request.ExecutionBars[index].Timestamp))
            {
                return index;
            }
        }

        return -1;
    }

    private static decimal? ComputeCloseLocationValue(OhlcvBar bar)
    {
        var range = bar.High - bar.Low;
        return range <= 0m ? null : (bar.Close - bar.Low) / range;
    }

    private static bool IsOneOf(string value, params string[] allowed)
    {
        var normalized = Normalize(value);
        return allowed.Any(candidate =>
            normalized.Equals(candidate, StringComparison.Ordinal));
    }

    private static string Normalize(string? value) =>
        String.IsNullOrWhiteSpace(value)
            ? ExecutionConfirmationRules.NoFilter
            : value.Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .ToLowerInvariant();
}
