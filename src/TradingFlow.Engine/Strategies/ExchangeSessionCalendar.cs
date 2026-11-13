using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>Reads New York session dates and the next trading session from an authoritative exchange calendar.</summary>
public static class ExchangeSessionCalendar
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // The New York trading date of a bar that starts at this instant.
    public static DateOnly SessionDate(DateTimeOffset barStart) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(barStart, NewYork).DateTime);

    /// <summary>
    /// The first trading day after <paramref name="session"/>. Every calendar date in between must be present, so a gap in the
    /// calendar is reported as missing evidence instead of being read as a month boundary. Looks at most 14 days ahead.
    /// </summary>
    public static DateOnly NextTradingSession(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateOnly session)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        for (var date = session.AddDays(1); date <= session.AddDays(14); date = date.AddDays(1))
        {
            if (!calendar.TryGetValue(date, out var schedule))
                throw new InvalidDataException($"The exchange calendar has no entry for {date:yyyy-MM-dd}, so the session after {session:yyyy-MM-dd} is unknown.");
            if (schedule.IsTradingDay) return date;
        }
        throw new InvalidDataException($"The exchange calendar shows no trading session within 14 days after {session:yyyy-MM-dd}.");
    }
}
