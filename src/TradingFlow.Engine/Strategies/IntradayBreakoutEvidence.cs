using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>Exchange calendar, tick size and opening-volume baseline evidence supplied to the opening-range breakout evaluator.</summary>
public sealed record IntradayBreakoutContext(
    IReadOnlyDictionary<DateOnly, MarketSessionSchedule> SessionSchedules,
    decimal TickSize,
    DateTimeOffset CalendarEvidenceAsOfUtc,
    string? TickEvidenceId = null,
    string CalendarAvailabilityEvidence = "caller_supplied_schedule;original_calendar_receipt_not_asserted",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OhlcvBar>? OpeningRangeBaselineBars = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OhlcvBar>? PriorSessionDailyBars = null);

/// <summary>Frozen completed-candle breakout order intent, including the previous-session range and opening-range evidence.</summary>
public sealed record IntradayBreakoutSignal(
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
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? BreakoutTriggerPrice = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? DailyAtr = null);

/// <summary>Evaluator outcome: a signal, or a named rejection reason and whether a breakout was seen.</summary>
public sealed record IntradayBreakoutEvaluation(
    IntradayBreakoutSignal? Signal,
    string? RejectionReason,
    bool HasBreakout = false);

/// <summary>Completed five-minute bar and exchange-calendar checks shared by intraday breakout evaluators.</summary>
public static class IntradayFiveMinuteBars
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
}
