using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Sessions;

public sealed class StrategySessionClock
{
    private static readonly TimeSpan OpenTime = new(9, 30, 0);
    private static readonly TimeSpan CloseTime = new(16, 0, 0);

    public bool ValidateExecutionWindow(DateTime exchangeTime, string timeframe, bool vetoFridayWeekend)
    {
        if (exchangeTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var timeOfDay = exchangeTime.TimeOfDay;
        if (timeOfDay < OpenTime || timeOfDay > CloseTime)
        {
            return false;
        }

        if (timeframe.Equals("15m", StringComparison.OrdinalIgnoreCase) &&
            timeOfDay < OpenTime.Add(TimeSpan.FromMinutes(30)))
        {
            return false;
        }

        var restrictionMinutes = timeframe.Equals("15m", StringComparison.OrdinalIgnoreCase) ? 60.0 : 0.0;
        if (exchangeTime.DayOfWeek == DayOfWeek.Friday &&
            vetoFridayWeekend &&
            timeframe.Equals("15m", StringComparison.OrdinalIgnoreCase))
        {
            restrictionMinutes = 120.0;
        }

        var cutOffTime = CloseTime.Subtract(TimeSpan.FromMinutes(restrictionMinutes));
        return timeOfDay < cutOffTime;
    }

    public bool ValidateExecutionWindow(DateTimeOffset timestamp, string timeframe, SessionRules sessionRules)
    {
        var exchangeTime = ConvertToExchangeTime(timestamp, sessionRules.ExchangeTimezone).DateTime;
        
        // 1. Continuous Markets (e.g. Crypto) bypass weekend/EOD checks
        if (sessionRules.IsContinuousMarket)
        {
            return true;
        }

        if (exchangeTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var timeOfDay = exchangeTime.TimeOfDay;
        if (timeOfDay < OpenTime || timeOfDay > CloseTime)
        {
            return false;
        }

        if (timeOfDay < OpenTime.Add(TimeSpan.FromMinutes(sessionRules.QuietMinutesAfterOpen)))
        {
            return false;
        }

        // 2. Standard End-of-Day Cutoff
        var restrictionMinutes = exchangeTime.DayOfWeek == DayOfWeek.Friday
            ? sessionRules.FridayCloseBufferMinutes
            : sessionRules.CloseBufferMinutes;

        // If Friday rules are defined, we restrict trades severely
        if (exchangeTime.DayOfWeek == DayOfWeek.Friday && sessionRules.FridayCloseBufferMinutes > 0)
        {
            // Usually we stop Friday entries around 2 PM (120 min before close)
            restrictionMinutes = Math.Max(restrictionMinutes, 120);
        }

        var cutOffTime = CloseTime.Subtract(TimeSpan.FromMinutes(restrictionMinutes));
        return timeOfDay < cutOffTime;
    }

    private static DateTimeOffset ConvertToExchangeTime(DateTimeOffset timestamp, string timezoneId)
    {
        var zone = ResolveTimezone(timezoneId);
        return TimeZoneInfo.ConvertTime(timestamp, zone);
    }

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
