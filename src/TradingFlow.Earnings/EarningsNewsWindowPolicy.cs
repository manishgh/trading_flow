namespace TradingFlow.Earnings;

public sealed record EarningsNewsWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Label);

/// <summary>
/// Defines the evidence window in the operator's configured local time, then converts its
/// boundaries to UTC for repository queries. The extended weekend window keeps
/// Friday evening context visible until Monday afternoon.
/// </summary>
public static class EarningsNewsWindowPolicy
{
    private static readonly TimeOnly WeekendStart = new(22, 0);
    private static readonly TimeOnly WeekendEnd = new(13, 0);

    public static EarningsNewsWindow Resolve(DateTimeOffset nowUtc) =>
        Resolve(nowUtc, EarningsTimeZones.OperatorLocal);

    public static EarningsNewsWindow Resolve(DateTimeOffset nowUtc, TimeZoneInfo operatorTimeZone)
    {
        ArgumentNullException.ThrowIfNull(operatorTimeZone);
        var endUtc = nowUtc.ToUniversalTime();
        var operatorNow = TimeZoneInfo.ConvertTime(endUtc, operatorTimeZone);
        var localDate = DateOnly.FromDateTime(operatorNow.DateTime);
        var localTime = TimeOnly.FromDateTime(operatorNow.DateTime);

        var useWeekendWindow = operatorNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
            (operatorNow.DayOfWeek == DayOfWeek.Friday && localTime >= WeekendStart) ||
            (operatorNow.DayOfWeek == DayOfWeek.Monday && localTime <= WeekendEnd);
        if (!useWeekendWindow)
        {
            return new EarningsNewsWindow(endUtc.AddHours(-24), endUtc, "Latest 24 hours");
        }

        var daysSinceFriday = ((int)operatorNow.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        var friday = localDate.AddDays(-daysSinceFriday);
        var localStart = friday.ToDateTime(WeekendStart, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, operatorTimeZone);
        return new EarningsNewsWindow(
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            endUtc,
            "Friday 22:00 to Monday 13:00 local time");
    }
}
