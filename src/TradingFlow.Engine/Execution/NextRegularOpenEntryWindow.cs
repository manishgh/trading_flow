using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Engine.Execution;

/// <summary>A causal submission window bound to the setup's source session, never its receipt or host launch.</summary>
public sealed record NextRegularOpenEntryWindow(DateTimeOffset OpenUtc, DateTimeOffset EndUtc)
{
    // A submitted order may fill later; this predicate only authorizes a new command.
    public bool Contains(DateTimeOffset timestamp) => timestamp >= OpenUtc && timestamp < EndUtc;

    // Require every intervening calendar date and resolve DST at each session's local clock.
    public static NextRegularOpenEntryWindow? Resolve(StrategyDefinition strategy, DateTimeOffset setupSourceUtc,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        if (strategy.Execution.EntryWindow is not { } policy) return null;
        policy.Validate(strategy);
        if (schedules is null) throw new InvalidDataException("Entry window requires an authoritative exchange calendar.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var sourceDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(setupSourceUtc, zone).DateTime);
        if (!schedules.TryGetValue(sourceDate, out var source) || source.TradeDate != sourceDate || !source.IsTradingDay)
            throw new InvalidDataException("Entry window setup is not a verified source session.");
        var nextDate = ExchangeSessionCalendar.NextTradingSession(schedules, sourceDate);
        for (var date = sourceDate.AddDays(1); date <= nextDate; date = date.AddDays(1))
            if (schedules[date].TradeDate != date)
                throw new InvalidDataException("Entry window calendar date identity is inconsistent.");
        var next = schedules[nextDate];
        if (next.TradeDate != nextDate || next.RegularClose <= next.RegularOpen)
            throw new InvalidDataException("Entry window has an inconsistent exchange session.");
        var open = At(nextDate, next.RegularOpen, zone);
        var close = At(nextDate, next.RegularClose, zone);
        var end = open.AddSeconds(policy.GraceSeconds);
        return new(open, end < close ? end : close);
    }

    // Convert a verified exchange wall-clock boundary to its UTC instant.
    private static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidDataException("Entry window exchange boundary has ambiguous timezone evidence.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
