using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

/// <summary>Limits day replay calendars to source dates that can occur before their exclusive execution cutoff.</summary>
public static class BacktestCalendarBounds
{
    /// <summary>Cover the prior session and prior-week close needed by the sampled replay's initial flat anchor.</summary>
    public static DateOnly CandidateFirstRequiredDate(BacktestRunConfig run, DateTimeOffset dataStartUtc,
        DateTimeOffset evaluationStartUtc)
    {
        var first = DateOnly.FromDateTime(dataStartUtc.UtcDateTime).AddDays(-1);
        if (run.Engine.AccountLossReplay is not { Enabled: true }) return first;
        var priorWeek = AccountLossLimitPolicy.WeekOf(ExchangeSessionCalendar.SessionDate(evaluationStartUtc)).AddDays(-14);
        return priorWeek < first ? priorWeek : first;
    }

    /// <summary>Returns the final exchange date for an intraday replay; other consumers retain their existing calendar buffers.</summary>
    public static DateOnly? IntradayLastRequiredDate(BacktestRunConfig run, DateTimeOffset exclusiveEndUtc, string exchangeTimezone)
    {
        if (!ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
            return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(exchangeTimezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(exclusiveEndUtc.AddTicks(-1), zone).DateTime);
    }
}
