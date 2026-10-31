using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

public sealed record PreviousSessionBreakoutContext(
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> SessionSchedules,
    decimal TickSize,
    DateTimeOffset CalendarEvidenceAsOfUtc,
    string? TickEvidenceId = null,
    string CalendarAvailabilityEvidence = "caller_supplied_schedule;original_calendar_receipt_not_asserted",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OhlcvBar>? OpeningRangeBaselineBars = null);

public sealed record PreviousSessionBreakoutSignal(
    string Direction,
    string EntryPattern,
    decimal LimitPrice,
    decimal InitialStopPrice,
    decimal TickSize,
    decimal PreviousSessionHigh,
    decimal PreviousSessionLow,
    DateTimeOffset BreakoutTimestampUtc,
    DateTimeOffset ConfirmationTimestampUtc,
    DateTimeOffset ActiveAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset SessionDeadlineUtc,
    int BreakoutIndex,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] IntradayEntryOrderMode EntryOrderMode = IntradayEntryOrderMode.NextBarLimit,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? OpeningRangeHigh = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? OpeningRangeLow = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? OpeningRangeOpen = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? OpeningRangeClose = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OpeningRangeCompletedAtUtc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? OpeningRelativeVolume = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? OpeningVolumeBaselineSampleCount = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int MaxFilledParentTradesPerSession = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RequireRangeReturnBeforeReentry = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OpeningVolumeBaselineSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? BreakoutTriggerPrice = null);

public sealed record PreviousSessionBreakoutEvaluation(
    PreviousSessionBreakoutSignal? Signal,
    string? RejectionReason,
    bool HasBreakout = false);

