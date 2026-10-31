using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class IntradayZoneStrategyTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 27, 13, 30, 0, TimeSpan.Zero);

    // Equality and zero-width wicks are literal, and the third right candle determines the first publication clock.
    [Fact]
    public void FlatCloses_ConfirmBothRawZeroWidthBoxesExactlyFortyFiveMinutesAfterPivotClose()
    {
        var calculator = new IntradayZoneStateCalculator();
        IntradayZoneUpdate? update = null;
        for (var index = 0; index < 7; index++)
        {
            var bar = Bar(index * 15, 100, 100, 100, 100, "15m");
            update = calculator.ApplyCompleted15m(bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(15));
            if (index < 6) Assert.Empty(update.Snapshot.Zones);
        }
        Assert.Equal(2, update!.Snapshot.Zones.Count);
        Assert.Equal(new[] { IntradayZoneRole.Resistance, IntradayZoneRole.Support }, update.Snapshot.Zones.Select(zone => zone.Role));
        Assert.All(update.Snapshot.Zones, zone =>
        {
            Assert.Equal(100m, zone.Bottom);
            Assert.Equal(100m, zone.Top);
            Assert.Equal(1, zone.PivotCount);
            Assert.Equal(TimeSpan.FromMinutes(45), zone.KnownAtUtc - zone.LatestPivot.PivotCompletedAtUtc);
        });
    }

    // Bottom distance merges disjoint rectangles; published versions retain their original raw bounds and pivot count.
    [Fact]
    public void BottomDistance_CanMergeDisjointWicksWithoutRewritingPublishedSnapshot()
    {
        var calculator = new IntradayZoneStateCalculator();
        var first = FeedBlock(calculator, 0, 103m, 103.2m, 5m);
        var resistance = Assert.Single(first.Snapshot.Zones, zone => zone.Role == IntradayZoneRole.Resistance);
        var second = FeedBlock(calculator, 7, 104m, 104.2m, 5m);
        var merged = Assert.Single(second.Snapshot.Zones, zone => zone.ZoneId == resistance.ZoneId);
        Assert.Equal(103m, merged.Bottom);
        Assert.Equal(104.2m, merged.Top);
        Assert.Equal(2, merged.PivotCount);
        Assert.Equal(103.2m, resistance.Top);
        Assert.Equal(1, resistance.PivotCount);
    }

    // Rectangle overlap alone cannot merge when the literal bottom-distance test fails.
    [Fact]
    public void BottomDistance_DoesNotMergeOverlappingRectanglesBeyondThreshold()
    {
        var calculator = new IntradayZoneStateCalculator();
        FeedBlock(calculator, 0, 100m, 110m, 1m);
        var update = FeedBlock(calculator, 7, 104m, 111m, 1m);
        Assert.Equal(2, update.Snapshot.Zones.Count(zone => zone.Role == IntradayZoneRole.Resistance));
    }

    // Multiple eligible boxes select the oldest exactly once, rather than merging all eligible zones transitively.
    [Fact]
    public void Merge_SelectsOldestCurrentRoleAndUsesLatestCompletedAtr()
    {
        var calculator = new IntradayZoneStateCalculator();
        var first = FeedBlock(calculator, 0, 100m, 110m, 1m).Snapshot.Zones.First(zone => zone.Role == IntradayZoneRole.Resistance);
        FeedBlock(calculator, 7, 102m, 112m, 1m);
        var update = FeedBlock(calculator, 14, 101m, 111m, 5m);
        var zones = update.Snapshot.Zones.Where(zone => zone.Role == IntradayZoneRole.Resistance).ToArray();
        Assert.Equal(2, zones.Length);
        Assert.Equal(2, zones.Single(zone => zone.ZoneId == first.ZoneId).PivotCount);
        Assert.Equal(1, zones.Single(zone => zone.ZoneId != first.ZoneId).PivotCount);
    }

    // Both flip directions are strict; equality never changes the role, and an old box can arm a later retest.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlipThreshold_IsStrictAndSymmetric(bool upward)
    {
        var calculator = new IntradayZoneStateCalculator();
        for (var index = 0; index < 7; index++)
        {
            var bar = Bar(index * 15, 100, 100, 100, 100, "15m");
            calculator.ApplyCompleted15m(bar, Indicator(bar, 5m), Session(), bar.Timestamp.AddMinutes(15));
        }
        var equal = Bar(105, 100, upward ? 101 : 100, upward ? 100 : 99, upward ? 101 : 99, "15m");
        Assert.Empty(calculator.ApplyCompleted15m(equal, Indicator(equal, 5m), Session(), equal.Timestamp.AddMinutes(15)).Snapshot.Transitions);
        var beyond = Bar(120, 100, upward ? 101.0001m : 100, upward ? 100 : 98.9999m, upward ? 101.0001m : 98.9999m, "15m");
        var transition = Assert.Single(calculator.ApplyCompleted15m(beyond, Indicator(beyond, 5m), Session(), beyond.Timestamp.AddMinutes(15)).Snapshot.Transitions);
        Assert.Equal(upward ? IntradayZoneRole.Support : IntradayZoneRole.Resistance, transition.After.Role);
        Assert.True(transition.CanArmRetest);
    }

    // A newly published zone has no earlier availability even when the supplied close sequence is flat.
    [Fact]
    public void NewZoneConfirmation_IsPublishedAtCurrentClock()
    {
        var calculator = new IntradayZoneStateCalculator();
        var closes = new[] { 100m, 100m, 100m, 100m, 100m, 100m, 100m };
        IntradayZoneUpdate? update = null;
        for (var index = 0; index < closes.Length; index++)
        {
            var bar = Bar(index * 15, 100, 100, index == 3 ? 90 : 100, closes[index], "15m");
            update = calculator.ApplyCompleted15m(bar, Indicator(bar, 1m), Session(), bar.Timestamp.AddMinutes(15));
        }
        Assert.All(update!.Snapshot.Zones, zone => Assert.Equal(update.Snapshot.AsOfUtc, zone.CreatedAtUtc));
        Assert.All(update.Snapshot.Transitions, transition => Assert.False(transition.CanArmRetest));
    }

    // A real gap is unknown source evidence, whereas an identical transport retry preserves its original immutable result.
    [Fact]
    public void SourceSequence_IsIdempotentAndFailsClosedOnGapOrConflictingDuplicate()
    {
        var calculator = new IntradayZoneStateCalculator();
        var first = Bar(0, 100, 101, 99, 100, "15m");
        var update = calculator.ApplyCompleted15m(first, Indicator(first), Session(), first.Timestamp.AddMinutes(15));
        Assert.Same(update, calculator.ApplyCompleted15m(first, Indicator(first), Session(), first.Timestamp.AddMinutes(15)));
        var conflict = calculator.ApplyCompleted15m(first with { Volume = 999m }, Indicator(first), Session(), first.Timestamp.AddMinutes(15));
        Assert.False(conflict.Snapshot.CoverageKnown);
        Assert.False(calculator.ApplyCompleted15m(first, Indicator(first), Session(), first.Timestamp.AddMinutes(15)).Snapshot.CoverageKnown);
        var gapCalculator = new IntradayZoneStateCalculator();
        gapCalculator.ApplyCompleted15m(first, Indicator(first), Session(), first.Timestamp.AddMinutes(15));
        var gap = first with { Timestamp = first.Timestamp.AddMinutes(30) };
        Assert.Equal("intraday_zone_15m_coverage_gap", gapCalculator.ApplyCompleted15m(gap, Indicator(gap), Session(), gap.Timestamp.AddMinutes(15)).RejectionReason);
        Assert.False(gapCalculator.ApplyCompleted15m(first, Indicator(first), Session(), first.Timestamp.AddMinutes(15)).Snapshot.CoverageKnown);
    }

    // Looking at a future candle before completion cannot mutate the prefix or affect the later valid replay.
    [Fact]
    public void FutureObservation_DoesNotAdvanceCompletedPrefix()
    {
        var calculator = new IntradayZoneStateCalculator();
        var bar = Bar(0, 100, 101, 99, 100, "15m");
        Assert.Equal("intraday_zone_completed_15m_evidence_invalid", calculator.ApplyCompleted15m(bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(14)).RejectionReason);
        Assert.Null(calculator.ApplyCompleted15m(bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(15)).RejectionReason);
    }

    // Each direction uses its strict VWAP side, exact slot volume and outward candle stop; EMA evidence is irrelevant.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bounce_UsesOnlyVwapAndConfirmationSlotVolume(bool isLong)
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var zone = Zone(1, isLong ? IntradayZoneRole.Support : IntradayZoneRole.Resistance, 99m, 100m);
        var bar = isLong ? Bar(0, 100, 102, 99.005m, 101m) : Bar(0, 99, 100.005m, 97, 98);
        var indicator = Indicator(bar) with { Vwap = 99.5m, Ema20 = isLong ? 1000m : 1m };
        var decision = evaluator.EvaluateCompleted5m(Snapshot(zone), bar, indicator, Session(), bar.Timestamp.AddMinutes(5), 0.01m);
        Assert.Equal(isLong ? "long" : "short", decision.Signal!.Direction);
        Assert.Equal("rejection_bounce", decision.Signal.Pattern);
        Assert.Equal(isLong ? 99m : 100.01m, decision.Signal.InitialStopPrice);
        Assert.True(IntradayZonePatternEvaluator.MatchesDecision(decision, bar, bar.Timestamp.AddMinutes(5)));
        Assert.Same(decision, evaluator.EvaluateCompleted5m(Snapshot(zone), bar, indicator, Session(), bar.Timestamp.AddMinutes(5), 0.01m));
    }

    // Strict volume equality, equality at VWAP and unverified slot samples all suppress a confirmation.
    [Theory]
    [InlineData("volume")]
    [InlineData("vwap")]
    [InlineData("samples")]
    public void Bounce_RejectsUnavailableOrNonStrictFilters(string mutation)
    {
        var bar = Bar(0, 100, 102, 99, 101);
        var indicator = Indicator(bar);
        if (mutation == "volume") indicator = indicator with { SlotRelativeVolume = 1m, SlotMedianVolume = bar.Volume };
        if (mutation == "vwap") indicator = indicator with { Vwap = bar.Close };
        if (mutation == "samples") indicator = indicator with { SlotRelativeVolumeSampleCount = 19 };
        var result = new IntradayZonePatternEvaluator().EvaluateCompleted5m(Snapshot(Zone(1, IntradayZoneRole.Support, 99, 100)),
            bar, indicator, Session(), bar.Timestamp.AddMinutes(5), 0.01m);
        Assert.Null(result.Signal);
    }

    // Priority and consumption are shared across patterns; remaining in the box cannot repeatedly emit the resolved touch.
    [Fact]
    public void Retest_TakesPriorityFreezesBoxAndConsumesAtFirstIntent()
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var frozen = Zone(1, IntradayZoneRole.Support, 99, 100);
        var bounce = Zone(2, IntradayZoneRole.Support, 100.5m, 100.75m);
        var transition = new IntradayZoneTransition(frozen with { Role = IntradayZoneRole.Resistance }, frozen, Open, true, 1m);
        evaluator.ObserveZoneUpdate(new(Snapshot(frozen, bounce) with { Transitions = new[] { transition } }));
        var bar = Bar(0, 100, 102, 99.5m, 101);
        var first = evaluator.EvaluateCompleted5m(Snapshot(frozen, bounce), bar, Indicator(bar), Session(), Open.AddMinutes(5), 0.01m);
        Assert.Equal("breakout_retest", first.Signal!.Pattern);
        Assert.Equal(1, first.Signal.Evidence.Zone.ZoneId);
        var next = bar with { Timestamp = Open.AddMinutes(5) };
        var second = evaluator.EvaluateCompleted5m(Snapshot(frozen), next, Indicator(next), Session(), Open.AddMinutes(10), 0.01m);
        Assert.Null(second.Signal);
    }

    // Same-close new state cannot reinterpret the five-minute candle that ended at the flip clock.
    [Fact]
    public void SameCloseFlip_BecomesUsableOnlyForCandleStartingAtFlip()
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var zone = Zone(1, IntradayZoneRole.Support, 99, 100) with { KnownAtUtc = Open.AddMinutes(5) };
        evaluator.ObserveZoneUpdate(new(Snapshot(zone) with { AsOfUtc = Open.AddMinutes(5),
            Transitions = new[] { new IntradayZoneTransition(zone with { Role = IntradayZoneRole.Resistance }, zone, Open.AddMinutes(5), true, 1m) } }));
        var bar = Bar(0, 100, 102, 99, 101);
        Assert.Null(evaluator.EvaluateCompleted5m(Snapshot(), bar, Indicator(bar), Session(), Open.AddMinutes(5), 0.01m).Signal);
        var later = bar with { Timestamp = Open.AddMinutes(5) };
        var result = evaluator.EvaluateCompleted5m(Snapshot(zone) with { AsOfUtc = Open.AddMinutes(5) }, later,
            Indicator(later), Session(), Open.AddMinutes(10), 0.01m);
        Assert.Equal("breakout_retest", result.Signal!.Pattern);
    }

    // Equality at the invalidation edge preserves the frozen retest, while a strict close through it cancels the pattern.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retest_InvalidationIsStrictAndKeepsOriginalFrozenGeometry(bool throughEdge)
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var frozen = Zone(1, IntradayZoneRole.Support, 99, 100);
        evaluator.ObserveZoneUpdate(new(Snapshot(frozen) with { Transitions = new[] {
            new IntradayZoneTransition(frozen with { Role = IntradayZoneRole.Resistance }, frozen, Open, true, 1m) } }));
        var edge = Bar(0, 99, 100, 98, throughEdge ? 98.99m : 99m);
        Assert.Null(evaluator.EvaluateCompleted5m(Snapshot(frozen), edge, Indicator(edge), Session(), Open.AddMinutes(5), 0.01m).Signal);
        var merged = frozen with { Version = 2, Bottom = 98, Top = 102, KnownAtUtc = Open.AddMinutes(5) };
        var later = Bar(5, 100, 101.5m, 99.5m, 101);
        var result = evaluator.EvaluateCompleted5m(Snapshot(merged), later, Indicator(later), Session(), Open.AddMinutes(10), 0.01m);
        if (throughEdge) Assert.Null(result.Signal);
        else
        {
            Assert.Equal("breakout_retest", result.Signal!.Pattern);
            Assert.Equal(100m, result.Signal.Evidence.Zone.Top);
            Assert.Equal(1, result.Signal.Evidence.Zone.Version);
        }
    }

    // Kernel validation compares the emitted immutable filter evidence with its independently supplied completed snapshot.
    [Fact]
    public void DecisionGuard_RejectsForgedSideOrChangedIndicatorEvidence()
    {
        var bar = Bar(0, 100, 102, 99, 101);
        var indicator = Indicator(bar);
        var result = new IntradayZonePatternEvaluator().EvaluateCompleted5m(Snapshot(Zone(1, IntradayZoneRole.Support, 99, 100)),
            bar, indicator, Session(), Open.AddMinutes(5), 0.01m);
        Assert.True(IntradayZonePatternEvaluator.MatchesDecision(result, bar, Open.AddMinutes(5), indicator));
        Assert.False(IntradayZonePatternEvaluator.MatchesDecision(result with { Signal = result.Signal! with { Direction = "short" } }, bar, Open.AddMinutes(5), indicator));
        Assert.False(IntradayZonePatternEvaluator.MatchesDecision(result, bar, Open.AddMinutes(5), indicator with { Vwap = 100.5m }));
    }

    // Candidate collision is deterministic by edge distance, then zone creation clock, then creation identity.
    [Fact]
    public void Bounce_SelectsNearestEdgeThenOldestZone()
    {
        var bar = Bar(0, 100, 103, 98, 102);
        var old = Zone(1, IntradayZoneRole.Support, 99, 100);
        var near = Zone(2, IntradayZoneRole.Support, 100, 101);
        var tie = Zone(3, IntradayZoneRole.Support, 100, 101) with { CreatedAtUtc = Open.AddMinutes(-5) };
        var result = new IntradayZonePatternEvaluator().EvaluateCompleted5m(Snapshot(old, tie, near), bar,
            Indicator(bar), Session(), Open.AddMinutes(5), 0.01m);
        Assert.Equal(2, result.Signal!.Evidence.Zone.ZoneId);
    }

    // Calendar carry crosses overnight boundaries and expires a zone only after its pivot leaves all five retained sessions.
    [Fact]
    public void Carry_UsesFiveActualPriorSessionsAndNeverRequiresOvernightBars()
    {
        var dates = new[] { 12, 13, 14, 15, 16, 19, 20, 21, 22, 23, 26, 27 };
        var calculator = new IntradayZoneStateCalculator();
        DateTimeOffset? priorClose = null;
        IntradayZoneUpdate? result = null;
        for (var dayIndex = 5; dayIndex < dates.Length; dayIndex++)
        {
            var date = new DateOnly(2026, 10, dates[dayIndex]);
            var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
            var session = new IntradayZoneSessionContext(date, open, open.AddHours(6.5),
                dates.Take(dayIndex).TakeLast(5).Reverse().Select(day => new DateOnly(2026, 10, day)).ToArray(), priorClose);
            for (var slot = 0; slot < 26; slot++)
            {
                var bar = Bar(0, 100, 100, 100, 100, "15m") with { Timestamp = open.AddMinutes(15 * slot) };
                var indicator = Indicator(bar) with { Atr = dayIndex == 5 && slot < 7 ? 5m : null };
                result = calculator.ApplyCompleted15m(bar, indicator, session, bar.Timestamp.AddMinutes(15));
            }
            Assert.True(result!.Snapshot.CoverageKnown);
            if (dayIndex <= 10) Assert.Equal(2, result.Snapshot.Zones.Count);
            else Assert.Empty(result.Snapshot.Zones);
            priorClose = session.CloseUtc;
        }
    }

    // One missing five-minute source slot suppresses later intents instead of treating the absent candle as a non-touch.
    [Fact]
    public void EntryGap_FailsClosedEvenWhenLaterCandleWouldQualify()
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var snapshot = Snapshot(Zone(1, IntradayZoneRole.Support, 99, 100));
        var first = Bar(0, 95, 96, 94, 95);
        evaluator.EvaluateCompleted5m(snapshot, first, Indicator(first), Session(), Open.AddMinutes(5), 0.01m);
        var later = Bar(10, 100, 102, 99, 101);
        var result = evaluator.EvaluateCompleted5m(snapshot, later, Indicator(later), Session(), Open.AddMinutes(15), 0.01m);
        Assert.Null(result.Signal);
        Assert.Equal("intraday_zone_5m_or_15m_coverage_unknown", result.RejectionReason);
    }

    // A bounce can recur only after an observed departure, followed by a genuinely new overlapping rejection candle.
    [Fact]
    public void Bounce_RearmsAfterFreshDepartureAndTouch()
    {
        var evaluator = new IntradayZonePatternEvaluator();
        var snapshot = Snapshot(Zone(1, IntradayZoneRole.Support, 99, 100));
        var touch = Bar(0, 100, 102, 99, 101);
        Assert.NotNull(evaluator.EvaluateCompleted5m(snapshot, touch, Indicator(touch), Session(), Open.AddMinutes(5), 0.01m).Signal);
        var repeated = touch with { Timestamp = Open.AddMinutes(5) };
        Assert.Null(evaluator.EvaluateCompleted5m(snapshot, repeated, Indicator(repeated), Session(), Open.AddMinutes(10), 0.01m).Signal);
        var departed = Bar(10, 102, 103, 101, 102);
        Assert.Null(evaluator.EvaluateCompleted5m(snapshot, departed, Indicator(departed), Session(), Open.AddMinutes(15), 0.01m).Signal);
        var fresh = touch with { Timestamp = Open.AddMinutes(15) };
        Assert.NotNull(evaluator.EvaluateCompleted5m(snapshot, fresh, Indicator(fresh), Session(), Open.AddMinutes(20), 0.01m).Signal);
    }

    // Reusable policy values govern the pivot buffer, calendar retention and submitted session cap.
    [Fact]
    public void DeclaredPolicy_IsConsumedByCalculatorAndPatternEvidence()
    {
        var rules = new IntradayZoneRules(MaxFilledParentTradesPerSession: 4, PivotStrength: 1, PriorSessionCount: 3,
            MergeAtrMultiple: 0.1m, FlipAtrMultiple: 0.5m);
        var session = Session() with { PreviousSessionDates = Session().PreviousSessionDates.Take(3).ToArray() };
        var calculator = new IntradayZoneStateCalculator(rules);
        IntradayZoneUpdate? result = null;
        for (var index = 0; index < 3; index++)
        {
            var bar = Bar(index * 15, 100, 100, 100, 100, "15m");
            result = calculator.ApplyCompleted15m(bar, Indicator(bar), session, bar.Timestamp.AddMinutes(15));
        }
        Assert.Equal(2, result!.Snapshot.Zones.Count);
        Assert.Equal(TimeSpan.FromMinutes(15), result.Snapshot.Zones[0].KnownAtUtc - result.Snapshot.Zones[0].LatestPivot.PivotCompletedAtUtc);
        var touch = Bar(0, 100, 102, 99, 101);
        var decision = new IntradayZonePatternEvaluator(rules).EvaluateCompleted5m(Snapshot(Zone(1, IntradayZoneRole.Support, 99, 100)),
            touch, Indicator(touch), session, Open.AddMinutes(5), 0.01m);
        Assert.Equal(4, decision.Signal!.MaxFilledParentTradesPerSession);
    }

    // Fifteen-minute-first and five-minute-first delivery at the same close publish exactly the same future decision.
    [Fact]
    public void SimultaneousClocks_HaveTheSameCausalFuturePrefix()
    {
        var first = ReplayClockOrder(true);
        var second = ReplayClockOrder(false);
        Assert.Equal(first.Signal, second.Signal);
        Assert.Equal("rejection_bounce", first.Signal!.Pattern);
        Assert.Equal(Open.AddMinutes(105), first.Signal.Evidence.Zone.KnownAtUtc);
        Assert.Equal(Open.AddMinutes(110), first.Signal.ActiveAtUtc);
    }

    // Replay fixed completed source slots, switching only delivery order at coincident timeframe clocks.
    private static IntradayZoneDecisionContext ReplayClockOrder(bool fifteenFirst)
    {
        var evaluator = new IntradayZoneEvaluator();
        for (var slot = 0; slot < 21; slot++)
        {
            var bar = Bar(slot * 5, 100, 100, 100, 100);
            var quarter = Bar((slot / 3) * 15, 100, 100, 100, 100, "15m");
            if (slot % 3 == 2 && fifteenFirst)
                evaluator.ProcessCompleted15m(quarter, Indicator(quarter), Session(), quarter.Timestamp.AddMinutes(15));
            Assert.Null(evaluator.ProcessCompleted5m(bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(5), 0.01m).Signal);
            if (slot % 3 == 2 && !fifteenFirst)
                evaluator.ProcessCompleted15m(quarter, Indicator(quarter), Session(), quarter.Timestamp.AddMinutes(15));
        }
        var touch = Bar(105, 100, 102, 100, 101);
        return evaluator.ProcessCompleted5m(touch, Indicator(touch), Session(), touch.Timestamp.AddMinutes(5), 0.01m);
    }

    // Deterministic seven-bar blocks isolate a peak while leaving real surrounding RTH candles in the sequence.
    private static IntradayZoneUpdate FeedBlock(IntradayZoneStateCalculator calculator, int startIndex, decimal peak, decimal top, decimal atr)
    {
        IntradayZoneUpdate? result = null;
        for (var index = 0; index < 7; index++)
        {
            var close = index == 3 ? peak : 90m + index % 3;
            var bar = Bar((startIndex + index) * 15, close, index == 3 ? top : close + 0.1m, close - 0.1m, close, "15m");
            result = calculator.ApplyCompleted15m(bar, Indicator(bar, atr), Session(), bar.Timestamp.AddMinutes(15));
        }
        return result!;
    }

    // Supply an explicit NY RTH calendar context without weekday inference or synthesized source bars.
    private static IntradayZoneSessionContext Session() => new(new(2026, 10, 27), Open, Open.AddHours(6.5),
        new[] { new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 20) });

    // Fixed provider-style synthetic candles make raw-price and exact slot-clock assertions reproducible.
    private static OhlcvBar Bar(int minutes, decimal open, decimal high, decimal low, decimal close, string timeframe = "5m") =>
        new("CRDO", Open.AddMinutes(minutes), timeframe, open, high, low, close, 200m, "sip", "raw");

    // Indicators are supplied contracts, proving that this primitive consumes existing ATR/VWAP/RVOL without recalculation.
    private static IndicatorSnapshot Indicator(OhlcvBar bar, decimal atr = 5m) => new(bar.Ticker, bar.Timestamp, bar.Timeframe,
        bar.Close, bar.Volume, 99.5m, null, atr, null, null, null, null, null, null, null, null, null, null,
        SlotRelativeVolume: 2m, SlotMedianVolume: 100m, SlotRelativeVolumeSampleCount: 20,
        MarketEvidenceProfileVersion: MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort: "regular",
        RelativeVolumeMinimumSamples: 20, DataFeed: bar.DataFeed, AdjustmentPolicy: bar.AdjustmentPolicy,
        MarketEvidenceReliability: "verified_same_feed");

    // Synthetic immutable seed zones isolate pattern state from the separately exercised pivot calculator.
    private static IntradayZoneEvidence Zone(long id, IntradayZoneRole role, decimal bottom, decimal top) => new(id, 1, role,
        bottom, top, 1, Open.AddMinutes(-15), Open, Open.AddMinutes(-75), new(2026, 10, 27),
        new(role == IntradayZoneRole.Support ? IntradayZonePivotKind.Valley : IntradayZonePivotKind.Peak,
            Open.AddMinutes(-75), Open.AddMinutes(-60), Open.AddMinutes(-15), new(2026, 10, 27), bottom, top));

    // Pattern fixtures publish all selected seed versions before the first completed entry candle starts.
    private static IntradayZoneSnapshot Snapshot(params IntradayZoneEvidence[] zones) => new("CRDO", Open, new(2026, 10, 27), zones, []);
}
