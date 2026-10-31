using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Web.Services;

/// <summary>Derives calendar-day warmup from the required authoritative completed regular sessions.</summary>
public static class BacktestSessionContextResolver
{
    /// <summary>Uses authoritative session closes and UTC elapsed time, preserving early closes and daylight-saving changes.</summary>
    public static BacktestRunConfig IncludePreviousSession(BacktestRunConfig run, DateTimeOffset startUtc,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, TimeZoneInfo exchangeZone) =>
        IncludeCompletedSessions(run, startUtc, schedules, exchangeZone, 1);

    /// <summary>Includes every immediately preceding baseline session; missing calendar dates cannot be skipped for older substitutes.</summary>
    public static BacktestRunConfig IncludeCompletedSessions(BacktestRunConfig run, DateTimeOffset startUtc,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, TimeZoneInfo exchangeZone, int requiredSessions)
    {
        if (requiredSessions <= 0) throw new ArgumentOutOfRangeException(nameof(requiredSessions));
        var completed = schedules.Values.Where(session => session.IsTradingDay &&
                ToUtc(session.TradeDate, session.RegularClose, exchangeZone) <= startUtc)
            .OrderByDescending(session => session.TradeDate).Take(requiredSessions).ToArray();
        if (completed.Length != requiredSessions)
            throw new InvalidOperationException($"The authoritative exchange calendar contains {completed.Length} completed regular sessions before the test window; {requiredSessions} are required to prepare the strategy's session context.");
        var earliest = completed[^1];
        if (requiredSessions > 1)
        {
            var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(startUtc, exchangeZone).DateTime);
            for (var date = earliest.TradeDate; date <= firstDate; date = date.AddDays(1))
                if (!schedules.ContainsKey(date))
                    throw new InvalidOperationException($"The authoritative exchange calendar is missing {date:yyyy-MM-dd}; opening-volume baseline sessions cannot be substituted with older dates.");
        }
        var openUtc = ToUtc(earliest.TradeDate, earliest.RegularOpen, exchangeZone);
        var days = checked((int)Math.Ceiling((startUtc - openUtc).TotalDays));
        return run with { TimeWindow = run.TimeWindow with { WarmupLookbackDays = Math.Max(0, days) } };
    }

    /// <summary>Converts an exchange-local schedule boundary to UTC without assuming a fixed New York offset.</summary>
    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
