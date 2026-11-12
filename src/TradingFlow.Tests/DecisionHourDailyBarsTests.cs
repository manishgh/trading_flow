using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

/// <summary>
/// The decision-hour primitive on synthetic bars. TEST rises 1.00 a session for 30 sessions to 129 on 1 October 2026; on
/// 2 October it opens at 129, falls to 105 by 15:00 New York (the 14:00 hourly bar closes at 105) and recovers to close at 131.
/// </summary>
public sealed class DecisionHourDailyBarsTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly Day = new(2026, 10, 2);

    // The decision reads the 15:00 price: RSI(2) on prior closes plus 105 is below 5, while the final 16:00 close of 131 would
    // give an RSI(2) far above it; the provisional bar is known at 15:00 and the order fills on the 15:00-16:00 bar.
    [Fact]
    public void Decision_UsesTheFifteenHundredCloseAndFillsOnTheLastHourBar()
    {
        var (decision, reason) = DecisionHourDailyBars.Build(Daily(Day, 129m, 131m), Hourly(Day, 16), MarketSessionSchedule.RegularDay(Day), NewYork);

        Assert.Null(reason);
        Assert.Equal((105m, At(Day, 15)), (decision!.ProvisionalDailyBar.Close, decision.ProvisionalDailyBar.KnownAtUtc));
        Assert.Equal((At(Day, 15), 105m), (decision.LastHourBar.Timestamp, decision.LastHourBar.Open));
        Assert.Equal(129m, decision.ProvisionalDailyBar.High);
        var engine = new IndicatorEngine();
        var atDecision = DecisionHourDailyBars.ProvisionalSnapshot(engine, Prior(), decision.ProvisionalDailyBar);
        var atFinalClose = engine.Compute([.. Prior(), Daily(Day, 129m, 131m)])[^1];
        Assert.True(atDecision.Rsi2 < 5m, $"RSI(2) at 15:00 was {atDecision.Rsi2}");
        Assert.True(atFinalClose.Rsi2 > 50m, $"RSI(2) at 16:00 was {atFinalClose.Rsi2}");
        Assert.Equal(Prior().TakeLast(4).Sum(bar => bar.Close) / 5m + 105m / 5m, atDecision.Sma5);
    }

    // On a half day (13:00 close) the decision is at 12:00 on the 11:00 bar's close and the fill is on the 12:00-13:00 bar.
    [Fact]
    public void HalfDay_DecidesOneHourBeforeTheEarlyClose()
    {
        var halfDay = new MarketSessionSchedule(Day, true, new TimeOnly(9, 30), new TimeOnly(13, 0));
        var (decision, _) = DecisionHourDailyBars.Build(Daily(Day, 129m, 131m), Hourly(Day, 13), halfDay, NewYork);

        Assert.Equal((At(Day, 12), At(Day, 12)), (decision!.DecisionTimeUtc, decision.LastHourBar.Timestamp));
        Assert.Equal(Hourly(Day, 13).Single(bar => bar.Timestamp == At(Day, 11)).Close, decision.ProvisionalDailyBar.Close);
    }

    // A missing decision bar or last-hour bar gives a named skip reason instead of a decision.
    [Fact]
    public void MissingHourlyBars_GiveNamedSkipReasons()
    {
        var session = MarketSessionSchedule.RegularDay(Day);
        var withoutDecision = Hourly(Day, 16).Where(bar => bar.Timestamp != At(Day, 14)).ToArray();
        var withoutLastHour = Hourly(Day, 16).Where(bar => bar.Timestamp != At(Day, 15)).ToArray();

        Assert.Equal((null, DecisionHourDailyBars.DecisionBarMissingReason),
            DecisionHourDailyBars.Build(Daily(Day, 129m, 131m), withoutDecision, session, NewYork));
        Assert.Equal((null, DecisionHourDailyBars.LastHourBarMissingReason),
            DecisionHourDailyBars.Build(Daily(Day, 129m, 131m), withoutLastHour, session, NewYork));
    }

    // Thirty rising sessions before the decision day, at New York midnight.
    private static OhlcvBar[] Prior() => Enumerable.Range(0, 30).Select(i => Daily(Day.AddDays(-30 + i), 100m + i, 100m + i)).ToArray();

    private static OhlcvBar Daily(DateOnly day, decimal open, decimal close) => new("TEST", Midnight(day), "1d", open,
        Math.Max(open, close) + 1m, Math.Min(open, close) - 1m, close, 1_000_000m, "sip", "raw");

    // Clock-aligned hourly bars from 10:00 to the hour before the close: a fall to 105 by 15:00, then a recovery.
    private static OhlcvBar[] Hourly(DateOnly day, int closeHour) => Enumerable.Range(10, closeHour - 10).Select(hour =>
    {
        var close = hour < 14 ? 129m - (hour - 9) * 2m : hour == 14 ? 105m : 131m;
        var open = hour == 10 ? 129m : hour <= 14 ? 129m - (hour - 10) * 2m : 105m;
        return new OhlcvBar("TEST", At(day, hour), "1h", open, Math.Max(open, close), Math.Min(open, close), close, 100_000m, "sip", "raw");
    }).ToArray();

    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), NewYork), TimeSpan.Zero);

    private static DateTimeOffset Midnight(DateOnly day) => At(day, 0);
}
