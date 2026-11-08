using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class FineExecutionReplayTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
    private static OhlcvBar Bar(int minute, decimal open = 10, decimal high = 10, decimal low = 10, decimal volume = 1000) =>
        new("X", Start.AddMinutes(minute), "5m", open, high, low, open, volume);
    private static BacktestCandidateTrade Candidate(int exit = 20, int terminal = 25) =>
        new("X", "daily", "long", Start, 10, 9, 12, Start.AddMinutes(exit), 10, "technical_exit", 1,
            CandidateId: "candidate")
        {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1, Start.Date.AddHours(4),
                Start.Date.AddDays(1).AddHours(4), Start.AddMinutes(terminal),
                Enumerable.Range(0, terminal / 5 + 1).Select(i => Start.AddMinutes(i * 5)).ToArray())
        };
    private static ChronologicalBacktestExecutionCoordinator Run(OhlcvBar[] bars,
        BacktestCandidateTrade? candidate = null, int quantity = 10, decimal participation = 100)
    {
        var original = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var strategy = original with { Execution = original.Execution with { SlippageBps = 0 } };
        var runner = new ChronologicalBacktestExecutionCoordinator(new(10000, 1, 25, 4, 0, 0, 1, true,
            MaxBarParticipationPct: participation), bars);
        Assert.True(runner.TrySchedule(new(candidate ?? Candidate(), strategy, quantity), out var rejection), rejection);
        runner.Complete();
        return runner;
    }

    [Fact]
    public void EntrySliceTouchIsUnprotected_ThenLaterSliceStopWinsOverTargetAndUsesGapOpen()
    {
        var runner = Run([Bar(0, 10, 13, 8), Bar(5, 8, 13, 7), Bar(10)], quantity: 10);
        Assert.Empty(runner.Failures);
        var trade = Assert.Single(runner.CompletedTrades);
        Assert.Equal(Start.AddMinutes(10), trade.ExitTimestamp);
        Assert.Equal("stop_loss", trade.ExitReason);
        Assert.Equal(8, trade.ExitPrice);
    }

    [Fact]
    public void OpeningIocCancelsRemainder_AndProtectionCoversOnlyKnownFill()
    {
        var runner = Run([Bar(0, volume: 50), Bar(5, 8, 8, 8, 1000), Bar(10)], quantity: 100, participation: 10);
        var trade = Assert.Single(runner.CompletedTrades);
        Assert.Empty(runner.Failures);
        Assert.Equal(5, trade.ShareQuantity);
        Assert.Equal("partial_fill_remainder_canceled", trade.EntryFillStatus);
    }

    [Fact]
    public void ActivatedPartialStopContinuesAtMarketAfterRebound()
    {
        var runner = Run([Bar(0), Bar(5, 8, 8, 8, 20), Bar(10, 11, 11, 11, 1000)], quantity: 10, participation: 10);
        Assert.Empty(runner.Failures);
        var trade = Assert.Single(runner.CompletedTrades);
        Assert.Equal(10, trade.ShareQuantity);
        Assert.Equal(10.4m, trade.ExitPrice);
    }

    [Fact]
    public void StopActivationSurvivesZeroParticipationCapacity()
    {
        var runner = Run([Bar(0), Bar(5, 8, 8, 8, 1), Bar(10, 11, 11, 11)], quantity: 10, participation: 10);
        Assert.Empty(runner.Failures);
        Assert.Equal(11, Assert.Single(runner.CompletedTrades).ExitPrice);
    }

    [Fact]
    public void MissingOpeningDoesNotShiftTheOrderToTheNextObservedSlice()
    {
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var runner = new ChronologicalBacktestExecutionCoordinator(new(10000, 1, 25, 4, 0, 0, 1, true), [Bar(5)]);
        Assert.False(runner.TrySchedule(new(Candidate(), strategy, 10), out _));
        Assert.Equal("entry_execution_bar_missing", Assert.Single(runner.Failures).Reason);
    }

    [Fact]
    public void MissingSliceAbortsAtItsDeadlineAndRecordsEveryOpenExposure()
    {
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var runner = new ChronologicalBacktestExecutionCoordinator(new(10000, 1, 25, 4, 0, 0, 1, true),
            [Bar(0), Bar(0) with { Ticker = "Y" }, Bar(5) with { Ticker = "Y" }, Bar(10)]);
        Assert.True(runner.TrySchedule(new(Candidate(), strategy, 10), out _));
        Assert.True(runner.TrySchedule(new(Candidate() with { Ticker = "Y", CandidateId = "other" }, strategy, 10), out _));
        runner.Complete();
        Assert.Equal(2, runner.Failures.Count);
        Assert.All(runner.Failures, failure => Assert.Equal(10, failure.OpenSignedQuantity));
        Assert.Empty(runner.CompletedTrades);
    }

    [Fact]
    public void ExtendedHoursBarsCannotTriggerRegularSessionProtection()
    {
        var terminal = Start.AddDays(1);
        var candidate = Candidate() with { ExitTimestamp = terminal, ExitReason = "scheduled_study_exit",
            ExecutionReplay = Candidate().ExecutionReplay! with { TerminalOpenUtc = terminal,
                RequiredSliceStarts = [Start, Start.AddMinutes(5), terminal] } };
        var runner = Run([Bar(0), Bar(5), Bar(10, 8, 8, 8), Bar(0, 11, 11, 11) with { Timestamp = terminal }], candidate);
        Assert.Empty(runner.Failures);
        Assert.Equal(11, Assert.Single(runner.CompletedTrades).ExitPrice);
    }

    [Fact]
    public void PartialProtectiveExitDoesNotSuppressScheduledResidualExit()
    {
        var runner = Run([Bar(0), Bar(5, 8, 8, 8, 20), Bar(10, 11, 11, 11, 1000)],
            Candidate(exit: 10), quantity: 10, participation: 10);
        Assert.Empty(runner.Failures);
        Assert.Equal(10, Assert.Single(runner.CompletedTrades).ShareQuantity);
    }

    [Fact]
    public void ScheduledMarketExitDoesNotInstallNewProtectiveOrders()
    {
        var runner = Run([Bar(0), Bar(5, 10, 13, 8), Bar(10)], Candidate(exit: 5));
        Assert.Empty(runner.Failures);
        var trade = Assert.Single(runner.CompletedTrades);
        Assert.Equal("technical_exit", trade.ExitReason);
        Assert.Equal(10, trade.ExitPrice);
    }

    [Fact]
    public void WorkingPartialMarketExitDoesNotReinstallProtection()
    {
        var runner = Run([Bar(0), Bar(5, 10, 13, 8, 20), Bar(10, 11, 13, 8)],
            Candidate(exit: 5), quantity: 10, participation: 10);
        Assert.Empty(runner.Failures);
        var trade = Assert.Single(runner.CompletedTrades);
        Assert.Equal("technical_exit", trade.ExitReason);
        Assert.Equal(10.8m, trade.ExitPrice);
    }

    [Fact]
    public void DelayedExecutionEvidenceFailsBeforeBackdatingProtection()
    {
        var runner = Run([Bar(0) with { KnownAtUtc = Start.AddMinutes(8) }, Bar(5, 8, 8, 8), Bar(10)]);
        Assert.Empty(runner.CompletedTrades);
        Assert.Equal("delayed_execution_evidence_unsupported", Assert.Single(runner.Failures).Reason);
        Assert.Equal(0, runner.Failures[0].EntryFilledQuantity);
    }

    [Fact]
    public void RequiredGapWhileOpenIsIncomplete_NotANoTouchOrLaterFill()
    {
        var runner = Run([Bar(0), Bar(10, 8, 8, 8)]);
        Assert.Empty(runner.CompletedTrades);
        Assert.Contains("required_execution_slice_missing", Assert.Single(runner.Failures).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiredInvalidSliceIsIncomplete(decimal volume)
    {
        var runner = Run([Bar(0), Bar(5, volume: volume), Bar(10)]);
        Assert.Contains("required_execution_slice_invalid", Assert.Single(runner.Failures).Reason);
    }

    [Fact]
    public void DuplicateRequiredSliceIsIncomplete()
    {
        var runner = Run([Bar(0), Bar(5), Bar(5), Bar(10)]);
        Assert.Contains("required_execution_slice_invalid", Assert.Single(runner.Failures).Reason);
    }

    [Fact]
    public void TerminalResidualDoesNotFillOnLaterData()
    {
        var candidate = Candidate(exit: 10, terminal: 10) with { ExitReason = "scheduled_study_exit" };
        var runner = Run([Bar(0), Bar(5, volume: 20), Bar(10, volume: 100000), Bar(15, volume: 100000)],
            candidate, quantity: 10, participation: 10);
        Assert.Empty(runner.CompletedTrades);
        var failure = Assert.Single(runner.Failures);
        Assert.Equal("terminal_open_quantity_unresolved", failure.Reason);
        Assert.Equal(8, failure.OpenSignedQuantity);
    }

    [Fact]
    public void CalendarUsesEarlyCloseAndSkipsHoliday_WithoutEntryCutoffForProtection()
    {
        var source = DateTimeOffset.Parse("2026-07-02T04:00:00Z");
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [new(2026,7,2)] = new(new(2026,7,2), true, new(9,30), new(13,0)),
            [new(2026,7,3)] = MarketSessionSchedule.Closed(new(2026,7,3)),
            [new(2026,7,4)] = MarketSessionSchedule.Closed(new(2026,7,4)),
            [new(2026,7,5)] = MarketSessionSchedule.Closed(new(2026,7,5)),
            [new(2026,7,6)] = MarketSessionSchedule.RegularDay(new(2026,7,6))
        };
        var open = FineExecutionTiming.SessionOpen(source, "America/New_York", sessions);
        var terminal = FineExecutionTiming.SessionOpen(source.AddDays(4), "America/New_York", sessions);
        var grid = FineExecutionTiming.RequiredSlices(open, terminal, "America/New_York", sessions);
        Assert.Equal(43, grid.Count);
        Assert.Contains(DateTimeOffset.Parse("2026-07-02T16:55:00Z"), grid);
        Assert.DoesNotContain(DateTimeOffset.Parse("2026-07-02T17:00:00Z"), grid);
        Assert.Equal(terminal, grid[^1]);
    }

    [Fact]
    public void DailyExitKeepsEntrySessionSkippedAndFiveDailyBarsHeld()
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var strategy = request.Strategy with { ExitRules = request.Strategy.ExitRules with
        { ExitOnCloseAboveSma5 = true, MaxHoldBars = 5, MaxHoldHours = 10000,
          ExitOnCloseBelowEma20 = false, ExitOnCloseBelowVwap = false, ExitOnMacdHistogramNegative = false } };
        var source = DateTimeOffset.Parse("2026-06-01T04:00:00Z");
        var daily = Enumerable.Range(0,8).Select(i => new OhlcvBar("X", source.AddDays(i), "1d", 10, 100, 1, 10, 1000)).ToArray();
        var snaps = daily.Select(b => request.SnapshotsByTimeframe["1d"][^1] with
            { Timestamp = b.Timestamp, CurrentPrice = 10, Sma5 = 11 }).ToArray();
        snaps[0] = snaps[0] with { Sma5 = 9 }; // recovery on entry day must be ignored
        var result = FineExecutionTiming.DailyExit(strategy, daily, snaps, 0, daily[^1].Timestamp, _ => true, t => t.AddHours(9.5));
        Assert.Equal(6, result.Index);
        Assert.Equal("max_hold_bars", result.Reason);
        snaps[1] = snaps[1] with { Sma5 = 9 };
        result = FineExecutionTiming.DailyExit(strategy, daily, snaps, 0, daily[^1].Timestamp, _ => true, t => t.AddHours(9.5));
        Assert.Equal(2, result.Index);
        Assert.Equal("technical_exit_above_sma5", result.Reason);
        daily[1] = daily[1] with { KnownAtUtc = daily[2].Timestamp.AddHours(8) };
        result = FineExecutionTiming.DailyExit(strategy, daily, snaps, 0, daily[^1].Timestamp,
            _ => true, t => t.AddHours(9.5));
        Assert.Equal(2, result.Index); // known before next mapped open, despite being after daily source midnight
        daily[1] = daily[1] with { KnownAtUtc = daily[^1].Timestamp.AddDays(2) };
        result = FineExecutionTiming.DailyExit(strategy, daily, snaps, 0, daily[^1].Timestamp,
            _ => true, t => t.AddHours(9.5));
        Assert.Equal(7, result.Index);
        Assert.Equal("scheduled_study_exit", result.Reason);
    }

    // Parity with the daily simulator: with check_exits_from_fill_day the entry session's close is the first exit check and
    // counts as bar 1, so both replay modes give the same exit session as the daily backtest.
    [Fact]
    public void DailyExitChecksTheEntrySessionWhenFillDayCheckIsSet()
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        var strategy = request.Strategy with { ExitRules = request.Strategy.ExitRules with
        { ExitOnCloseAboveSma5 = true, MaxHoldBars = 5, MaxHoldHours = 10000, CheckExitsFromFillDay = true,
          ExitOnCloseBelowEma20 = false, ExitOnCloseBelowVwap = false, ExitOnMacdHistogramNegative = false } };
        var source = DateTimeOffset.Parse("2026-06-01T04:00:00Z");
        var daily = Enumerable.Range(0,9).Select(i => new OhlcvBar("X", source.AddDays(i), "1d", 10, 100, 1, 10, 1000)).ToArray();
        var snaps = daily.Select(b => request.SnapshotsByTimeframe["1d"][^1] with
            { Timestamp = b.Timestamp, CurrentPrice = 10, Sma5 = 11 }).ToArray();

        var held = FineExecutionTiming.DailyExit(strategy, daily, snaps, 1, daily[^1].Timestamp, _ => true, t => t.AddHours(9.5));
        var withoutSetting = FineExecutionTiming.DailyExit(strategy with { ExitRules = strategy.ExitRules with { CheckExitsFromFillDay = false } },
            daily, snaps, 1, daily[^1].Timestamp, _ => true, t => t.AddHours(9.5));
        snaps[1] = snaps[1] with { Sma5 = 9 };
        var fillDayExit = FineExecutionTiming.DailyExit(strategy, daily, snaps, 1, daily[^1].Timestamp, _ => true, t => t.AddHours(9.5));

        Assert.Equal((6, "max_hold_bars"), held);
        Assert.Equal((7, "max_hold_bars"), withoutSetting);
        Assert.Equal((2, "technical_exit_above_sma5"), fillDayExit);
    }

    [Fact]
    public void DailyCloseSamplingProcessesIntradayFillsWithoutInventingIntradayMarks()
    {
        var close = Start.AddHours(7);
        var result = PortfolioCloseValuation.Analyze(1000,
            [new("X", Start.AddMinutes(5), true, 10, 10, 1), new("X", close.AddDays(1).AddHours(-2), false, 10, 12, 1)],
            [new("X", close, 11)], [close, close.AddDays(1)], sampleOnlyExpectedTimes: true);
        Assert.Equal(1009, result.Observations[0].Equity);
        Assert.Equal(1018, result.EndingEquity);
        Assert.Equal(2, result.Observations.Count);
    }
}
