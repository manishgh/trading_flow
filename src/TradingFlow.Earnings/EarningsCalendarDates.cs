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
    /// The session that trades the report: the report date itself, or the next weekday for a release
    /// after the close. An after-close report stays live through that following session.
    /// </summary>
    public static bool IsTradingSession(DateOnly reportDate, EarningsReleaseWindow releaseWindow, DateOnly exchangeDate) =>
        reportDate == exchangeDate ||
        releaseWindow == EarningsReleaseWindow.AfterMarketClose && NextWeekday(reportDate) == exchangeDate;
}
