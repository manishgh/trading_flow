using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class IntradayPortfolioExecutionRetestTests
{
    // Account-dependent rejection cannot resurrect emitted retests; every source in the rejected group remains consumed.
    [Fact]
    public void RetestEntry_PortfolioRejectionDoesNotRearmEmittedSources()
    {
        var (evaluator, snapshot) = IntradayZoneRetestEntryTests.Prime(
            [IntradayZoneRetestEntryTests.Arm(1), IntradayZoneRetestEntryTests.Arm(2, 99.2m, 100.2m)],
            [IntradayZoneRetestEntryTests.Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var confirmation = IntradayZoneRetestEntryTests.Bar(15, 102, 103, 99.5m, 101);
        var decision = IntradayZoneRetestEntryTests.Evaluate(evaluator, snapshot, confirmation);
        Assert.Equal(2, decision.OrderedAlternatives!.Count);
        Assert.All(decision.OrderedAlternatives, signal => Assert.Null(signal.Evidence.TargetPlan!.PlannedQuantity));
        var bars = Bars(confirmation);
        var result = Replay(bars, decision, new(10000, 1, .0001m, 1, 1, 1, 1, true, 100));
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Empty(result.SelectedCandidates);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
        var next = IntradayZoneRetestEntryTests.Evaluate(evaluator, snapshot,
            IntradayZoneRetestEntryTests.Bar(20, 102, 103, 99.5m, 101));
        Assert.Null(next.Signal);
        Assert.Empty(next.ArmedRetests!);
    }

    // Later gaps and adverse intrabar ordering use the common protection path without changing the frozen retest plan.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestEntry_ExecutorPreservesFrozenPlanAndConservativeExit(bool gap)
    {
        var (evaluator, snapshot) = IntradayZoneRetestEntryTests.Prime([IntradayZoneRetestEntryTests.Arm(1)],
            [IntradayZoneRetestEntryTests.Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var confirmation = IntradayZoneRetestEntryTests.Bar(15, 102, 103, 99.8m, 101);
        var decision = IntradayZoneRetestEntryTests.Evaluate(evaluator, snapshot, confirmation);
        var bars = Bars(confirmation);
        var normal = Replay(bars, decision);
        bars[4] = gap ? IntradayZoneRetestEntryTests.Bar(20, 97, 97, 97, 97)
            : IntradayZoneRetestEntryTests.Bar(20, 101, 106, 97, 101);
        var changed = Replay(bars, decision);
        Assert.Equal(normal.AlternativeDecisions, changed.AlternativeDecisions);
        Assert.Equal(Assert.Single(normal.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan,
            Assert.Single(changed.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan);
        var trade = Assert.Single(changed.Trades);
        Assert.Equal(gap ? "gap_fill_through_structural_stop" : "stop_loss", trade.ExitReason);
        Assert.Equal(98.7m, trade.StopLossPrice);
        Assert.Equal(104m, trade.TakeProfitPrice);
        Assert.True(trade.Fees >= 2m);
        Assert.Empty(changed.Failures);
        Assert.Empty(normal.Failures);
    }

    // Executor validation checks arm clocks, live role and actual overlap instead of accepting only a pattern name.
    [Theory]
    [InlineData("flip")]
    [InlineData("role")]
    [InlineData("snapshot")]
    [InlineData("pattern")]
    [InlineData("target_after_snapshot")]
    public void RetestEntry_ExecutorRejectsForgedArmEvidence(string mutation)
    {
        var (evaluator, snapshot) = IntradayZoneRetestEntryTests.Prime([IntradayZoneRetestEntryTests.Arm(1)],
            [IntradayZoneRetestEntryTests.Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var previous = IntradayZoneRetestEntryTests.Bar(15, 102, 103, 101, 102);
        IntradayZoneRetestEntryTests.Evaluate(evaluator, snapshot, previous);
        var confirmation = IntradayZoneRetestEntryTests.Bar(20, 102, 103, 99.8m, 101);
        var decision = IntradayZoneRetestEntryTests.Evaluate(evaluator, snapshot, confirmation);
        var signal = decision.Signal!;
        var proof = signal.Evidence.Retest!;
        if (mutation == "flip") proof = proof with { ArmTransition = proof.ArmTransition with { CanArmRetest = false } };
        if (mutation == "role") proof = proof with { ActiveSourceAtConfirmationStart = proof.ActiveSourceAtConfirmationStart with { Role = IntradayZoneRole.Resistance } };
        if (mutation == "snapshot") proof = proof with { ConfirmationSnapshotAsOfUtc = confirmation.Timestamp.AddMinutes(5) };
        signal = signal with { Pattern = mutation == "pattern" ? "python_support_bounce" : signal.Pattern,
            Evidence = signal.Evidence with { Retest = proof } };
        if (mutation == "target_after_snapshot") signal = signal with { Evidence = signal.Evidence with {
            TargetZone = signal.Evidence.TargetZone! with { KnownAtUtc = confirmation.Timestamp } } };
        decision = decision with { Signal = signal, OrderedAlternatives = [signal] };
        var result = Replay(Bars(confirmation), decision);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // Supply a full deterministic RTH session with the emitted completed prefix and no provider or operator state.
    private static OhlcvBar[] Bars(OhlcvBar confirmation)
    {
        var bars = Enumerable.Range(0, 78).Select(index => IntradayZoneRetestEntryTests.Bar(index * 5, 101, 101.2m, 100.8m, 101)).ToArray();
        var confirmationIndex = (int)((confirmation.Timestamp - IntradayZoneRetestEntryTests.Session().OpenUtc).TotalMinutes / 5);
        for (var index = 0; index < confirmationIndex; index++) bars[index] = IntradayZoneRetestEntryTests.Bar(index * 5, 102, 103, 101, 102);
        bars[confirmationIndex] = confirmation;
        return bars;
    }

    // Replay the registered strategy through the existing typed candidate-group and shared capital/execution path.
    private static IntradayPortfolioReplayOutcome Replay(OhlcvBar[] bars, IntradayZoneDecisionContext decision, PortfolioConfig? portfolio = null)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "zone-breakout-retest.bt-v1.yaml"));
        var signal = decision.Signal!;
        var candidate = new BacktestCandidateTrade(signal.ConfirmationBar.Ticker, strategy.StrategyName, signal.Direction,
            signal.ActiveAtUtc, signal.EntryReferencePrice, signal.InitialStopPrice, signal.FixedTargetPrice ?? 0,
            signal.SessionDeadlineUtc, null, "session_deadline", Math.Abs(signal.EntryReferencePrice - signal.InitialStopPrice),
            StrategyId: strategy.StrategyId, CandidateId: "synthetic-retest") {
            ZoneExecution = signal, ZoneAlternatives = decision.OrderedAlternatives,
            ExecutionReplay = new(ExecutionReplayPolicy.PythonZoneVolumeSpreadV1, signal.ConfirmationBar.Timestamp,
                signal.ActiveAtUtc, signal.SessionDeadlineUtc, []),
            IntradayExecution = new(signal.EntryReferencePrice, signal.TickSize, signal.ActiveAtUtc, signal.EntryCutoffUtc!.Value,
                signal.SessionDeadlineUtc, null, null, signal.ConfirmationBar.Timestamp, signal.ActiveAtUtc, signal.Pattern) {
                EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, EntryCutoffUtc = signal.EntryCutoffUtc,
                MaxFilledParentTradesPerSession = 2 } };
        var date = IntradayZoneRetestEntryTests.Session().SessionDate;
        var sessions = new Dictionary<DateOnly, MarketSessionSchedule> { [date] = MarketSessionSchedule.RegularDay(date) };
        var settings = portfolio ?? new PortfolioConfig(10000, 1, 25, 1, 1, 1, 1, true, 100);
        var start = IntradayZoneRetestEntryTests.Session().OpenUtc;
        var result = IntradayPortfolioExecution.Replay(new(settings, [strategy], [candidate], bars, sessions, start, start.AddHours(6.5)));
        NativeDailySessionLedger.VerifyRecordedState(settings.StartingCapital, result.PortfolioState, new(start, start.AddHours(6.5), sessions, true));
        return result;
    }
}
