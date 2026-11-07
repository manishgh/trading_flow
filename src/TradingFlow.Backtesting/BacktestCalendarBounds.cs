using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>Limits day replay calendars to source dates that can occur before their exclusive execution cutoff.</summary>
public static class BacktestCalendarBounds
{
    /// <summary>Returns the final exchange date for an intraday replay; other consumers retain their existing calendar buffers.</summary>
    public static DateOnly? IntradayLastRequiredDate(BacktestRunConfig run, DateTimeOffset exclusiveEndUtc, string exchangeTimezone)
    {
        if (!ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(exchangeTimezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(exclusiveEndUtc.AddTicks(-1), zone).DateTime);
    }
}
