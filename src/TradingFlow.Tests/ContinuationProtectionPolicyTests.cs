using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class ContinuationProtectionPolicyTests
{
    private static readonly DateTimeOffset EntryTime = DateTimeOffset.Parse("2026-10-01T13:30:00Z");

    // Reweighted actual fills retain activation, consumed candles, a partly-complete continuation group and earned target steps.
    [Fact]
    public void RebaseActualFillsPreservesEarnedProtectionAndConsumedContinuationEvidence()
    {
        var activated = Evaluate(Create(), LongActivation()).State;
        var firstBar = Bar(5, 102.4m, 102.8m, 102m, 102.6m);
        var first = Evaluate(activated, firstBar).State;
        var rebased = ContinuationProtectionPolicy.RebaseActualFills(first, 100.1m, 0.03m);
        Assert.True(rebased.IsActivated);
        Assert.Equal(100.14m, rebased.StopPrice);
        Assert.Equal(1, rebased.ConsecutiveContinuationBars);
        Assert.Equal(first.LastEvaluatedAtUtc, rebased.LastEvaluatedAtUtc);
        Assert.Same(rebased, Evaluate(rebased, firstBar).State);
        var extended = Evaluate(rebased, Bar(10, 102.6m, 102.95m, 102.2m, 102.8m)).State;
        Assert.Equal(104.5m, extended.TargetPrice);
        Assert.Equal(1, extended.CompletedTargetExtensions);
        var weightedAgain = ContinuationProtectionPolicy.RebaseActualFills(extended, 100.2m, 0.03m);
        Assert.Equal(105m, weightedAgain.TargetPrice);
        Assert.Equal(extended.StopPrice, weightedAgain.StopPrice);
        Assert.Equal(extended.PreviousCompletedBar, weightedAgain.PreviousCompletedBar);
    }

    // Short weighted fills preserve the mirrored cost-covered stop and the pending continuation group.
    [Fact]
    public void RebaseShortActualFillsNeverLoosensProtectionOrReusesConsumedBars()
    {
        var activated = Evaluate(Create(PlannedOrderSide.Short), ShortActivation()).State;
        var firstBar = Bar(5, 97.6m, 98m, 97.2m, 97.4m);
        var first = Evaluate(activated, firstBar).State;
        var rebased = ContinuationProtectionPolicy.RebaseActualFills(first, 99.9m, 0.03m);
        Assert.Equal(99.86m, rebased.StopPrice);
        Assert.True(rebased.StopPrice <= first.StopPrice);
        Assert.Equal(1, rebased.ConsecutiveContinuationBars);
        Assert.Same(rebased, Evaluate(rebased, firstBar).State);
        var extended = Evaluate(rebased, Bar(10, 97.4m, 97.8m, 97.05m, 97.2m)).State;
        Assert.Equal(95.5m, extended.TargetPrice);
        var weightedAgain = ContinuationProtectionPolicy.RebaseActualFills(extended, 99.8m, 0.03m);
        Assert.Equal(95m, weightedAgain.TargetPrice);
        Assert.Equal(extended.StopPrice, weightedAgain.StopPrice);
    }

    // Verify the approved long example, immutable R, and two fresh candles for every target step.
    [Fact]
    public void Long_ActivatesThenTightensAndExtendsFromOriginalRisk()
    {
        var initial = Create();
        var activation = Evaluate(initial, LongActivation());
        Assert.Equal(100.04m, activation.State.StopPrice);
        Assert.Equal(103m, activation.State.TargetPrice);
        Assert.Equal(EntryTime.AddMinutes(5), activation.EffectiveAfterUtc);
        Assert.False(initial.IsActivated);

        var first = Evaluate(activation.State, Bar(5, 102.4m, 102.8m, 102m, 102.6m));
        Assert.False(first.LevelsChanged);
        Assert.Equal(1, first.State.ConsecutiveContinuationBars);
        var second = Evaluate(first.State, Bar(10, 102.6m, 102.95m, 102.2m, 102.8m));
        Assert.True(second.LevelsChanged);
        Assert.Equal(102.2m, second.State.StopPrice);
        Assert.Equal(104m, second.State.TargetPrice);
        Assert.Equal(0, second.State.ConsecutiveContinuationBars);

        var third = Evaluate(second.State, Bar(15, 102.8m, 103.3m, 102.4m, 103.2m));
        Assert.False(third.LevelsChanged);
        var fourth = Evaluate(third.State, Bar(20, 103.2m, 103.8m, 102.8m, 103.6m));
        Assert.Equal(102.8m, fourth.State.StopPrice);
        Assert.Equal(105m, fourth.State.TargetPrice);
        Assert.Equal(1m, fourth.State.OriginalRiskPerShare);
        Assert.Equal(99m, fourth.State.InitialStopPrice);
        Assert.Equal(100m, fourth.State.EntryPrice);
    }

    // Mirror the exact same close, trend and original-risk rules for short positions.
    [Fact]
    public void Short_ActivatesThenTightensAndExtendsFromOriginalRisk()
    {
        var activated = Evaluate(Create(PlannedOrderSide.Short), ShortActivation());
        Assert.Equal(99.96m, activated.State.StopPrice);
        Assert.Equal(97m, activated.State.TargetPrice);
        var first = Evaluate(activated.State, Bar(5, 97.6m, 98m, 97.2m, 97.4m));
        var second = Evaluate(first.State, Bar(10, 97.4m, 97.8m, 97.05m, 97.2m));
        Assert.Equal(97.8m, second.State.StopPrice);
        Assert.Equal(96m, second.State.TargetPrice);
        var third = Evaluate(second.State, Bar(15, 97.2m, 97.6m, 96.7m, 96.8m));
        Assert.False(third.LevelsChanged);
        var fourth = Evaluate(third.State, Bar(20, 96.8m, 97.2m, 96.2m, 96.4m));
        Assert.Equal(97.2m, fourth.State.StopPrice);
        Assert.Equal(95m, fourth.State.TargetPrice);
        Assert.Equal(1m, fourth.State.OriginalRiskPerShare);
    }

    // An intrabar threshold touch does not authorize protection until the close reaches 2.4R.
    [Theory]
    [InlineData(PlannedOrderSide.Long, 102.1, 102.8, 101.8, 102.39)]
    [InlineData(PlannedOrderSide.Short, 97.9, 98.2, 97.2, 97.61)]
    public void Activation_UsesCloseInsteadOfWick(PlannedOrderSide side, double open, double high, double low, double close)
    {
        var initial = Create(side);
        var result = Evaluate(initial, Bar(0, (decimal)open, (decimal)high, (decimal)low, (decimal)close));
        Assert.False(result.LevelsChanged);
        Assert.False(result.State.IsActivated);
        Assert.Equal(initial.StopPrice, result.State.StopPrice);
    }

    // Cost protection rounds away from entry to cover the supplied round-trip estimate plus a tick.
    [Theory]
    [InlineData(PlannedOrderSide.Long, 100.10)]
    [InlineData(PlannedOrderSide.Short, 99.90)]
    public void CostBuffer_RoundsOutwardToValidTick(PlannedOrderSide side, double expected)
    {
        var initial = Create(side, cost: 0.031m, tick: 0.05m);
        var result = Evaluate(initial, side == PlannedOrderSide.Long ? LongActivation() : ShortActivation());
        Assert.Equal((decimal)expected, result.State.StopPrice);
    }

    // Fractional risk remains the target-step unit; target and stop rounding never enters original R.
    [Theory]
    [InlineData(PlannedOrderSide.Long, 103.05, 104.10, 102.35)]
    [InlineData(PlannedOrderSide.Short, 96.95, 95.90, 97.65)]
    public void FractionalRisk_RoundsTargetsOutwardAndStopsOutsideCandle(
        PlannedOrderSide side, double initialTarget, double nextTarget, double nextStop)
    {
        var initial = ContinuationProtectionPolicy.Create("TEST", side, EntryTime, 100m,
            side == PlannedOrderSide.Long ? 98.987m : 101.013m,
            0.025m, 0.05m, new());
        Assert.Equal((decimal)initialTarget, initial.TargetPrice);
        var activated = Evaluate(initial, side == PlannedOrderSide.Long
            ? Bar(0, 102.1m, 102.7m, 101.8m, 102.5m)
            : Bar(0, 97.9m, 98.2m, 97.3m, 97.5m));
        var first = Evaluate(activated.State, side == PlannedOrderSide.Long
            ? Bar(5, 102.5m, 102.8m, 102.113m, 102.6m)
            : Bar(5, 97.5m, 97.887m, 97.2m, 97.4m));
        var second = Evaluate(first.State, side == PlannedOrderSide.Long
            ? Bar(10, 102.6m, 102.99m, 102.357m, 102.8m)
            : Bar(10, 97.4m, 97.643m, 97.01m, 97.2m));
        Assert.Equal((decimal)nextTarget, second.State.TargetPrice);
        Assert.Equal((decimal)nextStop, second.State.StopPrice);
        Assert.Equal(1.013m, second.State.OriginalRiskPerShare);
    }

    // A failure of any strict trend component consumes the old streak and establishes a new anchor.
    [Theory]
    [InlineData(102.8, 102.1, 102.7)]
    [InlineData(102.9, 102.0, 102.7)]
    [InlineData(102.9, 102.1, 102.6)]
    public void FailedContinuation_ResetsConsecutiveCount(double high, double low, double close)
    {
        var activated = Evaluate(Create(), LongActivation()).State;
        var first = Evaluate(activated, Bar(5, 102.4m, 102.8m, 102m, 102.6m)).State;
        var failed = Evaluate(first, Bar(10, 102.6m, (decimal)high, (decimal)low, (decimal)close));
        Assert.Equal(0, failed.State.ConsecutiveContinuationBars);
        Assert.False(failed.LevelsChanged);
        var restart = Evaluate(failed.State, Bar(15, 102.7m, 103m, 102.2m, 102.8m));
        Assert.Equal(1, restart.State.ConsecutiveContinuationBars);
        Assert.Equal(103m, restart.State.TargetPrice);
        var confirmed = Evaluate(restart.State, Bar(20, 102.8m, 103.2m, 102.3m, 103m));
        Assert.Equal(104m, confirmed.State.TargetPrice);
    }

    // Missing intervals cannot be treated as consecutive five-minute evidence.
    [Fact]
    public void CandleGap_RequiresTwoMoreFreshCandles()
    {
        var activated = Evaluate(Create(), LongActivation()).State;
        var first = Evaluate(activated, Bar(5, 102.4m, 102.8m, 102m, 102.6m)).State;
        var gap = Evaluate(first, Bar(15, 102.6m, 102.95m, 102.2m, 102.8m));
        Assert.Equal(0, gap.State.ConsecutiveContinuationBars);
        Assert.False(gap.LevelsChanged);
        var restart = Evaluate(gap.State, Bar(20, 102.8m, 103m, 102.3m, 102.9m));
        Assert.False(restart.LevelsChanged);
        var second = Evaluate(restart.State, Bar(25, 102.9m, 103.1m, 102.4m, 103m));
        Assert.True(second.LevelsChanged);
    }

    // Neither replaying an activation candle nor sending older evidence creates a confirming candle.
    [Fact]
    public void DuplicateOutOfOrderAndEqualDecisionTime_DoNotAdvanceState()
    {
        var activated = Evaluate(Create(), LongActivation());
        var duplicate = Evaluate(activated.State, LongActivation());
        Assert.Same(activated.State, duplicate.State);
        var firstBar = Bar(5, 102.4m, 102.8m, 102m, 102.6m);
        var first = Evaluate(activated.State, firstBar);
        var older = Evaluate(first.State, LongActivation(), asOf: EntryTime.AddMinutes(15));
        Assert.Same(first.State, older.State);
        var lateFirst = Evaluate(activated.State, firstBar, asOf: EntryTime.AddMinutes(15));
        var equalTime = Evaluate(lateFirst.State, Bar(10, 102.6m, 102.95m, 102.2m, 102.8m),
            asOf: EntryTime.AddMinutes(15));
        Assert.Same(lateFirst.State, equalTime.State);
        Assert.False(equalTime.LevelsChanged);
        Assert.Equal("duplicate_or_out_of_order", equalTime.Reason);
    }

    // Entry inside a candle cannot grant permission to use its earlier extrema or close.
    [Fact]
    public void PartialEntryCandle_IsExcluded()
    {
        var initial = Create(enteredAt: EntryTime.AddMinutes(2));
        var partial = Evaluate(initial, LongActivation());
        Assert.Same(initial, partial.State);
        Assert.Equal("candle_began_before_entry", partial.Reason);
        var eligible = Evaluate(initial, LongActivation() with { Timestamp = EntryTime.AddMinutes(5) });
        Assert.True(eligible.State.IsActivated);
    }

    // A future close or provider revision is unavailable even if its OHLC values would trigger a change.
    [Fact]
    public void FutureCloseOrKnowledge_IsExcludedUntilAvailable()
    {
        var initial = Create();
        var future = Evaluate(initial, LongActivation(), asOf: EntryTime.AddMinutes(5).AddTicks(-1));
        Assert.Same(initial, future.State);
        var revised = LongActivation() with { KnownAtUtc = EntryTime.AddMinutes(6) };
        var unknown = Evaluate(initial, revised);
        Assert.Same(initial, unknown.State);
        var known = Evaluate(initial, revised, asOf: EntryTime.AddMinutes(6));
        Assert.True(known.LevelsChanged);
        Assert.Equal(EntryTime.AddMinutes(6), known.EffectiveAfterUtc);
    }

    // Late decisions take effect strictly after their actual decision clock, never at the earlier candle close.
    [Fact]
    public void DelayedDecision_DoesNotBackdateNewStop()
    {
        var activation = LongActivation() with { Low = 99.5m };
        var initial = Create();
        var result = Evaluate(initial, activation, asOf: EntryTime.AddMinutes(7));
        Assert.Equal(99m, initial.StopPrice);
        Assert.Equal(EntryTime.AddMinutes(7), result.EffectiveAfterUtc);
        Assert.True(activation.Low < result.State.StopPrice);
        Assert.True(activation.Timestamp.AddMinutes(5) < result.EffectiveAfterUtc);
    }

    // A caller that filled the old target on this candle cannot revive that position by extending it.
    [Theory]
    [InlineData(PlannedOrderSide.Long)]
    [InlineData(PlannedOrderSide.Short)]
    public void ActiveTargetTouchedAndPositionClosed_CannotExtend(PlannedOrderSide side)
    {
        var activated = Evaluate(Create(side), side == PlannedOrderSide.Long ? LongActivation() : ShortActivation()).State;
        var first = Evaluate(activated, side == PlannedOrderSide.Long
            ? Bar(5, 102.4m, 102.8m, 102m, 102.6m)
            : Bar(5, 97.6m, 98m, 97.2m, 97.4m)).State;
        var targetBar = side == PlannedOrderSide.Long
            ? Bar(10, 102.6m, 103.2m, 102.2m, 103.1m)
            : Bar(10, 97.4m, 97.8m, 96.8m, 96.9m);
        var targetTouched = side == PlannedOrderSide.Long
            ? targetBar.High >= first.TargetPrice
            : targetBar.Low <= first.TargetPrice;
        Assert.True(targetTouched);
        var result = Evaluate(first, targetBar, isOpen: !targetTouched);
        Assert.Same(first, result.State);
        Assert.False(result.LevelsChanged);
        Assert.Null(result.EffectiveAfterUtc);
        Assert.Equal("position_closed", result.Reason);
    }

    // The explicit order-processing watermark prevents a change before active orders are considered.
    [Fact]
    public void OrdersNotProcessedThroughDecisionTime_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Evaluate(
            Create(), LongActivation(), EntryTime.AddMinutes(5), true, EntryTime.AddMinutes(4)));
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Evaluate(
            Create(), LongActivation(), EntryTime.AddMinutes(5), true, EntryTime.AddMinutes(6)));
    }

    // Cost protection that would already trigger at the close cannot activate an executable stop.
    [Fact]
    public void CostBufferAtOrBeyondClose_DoesNotActivate()
    {
        var initial = Create(cost: 2.4m);
        var result = Evaluate(initial, LongActivation());
        Assert.False(result.State.IsActivated);
        Assert.False(result.LevelsChanged);
        Assert.Equal(initial.StopPrice, result.State.StopPrice);
    }

    // Candle patterns may progress while their lows still cannot tighten the cost-covered stop.
    [Fact]
    public void StopNotTighter_DoesNotExtendTarget()
    {
        var activated = Evaluate(Create(), Bar(0, 102.1m, 102.6m, 99m, 102.4m)).State;
        var first = Evaluate(activated, Bar(5, 102.4m, 102.8m, 99.5m, 102.6m)).State;
        var second = Evaluate(first, Bar(10, 102.6m, 102.95m, 100m, 102.8m));
        Assert.False(second.LevelsChanged);
        Assert.Equal(100.04m, second.State.StopPrice);
        Assert.Equal(103m, second.State.TargetPrice);
        Assert.Equal(0, second.State.ConsecutiveContinuationBars);
    }

    // A stop at a candle's closing market price is not an executable future stop and cannot extend a target alone.
    [Theory]
    [InlineData(PlannedOrderSide.Long)]
    [InlineData(PlannedOrderSide.Short)]
    public void StopAtClose_DoesNotExtendTarget(PlannedOrderSide side)
    {
        var activated = Evaluate(Create(side), side == PlannedOrderSide.Long ? LongActivation() : ShortActivation()).State;
        var first = Evaluate(activated, side == PlannedOrderSide.Long
            ? Bar(5, 102.4m, 102.8m, 102m, 102.6m)
            : Bar(5, 97.6m, 98m, 97.2m, 97.4m)).State;
        var flatAtClose = side == PlannedOrderSide.Long
            ? Bar(10, 102.8m, 102.95m, 102.7m, 102.7m)
            : Bar(10, 97.2m, 97.3m, 97.05m, 97.3m);
        var result = Evaluate(first, flatAtClose);
        Assert.False(result.LevelsChanged);
        Assert.Equal(first.TargetPrice, result.State.TargetPrice);
        Assert.Equal(first.StopPrice, result.State.StopPrice);
    }

    // A short extension reaching zero is not a possible price and must not tighten its stop independently.
    [Fact]
    public void ShortTargetAtZero_SkipsBothOrderChanges()
    {
        var initial = ContinuationProtectionPolicy.Create("TEST", PlannedOrderSide.Short,
            EntryTime, 1m, 1.2m, 0.001m, 0.01m, new());
        var activated = Evaluate(initial, Bar(0, 0.55m, 0.6m, 0.5m, 0.52m)).State;
        var first = Evaluate(activated, Bar(5, 0.52m, 0.58m, 0.47m, 0.49m)).State;
        var extended = Evaluate(first, Bar(10, 0.49m, 0.55m, 0.42m, 0.45m)).State;
        Assert.Equal(0.2m, extended.TargetPrice);
        Assert.Equal(0.55m, extended.StopPrice);
        var next = Evaluate(extended, Bar(15, 0.45m, 0.5m, 0.3m, 0.35m)).State;
        var impossible = Evaluate(next, Bar(20, 0.35m, 0.45m, 0.25m, 0.3m));
        Assert.False(impossible.LevelsChanged);
        Assert.Equal(0.2m, impossible.State.TargetPrice);
        Assert.Equal(0.55m, impossible.State.StopPrice);
        Assert.Equal("extended_target_not_executable", impossible.Reason);
    }

    // Invalid execution facts and weaker/unreachable policy settings fail before creating usable state.
    [Fact]
    public void InvalidCreationInputs_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(tick: 0m));
        Assert.Throws<ArgumentException>(() => Create(cost: -0.01m));
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Create("TEST", PlannedOrderSide.Long,
            EntryTime, 100m, 100m, 0m, 0.01m, new()));
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Create("TEST", (PlannedOrderSide)99,
            EntryTime, 100m, 99m, 0m, 0.01m, new()));
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Create("TEST", PlannedOrderSide.Long,
            EntryTime, 100m, 99m, 0m, 0.01m, new(), 2m));
        Assert.Throws<ArgumentException>(() => ContinuationProtectionPolicy.Create("TEST", PlannedOrderSide.Short,
            EntryTime, 1m, 2m, 0m, 0.01m, new()));
        Assert.Throws<InvalidOperationException>(() => Create(rules: new(ActivationR: 3m)));
        Assert.Throws<InvalidOperationException>(() => Create(rules: new(CostBufferTicks: 0)));
        Assert.Throws<InvalidOperationException>(() => Create(rules: new(ConfirmationBars: 1)));
        Assert.Throws<InvalidOperationException>(() => Create(rules: new(TargetStepR: 0m)));
    }

    // Wrong-position, wrong-timeframe and malformed OHLC candles cannot alter protection state.
    [Fact]
    public void InvalidCandleInputs_AreRejected()
    {
        var initial = Create();
        Assert.Throws<ArgumentException>(() => Evaluate(initial, LongActivation() with { Ticker = "OTHER" }));
        Assert.Throws<ArgumentException>(() => Evaluate(initial, LongActivation() with { Timeframe = "1m" }));
        Assert.Throws<ArgumentException>(() => Evaluate(initial, LongActivation() with { High = 100m }));
        Assert.Throws<ArgumentException>(() => Evaluate(initial, LongActivation() with { Low = 103m }));
        Assert.Throws<ArgumentException>(() => Evaluate(initial, LongActivation() with { Volume = -1m }));
    }

    // Supply only explicit synthetic fill, cost and tick facts to each isolated position state.
    private static ContinuationProtectionState Create(
        PlannedOrderSide side = PlannedOrderSide.Long,
        decimal cost = 0.025m,
        decimal tick = 0.01m,
        DateTimeOffset? enteredAt = null,
        ContinuationProtectionRules? rules = null) =>
        ContinuationProtectionPolicy.Create("TEST", side, enteredAt ?? EntryTime,
            100m, side == PlannedOrderSide.Long ? 99m : 101m, cost, tick, rules ?? new());

    // Model a caller that has already processed active orders through the candle close or supplied decision time.
    private static ContinuationProtectionDecision Evaluate(
        ContinuationProtectionState state, OhlcvBar bar, DateTimeOffset? asOf = null, bool isOpen = true)
    {
        var decisionTime = asOf ?? bar.Timestamp.AddMinutes(5);
        return ContinuationProtectionPolicy.Evaluate(state, bar, decisionTime, isOpen, decisionTime);
    }

    // Build a completed-close long activation candle without touching the original 3R target.
    private static OhlcvBar LongActivation() => Bar(0, 102.1m, 102.6m, 101.8m, 102.4m);

    // Build the short mirror of the long activation candle using the same fixed original risk.
    private static OhlcvBar ShortActivation() => Bar(0, 97.9m, 98.2m, 97.4m, 97.6m);

    // Timestamp deterministic five-minute evidence by its opening instant, matching the domain bar contract.
    private static OhlcvBar Bar(int minutes, decimal open, decimal high, decimal low, decimal close) =>
        new("TEST", EntryTime.AddMinutes(minutes), "5m", open, high, low, close, 1000m);
}
