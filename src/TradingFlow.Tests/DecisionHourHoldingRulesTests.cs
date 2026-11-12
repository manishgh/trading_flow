using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

/// <summary>
/// Research-screen holding rules for a decision-hour holding (fills on hourly bars) in the shared execution coordinator and
/// native ledger, with Connors 7.2.0 (10 bps slippage) holding a stop at 90 and a target at 120. Synthetic bars for TEST on
/// weekday sessions 14 to 25 September 2026: a daily bar at New York midnight and hourly bars from 10:00 to 15:00, flat at
/// 100 plus 0.50 a day except the 15:00 bar, which opens there and closes 0.25 higher. The 500-share position fills on the
/// 15 September last-hour bar.
/// </summary>
public sealed class DecisionHourHoldingRulesTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly First = new(2026, 9, 14);
    private static readonly DateOnly Last = new(2026, 9, 25);
    private static readonly DateOnly EntryDay = new(2026, 9, 15);

    // A split with ex-date 18 September sells at the open of the 17 September last-hour bar, inside that session; the entry,
    // filled at 16:00 on 15 September, is booked to 15 September, where the daily convention would have booked it to the 14th.
    [Fact]
    public void SplitDuringAHold_SellsAtTheLastHourOpenBeforeTheExDate()
    {
        var bars = Bars(_ => true);
        var coordinator = Schedule(bars, Rules([("TEST", new DateOnly(2026, 9, 18))]), exit: null);

        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal((ChronologicalBacktestExecutionCoordinator.SplitExitReason, At(new(2026, 9, 17), 15).AddTicks(1)),
            (trade.ExitReason, trade.ExitTimestamp));
        Assert.Equal(Price(new(2026, 9, 17)) * 0.999m, trade.ExitPrice);
        var state = coordinator.Snapshot(Cutoff);
        Assert.Equal([EntryDay, new DateOnly(2026, 9, 17)], state.Fills.Select(fill => fill.SourceSessionDate));
        Assert.Equal(EntryDay.AddDays(-1), NativeDailySessionLedger.SourceSessionDate(state.Fills[0].TimestampUtc));
        Assert.All(Ledger(state, bars).Sessions, session => Assert.NotNull(session.Equity));
    }

    // Bars stop after 17 September while held: before 18 September's admissions the position is closed at the 17 September
    // last-hour close less slippage, booked to 17 September, and every session stays valued.
    [Fact]
    public void BarsThatNeverResume_CloseAtTheLastHourlyClose()
    {
        var bars = Bars(day => day <= new DateOnly(2026, 9, 17));
        var coordinator = Schedule(bars, Rules([]), exit: null);

        coordinator.AdvanceThrough(At(new(2026, 9, 18), 0));
        Assert.Empty(coordinator.ActiveExecutions);
        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal((ChronologicalBacktestExecutionCoordinator.DataEndedExitReason, (Price(new(2026, 9, 17)) + 0.25m) * 0.999m,
            At(new(2026, 9, 17), 16)), (trade.ExitReason, trade.ExitPrice, trade.ExitTimestamp));
        var state = coordinator.Snapshot(Cutoff);
        Assert.Equal(new DateOnly(2026, 9, 17), state.Fills[^1].SourceSessionDate);
        Assert.All(Ledger(state, bars).Sessions, session => Assert.NotNull(session.Equity));
    }

    // A gap from 17 to 21 September is held through (valued at the 16 September close) and the planned exit fills at the open
    // of the 22 September last-hour bar when bars resume; nothing is forced.
    [Fact]
    public void GapAfterWhichBarsResume_IsHeldAndThePlannedExitFillsOnTheResumedLastHour()
    {
        var gap = new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 21) };
        var bars = Bars(day => !gap.Contains(day));
        var resume = new DateOnly(2026, 9, 22);
        var coordinator = Schedule(bars, Rules([]), exit: At(resume, 15));

        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(("technical_exit_above_sma5", At(resume, 16), Price(resume) * 0.999m), (trade.ExitReason, trade.ExitTimestamp, trade.ExitPrice));
        var ledger = Ledger(coordinator.Snapshot(Cutoff), bars);
        Assert.All(ledger.Sessions, session => Assert.NotNull(session.Equity));
        Assert.Contains(ledger.SessionMarks, mark => mark.SessionDate == gap[0] && mark.Price is null);
    }

    // The rules for the given split ex-dates over the fixture's weekday sessions, with their schedules.
    private static HoldingDataRules Rules(IEnumerable<(string, DateOnly)> splits) =>
        new(Weekdays().ToArray(), splits.ToHashSet()) { Schedules = Calendar() };

    // Schedules one 500-share long filling on the 15 September last-hour bar, with the stop and target 7.2.0 places, and an
    // optional planned exit (a completed close-above-SMA5 decision) on a later last-hour bar.
    private static ChronologicalBacktestExecutionCoordinator Schedule(IReadOnlyCollection<OhlcvBar> bars, HoldingDataRules rules, DateTimeOffset? exit)
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(Portfolio, bars, holdingRules: rules);
        var candidate = new BacktestCandidateTrade("TEST", "Connors 7.2.0", "long", At(EntryDay, 15), 100m, 90m, 120m,
            exit, null, exit is null ? "open_at_study_end" : "technical_exit_above_sma5", 10m, StrategyId: Strategy.StrategyId, CandidateId: "held")
            { FillTimeframe = "1h" };
        Assert.True(coordinator.TrySchedule(new(candidate, Strategy, 500), out var rejection), rejection);
        return coordinator;
    }

    // Values a snapshot with the native ledger over the fixture's calendar under the research-screen rules.
    private static BacktestPortfolioState Ledger(BacktestPortfolioState state, IReadOnlyCollection<OhlcvBar> bars) =>
        NativeDailySessionLedger.Analyze(Portfolio.StartingCapital, state, bars,
            new NativeDailyAccountingContext(At(First, 0), Cutoff, Calendar(), true) { ResearchScreenHoldingRules = true });

    private static readonly PortfolioConfig Portfolio = new(StartingCapital: 100_000m, AccountRiskBudgetPct: 1m, MaxPositionNotionalPct: 100m,
        MaxConcurrentPositions: 10, FixedBuyFee: 0m, FixedSellFee: 0m, MaxOpenTradesPerTicker: 10, PreventOverlappingTickerPositions: true,
        MaxBarParticipationPct: 5m);

    private static readonly TradingFlow.Domain.Strategies.StrategyDefinition Strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(
        TestRepository.FindRoot(), "configs", "backtest", "strategies", "swing-connors-rsi2-sma5-risk-managed-decision-hour.bt-v7-2.yaml"));

    private static DateTimeOffset Cutoff => At(Last.AddDays(1), 0);

    // Weekday sessions of the fixture.
    private static IEnumerable<DateOnly> Weekdays() => Enumerable.Range(First.DayNumber, Last.DayNumber - First.DayNumber + 1)
        .Select(DateOnly.FromDayNumber).Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));

    // Every calendar date of the fixture window, weekends closed.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar() =>
        Enumerable.Range(First.DayNumber, Last.DayNumber - First.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
            day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));

    // TEST's daily and hourly bars on the kept weekdays (see the class summary); 100,000 shares an hourly bar, so the stop and
    // target are never touched and a 5% participation cap allows 5,000 shares.
    private static OhlcvBar[] Bars(Func<DateOnly, bool> keep) => Weekdays().Where(keep).SelectMany(day =>
        new[] { new OhlcvBar("TEST", At(day, 0), "1d", Price(day), Price(day) + 2m, Price(day) - 2m, Price(day) + 0.25m, 600_000m, "sip", "raw") }
            .Concat(Enumerable.Range(10, 6).Select(hour => new OhlcvBar("TEST", At(day, hour), "1h", Price(day), Price(day) + 1m, Price(day) - 1m,
                hour == 15 ? Price(day) + 0.25m : Price(day), 100_000m, "sip", "raw")))).ToArray();

    private static decimal Price(DateOnly day) => 100m + 0.5m * (day.DayNumber - First.DayNumber);

    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), NewYork), TimeSpan.Zero);
}