/// <summary>Pure completed-bar signal calculation shared by decision kernels and execution adapters.</summary>
public sealed class PreviousSessionBreakoutEvaluator
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Select exactly one B/C pattern using only completed evidence and authoritative regular-session bounds.
    public PreviousSessionBreakoutEvaluation Evaluate(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> completedBars,
        PreviousSessionBreakoutContext context,
        DateTimeOffset asOfUtc,
        DateTimeOffset? resolvedAfterUtc = null)
    {
        if (strategy.EntryRules.PreviousSessionBreakout is not { } policy)
            return Reject("previous_session_breakout_not_declared");
        policy.Validate(strategy);
        if (context.TickSize <= 0m || context.CalendarEvidenceAsOfUtc == default || context.CalendarEvidenceAsOfUtc > asOfUtc ||
            context.CalendarEvidenceAsOfUtc.Offset != TimeSpan.Zero)
            return Reject("previous_session_tick_or_calendar_unavailable");
        if (completedBars.Count < 3) return Reject("previous_session_completed_bars_unavailable");
        var confirmation = completedBars[^1];
        var breakout = completedBars[^2];
        var preceding = completedBars[^3];
        if (!CompletedSeriesIsValid(completedBars, asOfUtc))
            return Reject("previous_session_bar_evidence_invalid");
        if (confirmation.Timestamp != breakout.Timestamp.Add(Interval) ||
            confirmation.Timestamp.Add(Interval) != asOfUtc)
            return Reject("confirmation_not_immediate_completed_candle");
        if (resolvedAfterUtc is { } resolved && breakout.Timestamp < resolved)
            return Reject("breakout_precedes_prior_order_resolution");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(confirmation.Timestamp, zone).DateTime);
        if (!context.SessionSchedules.TryGetValue(date, out var session) || !session.IsTradingDay)
            return Reject("current_exchange_session_unavailable");
        var open = AtUtc(session.TradeDate, session.RegularOpen, zone);
        var close = AtUtc(session.TradeDate, session.RegularClose, zone);
        var deadline = close.Subtract(Interval);
        if (breakout.Timestamp < open || asOfUtc >= deadline)
            return Reject("outside_day_entry_window");
        if (!TryPreviousSession(context.SessionSchedules, date, out var previousSession))
            return Reject("previous_exchange_session_unavailable");
        var previousOpen = AtUtc(previousSession.TradeDate, previousSession.RegularOpen, zone);
        var previousClose = AtUtc(previousSession.TradeDate, previousSession.RegularClose, zone);
        var previousBars = completedBars.Where(bar => bar.Timestamp >= previousOpen && bar.Timestamp < previousClose).ToArray();
        if (!HasCompleteCoverage(previousBars, previousOpen, previousClose))
            return Reject("previous_session_five_minute_coverage_incomplete");
        if (breakout.Timestamp == open) preceding = previousBars[^1];
        if (preceding.Timestamp != breakout.Timestamp.Subtract(Interval) &&
            !(breakout.Timestamp == open && preceding.Timestamp == previousClose.Subtract(Interval)))
            return Reject("breakout_previous_close_missing");
        var high = previousBars.Max(bar => bar.High);
        var low = previousBars.Min(bar => bar.Low);
        var isLong = Allows(strategy.Direction, "long") && preceding.Close <= high && breakout.Close > high;
        var isShort = Allows(strategy.Direction, "short") && preceding.Close >= low && breakout.Close < low;
        if (!isLong && !isShort) return Reject("breakout_or_immediate_confirmation_not_satisfied");
        if (isLong ? confirmation.Close <= breakout.Close : confirmation.Close >= breakout.Close)
            return Reject("breakout_or_immediate_confirmation_not_satisfied", true);
        var level = isLong ? high : low;
        var extreme = isLong ? confirmation.Low : confirmation.High;
        var stop = isLong ? breakout.Low : breakout.High;
        var tick = context.TickSize;
        string pattern;
        decimal limit;
        if ((isLong && stop < extreme && extreme < level - tick) ||
            (!isLong && level + tick < extreme && extreme < stop))
        {
            pattern = "deep_pullback";
            limit = extreme;
        }
        else if (Math.Abs(extreme - level) <= tick && (isLong ? level > stop : level < stop))
        {
            pattern = "retest";
            limit = level;
        }
        else if ((isLong && extreme > level + tick && extreme > stop) ||
                 (!isLong && extreme < level - tick && extreme < stop))
        {
            pattern = "shallow_pullback";
            limit = extreme;
        }
        else return Reject("confirmation_pullback_pattern_not_satisfied", true);
        // A buy limit rounds down and a sell limit rounds up; neither normalization pays beyond the selected limit.
        limit = (isLong ? Math.Floor(limit / tick) : Math.Ceiling(limit / tick)) * tick;
        // Preserve the exact structural stop; an incompatible tick cannot be repaired by tightening it.
        if (stop % tick != 0m || (isLong ? limit <= stop : limit >= stop))
            return Reject("entry_or_structural_stop_incompatible_with_tick", true);
        return new(new PreviousSessionBreakoutSignal(isLong ? "long" : "short", pattern, limit, stop,
            tick, high, low, breakout.Timestamp, confirmation.Timestamp, asOfUtc,
            asOfUtc.Add(Interval) < deadline ? asOfUtc.Add(Interval) : deadline,
            deadline, completedBars.Count - 2), null, true);
    }

    // Validate provenance, chronology and availability before using any completed close in the crossing.
    internal static bool CompletedSeriesIsValid(IReadOnlyList<OhlcvBar> bars, DateTimeOffset asOfUtc)
    {
        var first = bars[0];
        for (var index = 0; index < bars.Count; index++)
        {
            var bar = bars[index];
            if (bar.Timeframe != "5m" || bar.Timestamp.Offset != TimeSpan.Zero ||
                bar.Timestamp.Add(Interval) > asOfUtc || bar.KnownAtUtc > asOfUtc ||
                bar.Ticker != first.Ticker || bar.DataFeed != first.DataFeed ||
                bar.AdjustmentPolicy != first.AdjustmentPolicy ||
                bar.Low <= 0m || bar.Low > Math.Min(bar.Open, bar.Close) ||
                bar.High < Math.Max(bar.Open, bar.Close) || bar.Volume < 0m ||
                index > 0 && bars[index - 1].Timestamp >= bar.Timestamp)
                return false;
        }
        return true;
    }

    // Require every expected five-minute interval, including the last interval of an early-close session.
    internal static bool HasCompleteCoverage(IReadOnlyList<OhlcvBar> bars, DateTimeOffset open, DateTimeOffset close)
    {
        if (bars.Count == 0 || close <= open || (close - open).Ticks % Interval.Ticks != 0 ||
            bars.Count != (close - open).Ticks / Interval.Ticks) return false;
        for (var index = 0; index < bars.Count; index++)
            if (bars[index].Timestamp != open.AddTicks(Interval.Ticks * index)) return false;
        return true;
    }

    // A missing calendar date is missing evidence, never permission to skip to a convenient older session.
    internal static bool TryPreviousSession(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar,
        DateOnly current, out MarketSessionSchedule session)
    {
        for (var date = current.AddDays(-1); calendar.TryGetValue(date, out var candidate); date = date.AddDays(-1))
        {
            if (candidate.TradeDate != date) break;
            if (candidate.IsTradingDay) { session = candidate; return true; }
        }
        session = null!;
        return false;
    }

    // Convert the explicit calendar wall time using the historical exchange timezone for that session date.
    internal static DateTimeOffset AtUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    // Retain the repository's existing both-direction spelling while accepting the explicit both alias.
    private static bool Allows(string configured, string direction) =>
        configured.Equals(direction, StringComparison.OrdinalIgnoreCase) || configured is "long_short" or "both";

    // Return a named failure without inventing levels or retaining an earlier confirmation.
    private static PreviousSessionBreakoutEvaluation Reject(string reason, bool hasBreakout = false) => new(null, reason, hasBreakout);
}
