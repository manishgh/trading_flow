using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class BacktestAccountLossReplayTests
{
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly BacktestAccountLossReplayConfig Limits = new(true,
        BacktestAccountLossReplayConfig.FiveMinutePolicyId, new AccountLossLimits(2, 4, 10));
    private static readonly PortfolioConfig Portfolio = new(100, 100, 100, 4, 0, 0, 1, true, MaxBarParticipationPct: 100);

    // Each inclusive threshold latches at the first completed slice, using unrealized equity while no trade has closed.
    [Theory]
    [InlineData(98, true, false, false)]
    [InlineData(96, true, true, false)]
    [InlineData(90, true, true, true)]
    public void SamplesInclusiveLossThresholdsAndRefusesNextSlice(decimal close, bool daily, bool weekly, bool peak)
    {
        var open = At(Monday);
        var coordinator = Book([Bar("X", open, 100, close), Bar("Y", open.AddMinutes(5), 100, 100)]);
        Admit(coordinator, Plan("X", open));
        coordinator.AdvanceThrough(open.AddMinutes(5));
        var summary = coordinator.AccountLossSummary!;
        Assert.Equal(close, summary.LastMarkedEquity);
        Assert.Equal(daily, summary.FinalState!.DailyBreach is not null);
        Assert.Equal(weekly, summary.FinalState.WeeklyBreach is not null);
        Assert.Equal(peak, summary.FinalState.PeakBreach is not null);
        Assert.Equal(open.AddMinutes(5), summary.FinalState.LastTrustedRequestUtc);
        Assert.Equal(summary.FinalState.LastTrustedRequestUtc, summary.FinalState.ObservationUtc);
        Assert.False(summary.FinalState.CancellationSweepPending);
        Assert.Equal(1, summary.CompletedCancellationSweeps);
        Assert.Empty(coordinator.CompletedTrades);
        Assert.False(coordinator.TrySchedule(Plan("Y", open.AddMinutes(5)), out var rejection));
        Assert.Equal("account_loss_replay_refused", rejection);
    }

    // Fixed fees and the actual partial share count are present before the boundary, and recovery leaves the daily latch set.
    [Fact]
    public void PartialIocFeeCountsAndRiskReducingExitSurvivesRecovery()
    {
        var open = At(Monday);
        var book = Book([Bar("X", open, 100, 99, volume: 10), Bar("X", open.AddMinutes(5), 101, 101),
            Bar("X", open.AddMinutes(10), 101, 101), Bar("Y", open.AddMinutes(10), 100, 100)],
            portfolio: Portfolio with { FixedBuyFee = 1, MaxBarParticipationPct = 10 });
        Admit(book, Plan("X", open, exitMinutes: 10, quantity: 2));
        book.AdvanceThrough(open.AddMinutes(5));
        Assert.Equal(98, book.AccountLossSummary!.LastMarkedEquity);
        Assert.Empty(book.Snapshot(open.AddMinutes(5)).PendingOrders);
        book.AdvanceThrough(open.AddMinutes(10));
        Assert.Equal(100, book.AccountLossSummary!.LastMarkedEquity);
        Assert.NotNull(book.AccountLossSummary.FinalState!.DailyBreach);
        Assert.False(book.TrySchedule(Plan("Y", open.AddMinutes(10)), out _));
        book.AdvanceThrough(open.AddMinutes(15));
        var trade = Assert.Single(book.CompletedTrades);
        Assert.Equal(1, trade.ShareQuantity);
        Assert.Equal(1, trade.Fees);
        Assert.Equal("partial_fill_remainder_canceled", trade.EntryFillStatus);
        Assert.Empty(book.Failures);
    }

    // A simultaneous loss and gain leave the account flat regardless of symbol sort order or input ordering.
    [Theory]
    [InlineData(60, 140)]
    [InlineData(140, 60)]
    public void AllSymbolsContributeBeforeAnyLatches(decimal firstClose, decimal secondClose)
    {
        var open = At(Monday);
        var book = Book([Bar("Z", open, 100, secondClose), Bar("A", open, 100, firstClose)],
            portfolio: Portfolio with { StartingCapital = 1_000 });
        Admit(book, Plan("Z", open));
        Admit(book, Plan("A", open));
        book.AdvanceThrough(open.AddMinutes(5));
        var summary = book.AccountLossSummary!;
        Assert.Equal(1_000, summary.LastMarkedEquity);
        Assert.False(summary.FinalState!.Breached);
        Assert.Equal(2, summary.ObservationCount);
        Assert.Equal(0, summary.CompletedCancellationSweeps);
    }

    // The official open cannot borrow yesterday's mark or today's first candle close, even with a verified prior close.
    [Fact]
    public void OvernightHoldingRefusesAtOpenAndCanExitOnThatSlice()
    {
        var tuesday = Monday.AddDays(1);
        var open = At(Monday);
        var nextOpen = At(tuesday);
        var book = Book([.. Enumerable.Range(0, 4).Select(i => Bar("X", open.AddMinutes(i * 5), 100, 100)),
            Bar("X", nextOpen, 101, 101), Bar("Y", nextOpen, 100, 100)], end: tuesday);
        var held = Plan("X", open);
        Admit(book, held with { Candidate = held.Candidate with
        {
            ExitTimestamp = nextOpen,
            ExecutionReplay = held.Candidate.ExecutionReplay! with
            { TerminalOpenUtc = nextOpen, RequiredSliceStarts = [open, open.AddMinutes(5), open.AddMinutes(10), open.AddMinutes(15), nextOpen] }
        } });
        book.AdvanceThrough(nextOpen);
        Assert.False(book.TrySchedule(Plan("Y", nextOpen), out var rejection));
        Assert.Equal("loss_mark_unavailable_at_open", rejection);
        Assert.Equal(open.AddMinutes(20), book.AccountLossSummary!.LastMarkedAtUtc);
        book.AdvanceThrough(nextOpen.AddMinutes(5));
        Assert.Single(book.CompletedTrades);
        Assert.Null(book.AccountLossRefusalAt(nextOpen.AddMinutes(5)));
        Assert.Equal(101, book.AccountLossSummary!.LastMarkedEquity);
    }

    // A missing or delayed held slice blocks admission, while an independently resolved exit from the same batch is kept.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingHeldMarkDoesNotEraseOtherSameBoundaryFills(bool delayed)
    {
        var open = At(Monday);
        var bars = new List<OhlcvBar> { Bar("A", open, 100, 100), Bar("Z", open, 100, 100),
            Bar("Z", open.AddMinutes(5), 101, 101), Bar("NEW", open.AddMinutes(10), 100, 100) };
        if (delayed) bars.Add(Bar("A", open.AddMinutes(5), 110, 110) with { KnownAtUtc = open.AddMinutes(11) });
        var book = Book(bars, portfolio: Portfolio with { StartingCapital = 1_000 });
        Admit(book, Plan("A", open));
        Admit(book, Plan("Z", open, exitMinutes: 5));
        book.AdvanceThrough(open.AddMinutes(10));
        Assert.Equal("Z", Assert.Single(book.CompletedTrades).Ticker);
        Assert.Equal("A", Assert.Single(book.Failures).Ticker);
        Assert.Equal(1, book.AccountLossSummary!.UnavailableObservationCount);
        Assert.False(book.TrySchedule(Plan("NEW", open.AddMinutes(10)), out var rejection));
        Assert.Equal("loss_mark_unavailable", rejection);
    }

    // A broken simulator retaining an opening remainder cannot clear its sweep or progress to another entry.
    [Fact]
    public void UnprovedTerminalIocLeavesSweepPending()
    {
        var open = At(Monday);
        var book = Book([Bar("X", open, 100, 98, volume: 10), Bar("Y", open.AddMinutes(5), 100, 100)],
            portfolio: Portfolio with { MaxBarParticipationPct = 10 }, simulator: new RetainedIocSimulator());
        Admit(book, Plan("X", open, quantity: 2));
        book.AdvanceThrough(open.AddMinutes(5));
        Assert.True(book.AccountLossSummary!.FinalState!.CancellationSweepPending);
        Assert.Equal(0, book.AccountLossSummary.CompletedCancellationSweeps);
        Assert.False(book.TrySchedule(Plan("Y", open.AddMinutes(5)), out var rejection));
        Assert.Equal("loss_ioc_terminal_unproved", rejection);
    }

    // No plans or bars are needed to anchor flat cash, cross a holiday or verify the authoritative early closes.
    [Fact]
    public void FlatBooksAdvanceAcrossNoCandidateSessionsAndHoliday()
    {
        var friday = Monday.AddDays(-3);
        var tuesday = Monday.AddDays(1);
        var inputs = Inputs(friday, tuesday, holiday: Monday);
        var book = new ChronologicalBacktestExecutionCoordinator(Portfolio, [], accountLoss: inputs);
        book.Complete();
        var summary = book.AccountLossSummary!;
        Assert.Equal(10, summary.ObservationCount);
        Assert.Equal(0, summary.UnavailableObservationCount);
        Assert.Equal(At(tuesday, 20), summary.LastMarkedAtUtc);
        Assert.Equal(100, summary.LastMarkedEquity);
        Assert.Equal(friday, summary.FinalState!.PriorClose!.Session);
        Assert.Equal(At(friday, 20), summary.FinalState.WeekAnchor!.CloseUtc);
    }

    // A flat open can reuse the verified prior close, while positive peak latches survive both period boundaries.
    [Theory]
    [InlineData(96, false)]
    [InlineData(90, true)]
    public void WeekResetUsesFridayCloseAcrossMondayHoliday(decimal fridayEquity, bool peakRemains)
    {
        var friday = Monday.AddDays(-3);
        var tuesday = Monday.AddDays(1);
        var sample = new SampledBacktestAccountLoss(Inputs(friday, tuesday, Monday), 100);
        Observe(sample, friday, 0);
        sample.RecordFill(Fill(100 - fridayEquity, At(friday), SimulatedOrderSide.Buy));
        sample.RecordFill(Fill(1, At(friday, 5), SimulatedOrderSide.Sell, fees: 1));
        Observe(sample, friday, 20);
        Assert.NotNull(sample.Summary.FinalState!.DailyBreach);
        Assert.NotNull(sample.Summary.FinalState.WeeklyBreach);
        Observe(sample, tuesday, 0);
        Assert.Equal(fridayEquity, sample.Summary.FinalState!.PriorClose!.Equity);
        Assert.Equal(fridayEquity, sample.Summary.FinalState.WeekAnchor!.Equity);
        Assert.Null(sample.Summary.FinalState.DailyBreach);
        Assert.Null(sample.Summary.FinalState.WeeklyBreach);
        Assert.Equal(peakRemains, sample.Summary.FinalState.PeakBreach is not null);
        Assert.Equal(peakRemains, sample.Refusal(At(tuesday)) is not null);
    }

    // A missing prior close is never forward-filled. A newly verified close restores the next day's denominator only.
    [Fact]
    public void MissingCloseKeepsBaselineUnavailableUntilAnotherCloseIsVerified()
    {
        var tuesday = Monday.AddDays(1);
        var wednesday = Monday.AddDays(2);
        var sample = new SampledBacktestAccountLoss(Inputs(Monday, wednesday), 100);
        Observe(sample, Monday, 0);
        sample.RecordFill(Fill(100, At(Monday), SimulatedOrderSide.Buy));
        Observe(sample, Monday, 20);
        Observe(sample, tuesday, 0, mark: 100);
        Assert.Equal("loss_mark_unavailable_at_open", sample.Refusal(At(tuesday))?.Code);
        Observe(sample, tuesday, 5, mark: 100);
        Assert.Equal("loss_close_baseline_unavailable", sample.Refusal(At(tuesday, 5))?.Code);
        Observe(sample, tuesday, 20, mark: 100);
        Observe(sample, wednesday, 5, mark: 100);
        Assert.Null(sample.Refusal(At(wednesday, 5)));
        Assert.Equal(tuesday, sample.Summary.FinalState!.PriorClose!.Session);
    }

    // A daily-only latch clears on the next verified session within the same week and the old snapshot expires after five minutes.
    [Fact]
    public void DailyOnlyLatchResetsAndFiveMinuteFreshnessIsInclusive()
    {
        var tuesday = Monday.AddDays(1);
        var sample = new SampledBacktestAccountLoss(Inputs(Monday, tuesday), 100);
        Observe(sample, Monday, 0);
        Assert.Null(sample.Refusal(At(Monday, 5)));
        Assert.NotNull(sample.Refusal(At(Monday, 5).AddTicks(1)));
        sample.RecordFill(Fill(100, At(Monday), SimulatedOrderSide.Buy));
        Observe(sample, Monday, 20, mark: 98);
        Assert.NotNull(sample.Summary.FinalState!.DailyBreach);
        Assert.Null(sample.Summary.FinalState.WeeklyBreach);
        Observe(sample, tuesday, 5, mark: 98);
        Assert.Null(sample.Summary.FinalState!.DailyBreach);
        Assert.Null(sample.Refusal(At(tuesday, 5)));
    }

    // Explicitly disabled policy is identical to ordinary replay and simultaneous accounts own independent evidence.
    [Fact]
    public void DisabledReplayHasExactExecutionParityAndAccountsAreIsolated()
    {
        var open = At(Monday);
        OhlcvBar[] bars = [Bar("X", open, 100, 90), Bar("X", open.AddMinutes(5), 99, 99)];
        var normal = new ChronologicalBacktestExecutionCoordinator(Portfolio, bars);
        var disabled = new ChronologicalBacktestExecutionCoordinator(Portfolio, bars,
            accountLoss: Inputs(Monday, Monday) with { Settings = Limits with { Enabled = false } });
        var enabled = Book(bars);
        var other = Book(bars);
        foreach (var book in new[] { normal, disabled, enabled })
        {
            Admit(book, Plan("X", open, exitMinutes: 5));
            book.Complete();
        }
        Assert.Equal(JsonSerializer.Serialize(normal.Snapshot(open.AddMinutes(20))),
            JsonSerializer.Serialize(disabled.Snapshot(open.AddMinutes(20))));
        Assert.Equal(normal.CompletedTrades.ToArray(), disabled.CompletedTrades.ToArray());
        Assert.Equal(normal.Failures.ToArray(), disabled.Failures.ToArray());
        Assert.Null(disabled.AccountLossSummary);
        Assert.True(enabled.AccountLossSummary!.FinalState!.Breached);
        other.AdvanceThrough(open.AddMinutes(5));
        Assert.False(other.AccountLossSummary!.FinalState!.Breached);
        Assert.Equal(100, other.AccountLossSummary.LastMarkedEquity);
    }

    // Enabled configuration refuses every unsupported replay path and the fine book refuses a short plan independently.
    [Theory]
    [InlineData(null)]
    [InlineData(ExecutionReplayPolicy.DailyRthSipPortfolioV1)]
    [InlineData(ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1)]
    public void UnsupportedReplaysRefuseEnabledLimits(string? enginePolicy)
    {
        Assert.Throws<InvalidOperationException>(() => Limits.Validate("backtest", enginePolicy));
        (Limits with { Enabled = false }).Validate("backtest", enginePolicy);
        var open = At(Monday);
        var book = Book([Bar("X", open, 100, 100)]);
        var original = Plan("X", open);
        var shortPlan = original with { Candidate = original.Candidate with { Direction = "short" } };
        Assert.Throws<InvalidOperationException>(() => book.TrySchedule(shortPlan, out _));
    }

    // Build an isolated account with the synthetic fixture's short but authoritative four-slice session.
    private static ChronologicalBacktestExecutionCoordinator Book(IReadOnlyCollection<OhlcvBar> bars,
        DateOnly? end = null, PortfolioConfig? portfolio = null, IDeterministicExecutionSimulator? simulator = null) =>
        new(portfolio ?? Portfolio, bars, simulator, accountLoss: Inputs(Monday, end ?? Monday));

    // Advance the model clock before admitting a candidate, as production replay does before reserving its capacity.
    private static void Admit(ChronologicalBacktestExecutionCoordinator book, ChronologicalExecutionPlan plan)
    {
        book.AdvanceThrough(plan.Candidate.EntryTimestamp);
        Assert.True(book.TrySchedule(plan, out var refusal), refusal);
    }

    // Build a long-only IOC plan with no execution friction beyond the fixture's configurable fees and participation.
    private static ChronologicalExecutionPlan Plan(string symbol, DateTimeOffset open, int exitMinutes = 15, int quantity = 1)
    {
        var strategy = StrategyDecisionKernelTests.CreateRequest().Strategy;
        strategy = strategy with { Execution = strategy.Execution with { SlippageBps = 0 } };
        var candidate = new BacktestCandidateTrade(symbol, strategy.StrategyName, "long", open, 100, 10, 300,
            open.AddMinutes(exitMinutes), 100, "technical_exit", 90, CandidateId: $"{symbol}-{open:yyyyMMddHHmm}")
        {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1, open, open.AddMinutes(exitMinutes),
                open.AddMinutes(20), Enumerable.Range(0, 5).Select(i => open.AddMinutes(i * 5)).ToArray())
        };
        return new(candidate, strategy, quantity);
    }

    // Declare the session grid and explicit holiday for the synthetic fixtures, independent of market providers.
    private static BacktestAccountLossInputs Inputs(DateOnly first, DateOnly last, DateOnly? holiday = null)
    {
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var day = first.AddDays(-14); day <= last; day = day.AddDays(1))
            sessions[day] = day == holiday || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(day) : new(day, true, new(9, 30), new(9, 50));
        return new(Limits, sessions, At(first), At(last, 20));
    }

    // Advance the pure adapter with the same fixture boundary semantics used by the coordinator.
    private static void Observe(SampledBacktestAccountLoss sample, DateOnly date, int minutes, decimal? mark = null) =>
        sample.Observe(new(date, At(date, minutes), minutes == 0, minutes == 20), (_, _) => mark, true, false);

    // Model a single-share execution with its actual fill and fees for adapter-level state tests.
    private static SimulatedExecutionEvent Fill(decimal price, DateTimeOffset time, SimulatedOrderSide side, decimal fees = 0) =>
        new("fixture", "X", "filled", 1, 1, 0, price, fees, 0, 0, 0, 0, 0, time, "fixture", side, side == SimulatedOrderSide.Sell);

    // June and May fixture timestamps use New York daylight time.
    private static DateTimeOffset At(DateOnly date, int minutes = 0) =>
        new DateTimeOffset(date.ToDateTime(new(13, 30)), TimeSpan.Zero).AddMinutes(minutes);

    // Supply a well-formed regular-session candle with a distinct executable open and end mark.
    private static OhlcvBar Bar(string symbol, DateTimeOffset time, decimal open, decimal close, decimal volume = 1_000) =>
        new(symbol, time, "5m", open, Math.Max(open, close), Math.Min(open, close), close, volume);

    private sealed class RetainedIocSimulator : IDeterministicExecutionSimulator
    {
        // Deliberately violate the simulator's IOC terminal contract while retaining the actual first partial fill.
        public ExecutionStepResult Advance(ExecutionStepRequest request)
        {
            var result = new DeterministicExecutionSimulator().Advance(request);
            var command = Assert.Single(request.Commands);
            var orders = result.State.WorkingOrders.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            orders.Add(command.ClientOrderId, new(command, 1, 100, true, 0, false));
            return result with { State = result.State with { WorkingOrders = orders } };
        }
    }
}
