namespace TradingFlow.Earnings;

/// <summary>
/// Resolves the next weekday used by the short-horizon earnings monitor.
/// Provider results remain authoritative; this helper only skips weekends.
/// </summary>
public static class EarningsCalendarDates
{
    public static DateOnly NextWeekday(DateOnly date)
    {
        var candidate = date.AddDays(1);
        while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }
}
