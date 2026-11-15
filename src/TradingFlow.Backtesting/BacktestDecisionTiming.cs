using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

/// <summary>Frozen setup validity; future stock observations never determine its deadline.</summary>
internal static class BacktestDecisionTiming
{
    // Report the actual source close separately from the replay's existing availability and receipt clocks.
    public static BacktestExitEvidence DescribeCompletedExit(OhlcvBar source, StrategyDefinition strategy,
        IReadOnlyList<BacktestExitCondition> conditions, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        var duration = TimeframeParser.Parse(strategy.Execution.Timeframe);
        var completed = source.Timestamp.Add(duration);
        var available = source.KnownAtUtc is { } receipt && receipt > completed ? receipt : completed;
        var close = duration < TimeSpan.FromDays(1) || strategy.Session.IsContinuousMarket
            ? completed
            : ObservedDailyClose(source.Timestamp, duration, strategy.Session, schedules);
        return new(source.Timestamp.ToUniversalTime(), close?.ToUniversalTime(), available.ToUniversalTime(), conditions.ToArray());
    }

    // Exchange-traded daily bars can be stamped at midnight. Their close requires an authoritative session.
    private static DateTimeOffset? ObservedDailyClose(DateTimeOffset source, TimeSpan duration, SessionRules session,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (duration != TimeSpan.FromDays(1) || schedules is null || session.UseExtendedHours)
            return null;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(source, TimeZoneInfo.FindSystemTimeZoneById(session.ExchangeTimezone)).DateTime);
        return schedules.TryGetValue(day, out var schedule) && schedule.TradeDate == day && schedule.IsTradingDay &&
            schedule.RegularClose > schedule.RegularOpen
            ? FineExecutionTiming.SessionClose(source, session.ExchangeTimezone, schedules)
            : null;
    }

    // With an unknown close, require entry by source start. Exit must reach evidence availability even when its
    // exact time is unknown, in which case the caller supplies the earliest instant in the fill range.
    public static BacktestExitEvidence? EvidenceWhileHeld(BacktestExitEvidence? evidence, DateTimeOffset? entryAt,
        DateTimeOffset? earliestExitAt) =>
        evidence is not null && entryAt <= (evidence.DecisionCloseUtc ?? evidence.DecisionSourceStartUtc) &&
            earliestExitAt >= evidence.AvailableAtUtc
            ? evidence
            : null;

    public static DateTimeOffset ValidateTerminalOpen(DateTimeOffset requested, DateTimeOffset start,
        DateTimeOffset end, string timeframe, SessionRules session,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (requested.Offset != TimeSpan.Zero || timeframe != "1d" || session.IsContinuousMarket || schedules is null)
            throw new InvalidOperationException("Terminal-open research requires UTC, native daily slices and an authoritative calendar.");
        var zone=TimeZoneInfo.FindSystemTimeZoneById(session.ExchangeTimezone);
        var first=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start,zone).DateTime);
        var last=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end,zone).DateTime);
        DateTimeOffset? latest=null;
        for(var day=first;day<=last;day=day.AddDays(1))
        {
            if(!schedules.TryGetValue(day,out var known)||known.TradeDate!=day)
                throw new InvalidOperationException($"Missing terminal calendar date {day}.");
            var local=day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
            var source=new DateTimeOffset(local,zone.GetUtcOffset(local)).ToUniversalTime();
            if(known.IsTradingDay&&source>=start&&source.AddDays(1)<=end)latest=source;
        }
        if(latest is null||requested!=latest)
            throw new InvalidOperationException("Terminal open must be the preregistered last authoritative completed daily source session.");
        return requested;
    }

    // Keep the backtest's end-of-session-day expiry using the shared session lookup.
    public static DateTimeOffset ResolveExpiry(DateTimeOffset available, string timeframe, SessionRules session,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        var duration = TimeframeParser.Parse(timeframe);
        // Continuous markets and deterministic CSV fixtures have a fixed bar-duration horizon.
        if (session.IsContinuousMarket || timeframe != "1d" || schedules is null) return available.Add(duration);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(session.ExchangeTimezone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(available, zone).DateTime);
        var next = ResolveScheduledSession(day, schedules);
        // Daily bars become available at the next exchange midnight. One scheduled session is allowed.
        var local = next.TradeDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    // An absent date does not prove the exchange was closed, even if later dates are present.
    public static MarketSessionSchedule ResolveScheduledSession(
        DateOnly first, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        for (var offset = 0; offset < 31; offset++)
        {
            var date = first.AddDays(offset);
            if (!schedules.TryGetValue(date, out var known) || known.TradeDate != date)
                throw new InvalidOperationException($"Authoritative exchange calendar missing {date}; setup expiry is unavailable.");
            if (known.IsTradingDay)
                return known;
        }
        throw new InvalidOperationException("No authoritative next exchange session for setup expiry.");
    }
}
