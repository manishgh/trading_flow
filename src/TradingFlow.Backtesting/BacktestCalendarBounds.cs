using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>Limits day replay calendars to source dates that can occur before their exclusive execution cutoff.</summary>
public static class BacktestCalendarBounds
{
    /// <summary>Returns the final exchange date for an intraday replay; other consumers retain their existing calendar buffers.</summary>
    public static DateOnly? IntradayLastRequiredDate(BacktestRunConfig run, DateTimeOffset exclusiveEndUtc, string exchangeTimezone)
    {
        if (run.Engine.ExecutionReplayPolicyId is not (ExecutionReplayPolicy.IntradayRthFiveMinuteV1 or
            ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 or ExecutionReplayPolicy.PythonZoneBounceBreakdownV1 or ExecutionReplayPolicy.PythonZoneVolumeSpreadV1))
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(exchangeTimezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(exclusiveEndUtc.AddTicks(-1), zone).DateTime);
    }
}
