using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Tests;

public sealed class StrategySessionClockTests
{
    [Fact]
    public void ValidateExecutionWindow_DailyBarOnWeekday_DoesNotApplySubDailyClock()
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
    public void ValidateExecutionWindow_SubDailySwingEntryThirtyMinutesBeforeClose_IsRejected()
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
    public void ValidateExecutionWindow_ExtendedHours_AllowsPremarketAndPostmarket()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 1,
            CloseBufferMinutes: 30,
            FridayCloseBufferMinutes: 5,
            UseExtendedHours: true);

        var premarket = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-09T08:05:00Z"),
            "5m",
            session);
        var postmarket = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-09T23:30:00Z"),
            "5m",
            session);

        Assert.True(premarket);
        Assert.True(postmarket);
    }

    [Fact]
    public void ValidateExecutionWindow_RegularHours_StillRejectsPremarket()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 1,
            CloseBufferMinutes: 30,
            FridayCloseBufferMinutes: 5);

        var accepted = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-09T08:05:00Z"),
            "5m",
            session);

        Assert.False(accepted);
    }

    [Fact]
    public void ValidateExecutionWindow_ExtendedHours_OnlyFridayPostmarketCloseBufferBlocksEntries()
    {
        var clock = new StrategySessionClock();
        var session = new SessionRules(
            "America/New_York",
            QuietMinutesAfterOpen: 0,
            CloseBufferMinutes: 30,
            FridayCloseBufferMinutes: 5,
            UseExtendedHours: true);

        var thursdayPostmarket = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-11T23:58:00Z"),
            "5m",
            session);
        var fridayBeforeCutoff = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-12T23:54:00Z"),
            "5m",
            session);
        var fridayAtCutoff = clock.ValidateExecutionWindow(
            DateTimeOffset.Parse("2026-06-12T23:55:00Z"),
            "5m",
            session);

        Assert.True(thursdayPostmarket);
        Assert.True(fridayBeforeCutoff);
        Assert.False(fridayAtCutoff);
    }

}
