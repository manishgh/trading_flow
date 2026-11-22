using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class BacktestTestWindowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 30, 0, TimeSpan.Zero);

    // 03:30 UTC is still 21 November in New York; the 180-day start falls in daylight-saving time, the end in winter time.
    [Fact]
    public void RollingProfile_UsesNewYorkCalendarDaysUpToToday()
    {
        var window = BacktestTestWindows.ProfileWindow(new TimeWindowConfig("rolling", 180, null, null, 400), Now);
        Assert.Equal((new DateTimeOffset(2026, 5, 25, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 5, 0, 0, TimeSpan.Zero)), window);
    }

    [Fact]
    public void FixedProfile_UsesItsOwnDatesAndAnEmptyProfileHasNone()
    {
        var start = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal((start.ToUniversalTime(), end), BacktestTestWindows.ProfileWindow(new TimeWindowConfig("fixed", 180, start, end), Now));
        Assert.Null(BacktestTestWindows.ProfileWindow(new TimeWindowConfig("fixed", 0, start, null), Now));
    }

    // Only registered holdout partitions count, each once per strategy and dates with the most-used state recorded for it.
    [Fact]
    public void HeldBackPeriods_ListEachRegisteredHoldoutOnceWithItsMostUsedState()
    {
        var holdout = Partition("holdout", 2025, 2026);
        var ledger = new ResearchLedger(
        [
            Trial("a", "strategy.one", ResearchHoldoutState.Unopened, Partition("development", 2020, 2025), holdout),
            Trial("b", "strategy.one", ResearchHoldoutState.Completed, Partition("development", 2020, 2025), holdout),
            Trial("c", "strategy.two", null, Partition("development", 2020, 2025)),
            Trial("d", "strategy.three", ResearchHoldoutState.Consumed, Partition("development", 2020, 2025), Partition("HOLDOUT", 2025, 2026))
        ], []);

        var periods = BacktestTestWindows.HeldBackPeriods(ledger);
        Assert.Equal(["strategy.one", "strategy.three"], periods.Select(period => period.StrategyId));
        Assert.Equal(ResearchHoldoutState.Completed, periods[0].State);
        Assert.Equal(holdout.StartUtc, periods[0].StartUtc);
        Assert.Equal(holdout.EndUtc, periods[0].EndUtc);
        Assert.Equal("already used, result not published", periods[1].StateLabel);
        Assert.Equal("not opened by a registered trial", new HeldBackTestPeriod("x", Now, Now, ResearchHoldoutState.Unopened).StateLabel);
    }

    // A one-year-aligned partition.
    private static ResearchPartitionDefinition Partition(string name, int startYear, int endYear) => new(name,
        new DateTimeOffset(startYear, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(endYear, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.Zero);

    // A registered trial of one strategy with the given partitions.
    private static ResearchLedgerTrial Trial(string id, string strategyId, ResearchHoldoutState? state, params ResearchPartitionDefinition[] partitions) =>
        new(new ResearchTrialDefinition(id, strategyId, null, 1, "hypothesis", ["dataset"],
            new Dictionary<string, string> { ["market_clock"] = "America/New_York" }, new Dictionary<string, string> { ["rule"] = "x" },
            new Dictionary<string, decimal> { ["slots"] = 1m }, new Dictionary<string, string> { ["costs"] = "0" }, partitions,
            "net", ["none"], ResearchHoldoutState.Unopened, Now), new string('a', 64), state, []);
}
