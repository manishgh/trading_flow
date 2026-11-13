using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

/// <summary>
/// Research-screen daily holding rules in the shared execution coordinator and native ledger, with Connors 7.1.0 (as-traded,
/// 10 bps slippage) holding a stop at 90 and a target at 120. Synthetic daily bars at New York midnight for TEST, weekday
/// sessions 14 to 25 September 2026; the 500-share position fills on 15 September from a 100,000-share bar, while every later
/// bar trades only 1,000 shares, so a 5% participation cap (50 shares) could never close it.
/// </summary>
public sealed class ResearchScreenHoldingRulesTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly First = new(2026, 9, 14);
    private static readonly DateOnly Last = new(2026, 9, 25);

    // Bars stop after 17 September while held: by the start of 18 September (before its admissions) the position is closed at
    // the 17 September close less slippage, the stop and target are cancelled, and every session stays valued. Without the
    // rules (any other run) the position stays open and valuation is unavailable from 18 September, as before.
    [Fact]
    public void BarsThatNeverResume_CloseAtTheLastCloseBeforeTheNextSessionsAdmissions()
    {
        var bars = Bars(day => day <= new DateOnly(2026, 9, 17));
        var coordinator = Schedule(bars, Rules([]), exit: null);

        coordinator.AdvanceThrough(Midnight(new(2026, 9, 18)));
        Assert.Empty(coordinator.ActiveExecutions);
        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(ChronologicalBacktestExecutionCoordinator.DataEndedExitReason, trade.ExitReason);
        Assert.Equal((500, Price(new(2026, 9, 17)) * 0.999m, Midnight(new(2026, 9, 18))), (trade.ShareQuantity, trade.ExitPrice, trade.ExitTimestamp));
        var state = coordinator.Snapshot(Cutoff);
        Assert.Empty(state.PendingOrders);
        Assert.Empty(state.OpenPositions);
        Assert.All(Ledger(state, bars, true).Sessions, session => Assert.NotNull(session.Equity));

        var unchanged = Schedule(bars, null, exit: null);
        unchanged.Complete(Cutoff);
        Assert.Empty(unchanged.CompletedTrades);
        Assert.Equal(new DateOnly(2026, 9, 18), Ledger(unchanged.Snapshot(Cutoff), bars, false).UnavailableFromSession);
    }

    // A split with ex-date 18 September closes the position at the 17 September close although bars continue.
    [Fact]
    public void SplitDuringAHold_ClosesAtTheLastCloseBeforeTheExDate()
    {
        var bars = Bars(_ => true);
        var coordinator = Schedule(bars, Rules([("TEST", new DateOnly(2026, 9, 18))]), exit: null);

        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(ChronologicalBacktestExecutionCoordinator.SplitExitReason, trade.ExitReason);
        Assert.Equal((Price(new(2026, 9, 17)) * 0.999m, Midnight(new(2026, 9, 18))), (trade.ExitPrice, trade.ExitTimestamp));
        Assert.Empty(coordinator.Snapshot(Cutoff).PendingOrders);
    }

    // A gap from 17 to 21 September is held through (valued at the 16 September close) and the pending signal exit fills at
    // the 22 September open when bars resume; nothing is forced.
    [Fact]
    public void GapAfterWhichBarsResume_IsHeldAndThePendingExitFillsAtTheNextRealOpen()
    {
        var gap = new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 21) };
        var bars = Bars(day => !gap.Contains(day), thin: false);
        var resume = new DateOnly(2026, 9, 22);
        var coordinator = Schedule(bars, Rules([]), exit: Midnight(resume));

        coordinator.Complete(Cutoff);

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(("close_above_sma5", Midnight(resume.AddDays(1))), (trade.ExitReason, trade.ExitTimestamp));
        Assert.Equal(Price(resume) * 0.999m, trade.ExitPrice);
        var ledger = Ledger(coordinator.Snapshot(Cutoff), bars, true);
        Assert.All(ledger.Sessions, session => Assert.NotNull(session.Equity));
        Assert.True(ledger.CarriesLastCloseThroughGaps);
        Assert.Contains(ledger.SessionMarks, mark => mark.SessionDate == gap[0] && mark.Price is null);
    }

    // The rules for the given split ex-dates over the fixture's weekday sessions.
    private static HoldingDataRules Rules(IEnumerable<(string, DateOnly)> splits) =>
        new(Weekdays().ToArray(), splits.ToHashSet());

    // Schedules one 500-share long held from the 15 September open, with the stop and target 7.1.0 places, and an optional
    // planned exit (a completed close-above-SMA5 signal) at a later open.
    private static ChronologicalBacktestExecutionCoordinator Schedule(IReadOnlyCollection<OhlcvBar> bars, HoldingDataRules? rules, DateTimeOffset? exit)
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(Portfolio, bars, holdingRules: rules);
        var candidate = new BacktestCandidateTrade("TEST", "Connors 7.1.0", "long", Midnight(new(2026, 9, 15)), 100m, 90m, 120m,
            exit, null, exit is null ? "open_at_study_end" : "close_above_sma5", 10m, StrategyId: Strategy.StrategyId, CandidateId: "held");
        Assert.True(coordinator.TrySchedule(new(candidate, Strategy, 500), out var rejection), rejection);
        return coordinator;
    }

    // Values a snapshot with the native ledger over the fixture's calendar, with or without the research-screen rules.
    private static BacktestPortfolioState Ledger(BacktestPortfolioState state, IReadOnlyCollection<OhlcvBar> bars, bool rules) =>
        NativeDailySessionLedger.Analyze(Portfolio.StartingCapital, state, bars,
            new NativeDailyAccountingContext(Midnight(First), Cutoff, Calendar(), true) { ResearchScreenHoldingRules = rules });

    private static readonly PortfolioConfig Portfolio = new(StartingCapital: 100_000m, AccountRiskBudgetPct: 1m, MaxPositionNotionalPct: 100m,
        MaxConcurrentPositions: 10, FixedBuyFee: 0m, FixedSellFee: 0m, MaxOpenTradesPerTicker: 10, PreventOverlappingTickerPositions: true,
        MaxBarParticipationPct: 5m);

    private static readonly TradingFlow.Domain.Strategies.StrategyDefinition Strategy = new SimpleYamlReader().ReadStrategy(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed-as-traded.bt-v7-1.yaml"));

    private static DateTimeOffset Cutoff => Midnight(Last.AddDays(1));

    // Weekday sessions of the fixture.
    private static IEnumerable<DateOnly> Weekdays() => Enumerable.Range(First.DayNumber, Last.DayNumber - First.DayNumber + 1)
        .Select(DateOnly.FromDayNumber).Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));

    // Every calendar date of the fixture window, weekends closed.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar() =>
        Enumerable.Range(First.DayNumber, Last.DayNumber - First.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
            day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));

    // TEST's daily bars on the kept weekdays: open = close = 100 plus 0.50 a day, high and low 5 away (stop and target never
    // touched). Volume is 100,000 shares, or 1,000 after the entry day when thin (a 5% cap would then allow 50 shares).
    private static OhlcvBar[] Bars(Func<DateOnly, bool> keep, bool thin = true) => Weekdays().Where(keep).Select(day => new OhlcvBar("TEST",
        Midnight(day), "1d", Price(day), Price(day) + 5m, Price(day) - 5m, Price(day),
        thin && day > new DateOnly(2026, 9, 15) ? 1_000m : 100_000m, "sip", "raw")).ToArray();

    private static decimal Price(DateOnly day) => 100m + 0.5m * (day.DayNumber - First.DayNumber);

    private static DateTimeOffset Midnight(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);
}
