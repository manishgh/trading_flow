using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

/// <summary>
/// Resolves exchange dates and the next weekday used by the short-horizon earnings monitor.
/// Provider results remain authoritative; this helper only skips weekends.
/// </summary>
public static class EarningsCalendarDates
{
    public static DateOnly ExchangeDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, EarningsTimeZones.NewYork).DateTime);

    public static DateOnly NextWeekday(DateOnly date) => StepToWeekday(date, 1);

    public static DateOnly PreviousWeekday(DateOnly date) => StepToWeekday(date, -1);

    /// <summary>Moves one day in the given direction (+1 or -1), then keeps going past Saturdays and Sundays.</summary>
    private static DateOnly StepToWeekday(DateOnly date, int direction)
    {
        var candidate = date.AddDays(direction);
        while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            candidate = candidate.AddDays(direction);
        }

        return candidate;
    }

    /// <summary>
    /// When the release window a provider slot names begins: the 16:00 New York close on the report date for an
    /// after-close report, the previous weekday's 16:00 close for a before-open report, and the scheduled time for
    /// any other window. A first sign of the result before this means it came out in an earlier session than the
    /// slot says, as Sony did on 5 Nov 2026; a print a few minutes before a default 16:30 slot does not count.
    /// </summary>
    public static DateTimeOffset ReleaseWindowStartUtc(EarningsCalendarEvent calendarEvent)
    {
        var closeDate = calendarEvent.ReleaseWindow switch
        {
            EarningsReleaseWindow.AfterMarketClose => calendarEvent.ReportDateExchange,
            EarningsReleaseWindow.BeforeMarketOpen => PreviousWeekday(calendarEvent.ReportDateExchange),
            _ => (DateOnly?)null
        };
        return closeDate is { } date
            ? TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(16, 0)), EarningsTimeZones.NewYork)
            : calendarEvent.ScheduledAtUtc;
    }

    /// <summary>
    /// The session that trades the report: the report date itself, or the next weekday for a release
    /// after the close. An after-close report stays live through that following session.
    /// </summary>
    public static bool IsTradingSession(DateOnly reportDate, EarningsReleaseWindow releaseWindow, DateOnly exchangeDate) =>
        reportDate == exchangeDate ||
        releaseWindow == EarningsReleaseWindow.AfterMarketClose && NextWeekday(reportDate) == exchangeDate;
}
