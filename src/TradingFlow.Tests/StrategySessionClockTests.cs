using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Tests;

public sealed class StrategySessionClockTests
{
    [Fact]
    public void ValidateExecutionWindow_DailyBarOnWeekday_DoesNotApplyIntradayClock()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 30,
            CloseBufferMinutes: 60,
            FridayCloseBufferMinutes: 120);

        var accepted = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-04-23T00:00:00Z"),
            "1d",
            session);

        Assert.True(accepted);
    }

    [Fact]
    public void ValidateExecutionWindow_DailyBarOnWeekend_IsRejected()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 30,
            CloseBufferMinutes: 60,
            FridayCloseBufferMinutes: 120);

        var accepted = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-04-25T16:00:00Z"),
            "1d",
            session);

        Assert.False(accepted);
    }

    [Fact]
    public void ValidateExecutionWindow_IntradayEntryThirtyMinutesBeforeClose_IsRejected()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 15,
            CloseBufferMinutes: 30,
            FridayCloseBufferMinutes: 30);

        var acceptedAtThreeTwentyNine = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-09T19:29:00Z"),
            "5m",
            session);
        var acceptedAtThreeThirty = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-09T19:30:00Z"),
            "5m",
            session);

        Assert.True(acceptedAtThreeTwentyNine);
        Assert.False(acceptedAtThreeThirty);
    }

    [Fact]
    public void ShouldFlattenBeforeSessionClose_IntradayFiveMinuteBar_FiresOnLastRegularBar()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 15,
            CloseBufferMinutes: 30,
            FridayCloseBufferMinutes: 30);

        var notYet = clock.ShouldFlattenBeforeSessionClose(
            DateTimeOffset.Parse("2026-06-09T19:50:00Z"),
            "5m",
            session);
        var flatten = clock.ShouldFlattenBeforeSessionClose(
            DateTimeOffset.Parse("2026-06-09T19:55:00Z"),
            "5m",
            session);

        Assert.False(notYet);
        Assert.True(flatten);
    }
}
