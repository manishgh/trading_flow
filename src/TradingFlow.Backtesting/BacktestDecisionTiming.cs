using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

/// <summary>Frozen setup validity; future stock observations never determine its deadline.</summary>
internal static class BacktestDecisionTiming
{
    public static DateTimeOffset ResolveExpiry(DateTimeOffset available, string timeframe, SessionRules session,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules)
    {
        var duration = TimeframeParser.Parse(timeframe);
        // Continuous markets and deterministic CSV fixtures have a fixed bar-duration horizon.
        if (session.IsContinuousMarket || timeframe != "1d" || schedules is null) return available.Add(duration);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(session.ExchangeTimezone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(available, zone).DateTime);
        MarketSessionSchedule? next = null;
        for (var offset = 0; offset < 31; offset++)
        {
            var date = day.AddDays(offset);
            if (!schedules.TryGetValue(date, out var known) || known.TradeDate != date)
                throw new InvalidOperationException($"Authoritative exchange calendar missing {date}; setup expiry is unavailable.");
            if (known.IsTradingDay) { next = known; break; }
        }
        if (next is null) throw new InvalidOperationException("No authoritative next exchange session for setup expiry.");
        // Daily bars become available at the next exchange midnight. One scheduled session is allowed.
        var local = next.TradeDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
