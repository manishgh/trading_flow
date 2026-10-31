using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class IntradayZoneRetestEntryTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 27, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 27);

    // A later actual retouch/reclaim can be red and low-RVOL; the source-zone stop and target economics remain D's.
    [Fact]
    public void RetestEntry_RequiresCompletedFlipThenActualOverlapWithoutColorOrVolumeThreshold()
    {
        var arm = Arm(1);
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (evaluator, snapshot) = Prime([arm], [target]);
        var bar = Bar(15, 102, 103, 99.8m, 101);
        var indicator = Indicator(bar) with { SlotRelativeVolume = .6m, SlotMedianVolume = 200m, Vwap = 1000m };
        var decision = evaluator.EvaluateCompleted5m(snapshot, bar, indicator, Session(), bar.Timestamp.AddMinutes(5), .01m);
        var signal = Assert.Single(decision.OrderedAlternatives!);
        Assert.Equal("breakout_retest", signal.Pattern);
        Assert.Equal("long", signal.Direction);
        Assert.Equal(arm.Transition.After, signal.Evidence.Zone);
        Assert.Equal(arm.FlippedAtUtc, signal.Evidence.FlippedAtUtc);
        Assert.Equal(98.7m, signal.InitialStopPrice);
        Assert.Equal(104m, signal.FixedTargetPrice);
        Assert.Equal("known_zone", signal.Evidence.TargetPlan!.TargetOrigin);
        Assert.Null(signal.Evidence.TargetPlan.PlannedQuantity);
        Assert.Equal(arm.Transition.After, signal.Evidence.Retest!.ActiveSourceAtConfirmationStart);
        Assert.Equal(snapshot.AsOfUtc, signal.Evidence.Retest.ConfirmationSnapshotAsOfUtc);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(decision, bar, bar.Timestamp.AddMinutes(5), indicator,
            Bar(10, 102, 103, 101, 102), Rules()));
        var trace = Assert.Single(decision.Trace!.Alternatives, item => item.Direction == "long" && item.Zone.ZoneId == 1);
        Assert.True(trace.PreviousPredicate);
        Assert.Null(trace.CandleDirection);
        Assert.False(trace.VolumeThresholdRequired);
        Assert.False(trace.VolumeThresholdPassed);
        Assert.Equal(signal.Evidence.Retest, trace.Retest);
    }

    // A tolerance-only near miss cannot emit; an un-emitted source remains available for a later actual retouch.
    [Fact]
    public void RetestEntry_NearTouchDoesNotConsumeArm()
    {
        var (evaluator, snapshot) = Prime([Arm(1)], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var near = Bar(15, 101, 103, 100.1m, 102);
        var first = Evaluate(evaluator, snapshot, near);
        Assert.Null(first.Signal);
        Assert.Contains(first.Trace!.Alternatives, item => item.Direction == "long" && item.Reason == "breakout_retest_actual_overlap_required");
        var later = Bar(20, 102, 103, 100, 101);
        Assert.Equal("breakout_retest", Evaluate(evaluator, snapshot, later).Signal!.Pattern);
    }

    // Every emitted armed source is consumed even though only the first alternative will be selected later by portfolio admission.
    [Fact]
    public void RetestEntry_ConsumesEveryEmittedLongAndCachesRetry()
    {
        var (evaluator, snapshot) = Prime([Arm(1), Arm(2, 99.2m, 100.2m)], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var bar = Bar(15, 102, 103, 99.5m, 101);
        var decision = Evaluate(evaluator, snapshot, bar);
        Assert.Equal(new long[] { 1, 2 }, decision.OrderedAlternatives!.Select(item => item.Evidence.Zone.ZoneId));
        Assert.Equal(2, decision.ArmedRetests!.Count);
        var serialized = JsonSerializer.Serialize(decision);
        Assert.Equal(serialized, JsonSerializer.Serialize(Evaluate(evaluator, snapshot, bar)));
        var next = Evaluate(evaluator, snapshot, Bar(20, 102, 103, 99.5m, 101));
        Assert.Null(next.Signal);
        Assert.Empty(next.ArmedRetests!);
        Assert.Equal(2, next.Trace!.RetestConsumptions!.Count);
        Assert.All(next.Trace.RetestConsumptions, item => Assert.Equal(Open.AddMinutes(20), item.EmittedAtUtc));
        Assert.Contains(next.Trace.Alternatives, item => item.Reason == "breakout_retest_source_consumed_at_emission");
        Assert.Equal(serialized, JsonSerializer.Serialize(decision));
    }

    // Same-close newly created resistance cannot arm, and a later qualifying replacement event is required after consumption.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestEntry_UsesCanArmAndReplacementFlip(bool canArm)
    {
        var arm = Arm(1);
        var selected = arm with { Transition = arm.Transition with { CanArmRetest = canArm } };
        var (evaluator, snapshot) = Prime([selected], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var first = Evaluate(evaluator, snapshot, Bar(15, 102, 103, 99.8m, 101));
        Assert.Equal(canArm, first.Signal?.Direction == "long");
        var replacementBefore = arm.Zone with { Role = IntradayZoneRole.Resistance, Version = 4, KnownAtUtc = Open.AddMinutes(15) };
        var replacementAfter = replacementBefore with { Role = IntradayZoneRole.Support, Version = 5, KnownAtUtc = Open.AddMinutes(30) };
        var flip = Bar(15, 100, 104, 99, 103) with { Timeframe = "15m", KnownAtUtc = Open.AddMinutes(30) };
        var replacement = new IntradayZoneTransition(replacementBefore, replacementAfter, Open.AddMinutes(30), true, 1, flip);
        for (var minute = 20; minute < 30; minute += 5) Evaluate(evaluator, snapshot, Bar(minute, 102, 103, 101, 102));
        var laterSnapshot = Snapshot(30, [replacementAfter, snapshot.Zones.Single(zone => zone.ZoneId == 99)]) with { Transitions = [replacement] };
        evaluator.ObserveZoneUpdate(new(laterSnapshot));
        Assert.Equal(5, Evaluate(evaluator, laterSnapshot, Bar(30, 102, 103, 99.8m, 101)).Signal!.Evidence.Zone.Version);
    }

    // Expansion of a live source cannot reorder the emitted alternatives ahead of another source's lower frozen bottom.
    [Fact]
    public void RetestEntry_OrdersFrozenBottomsAndKeepsGeometryRejectedArms()
    {
        var first = Arm(1);
        var second = Arm(2, 98.5m, 99.8m);
        var invalid = Arm(3, .1m, 99.5m);
        var (evaluator, snapshot) = Prime([first, second, invalid], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        snapshot = snapshot with { Zones = snapshot.Zones.Select(zone => zone.ZoneId == 1 ? zone with { Bottom = 97m, Version = 4, PivotCount = 3 } : zone).ToArray() };
        var emitted = Evaluate(evaluator, snapshot, Bar(15, 102, 103, 98.6m, 101));
        Assert.Equal(new long[] { 2, 1 }, emitted.OrderedAlternatives!.Select(item => item.Evidence.Zone.ZoneId));
        var later = Evaluate(evaluator, snapshot, Bar(20, 102, 103, 98.6m, 101));
        Assert.Null(later.Signal);
        Assert.Equal(3, Assert.Single(later.ArmedRetests!).Zone.ZoneId);
    }

    // Frozen overlap/stop geometry survives a same-role merge, while targets and minimum zone count use the live start snapshot.
    [Fact]
    public void RetestEntry_MergeUsesFrozenSourceAndCurrentTargetVersion()
    {
        var arm = Arm(1);
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (evaluator, initial) = Prime([arm], [target]);
        for (var minute = 15; minute < 30; minute += 5)
            Assert.Null(Evaluate(evaluator, initial, Bar(minute, 102, 103, 101, 102)).Signal);
        var active = arm.Zone with { Version = 4, Bottom = 98, Top = 102, KnownAtUtc = Open.AddMinutes(30), PivotCount = 3 };
        var currentTarget = target with { Version = 2, Bottom = 103.5m, KnownAtUtc = Open.AddMinutes(30) };
        var snapshot = Snapshot(30, [active, currentTarget]);
        var bar = Bar(30, 102, 103, 99.8m, 101);
        var decision = Evaluate(evaluator, snapshot, bar);
        var signal = Assert.Single(decision.OrderedAlternatives!);
        Assert.Equal(3, signal.Evidence.Zone.Version);
        Assert.Equal(4, signal.Evidence.Retest!.ActiveSourceAtConfirmationStart.Version);
        Assert.Equal(100m, signal.Evidence.Zone.Top);
        Assert.Equal(98.7m, signal.InitialStopPrice);
        Assert.Equal(currentTarget, signal.Evidence.TargetZone);
        Assert.Equal(103.5m, signal.FixedTargetPrice);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(decision, bar, bar.Timestamp.AddMinutes(5), Indicator(bar),
            Bar(25, 102, 103, 101, 102), Rules()));
    }

    // The active source contributes to the minimum count but is excluded as a target even after its live bottom moves above the entry.
    [Fact]
    public void RetestEntry_ExcludesSourceIdAndKeepsPendingFallbackWithoutInventedTarget()
    {
        var arm = Arm(1);
        var (evaluator, snapshot) = Prime([arm], [Zone(99, IntradayZoneRole.Support, 90, 91)]);
        var signal = Evaluate(evaluator, snapshot, Bar(15, 102, 103, 99.8m, 101)).Signal!;
        Assert.NotNull(signal);
        Assert.Null(signal.FixedTargetPrice);
        Assert.Null(signal.Evidence.TargetZone);
        Assert.Equal("risk_derived", signal.Evidence.TargetPlan!.TargetOrigin);
        Assert.Null(signal.Evidence.TargetPlan.PlannedQuantity);
    }

    // A strict close below the frozen bottom cancels; equality keeps the arm for a later reclaim.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestEntry_FrozenBottomInvalidationIsStrict(bool through)
    {
        var (evaluator, snapshot) = Prime([Arm(1)], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var edgeBar = Bar(15, 100, 101, 98, through ? 98.99m : 99m);
        var edge = Evaluate(evaluator, snapshot, edgeBar);
        Assert.DoesNotContain(edge.OrderedAlternatives ?? [], signal => signal.Direction == "long");
        if (through)
        {
            Assert.Contains(edge.RetestCancellations!, item => item.Reason == "breakout_retest_close_through_frozen_edge");
            Assert.Equal("short", edge.Signal!.Direction);
            Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(edge, edgeBar, edgeBar.Timestamp.AddMinutes(5),
                Indicator(edgeBar), Bar(10, 102, 103, 101, 102), Rules()));
        }
        else Assert.Null(edge.Signal);
        var later = Evaluate(evaluator, snapshot, Bar(20, 102, 103, 99.8m, 101));
        Assert.Equal(!through, later.Signal?.Direction == "long");
    }

    // A continuous prior-close/next-open sequence drops old arm/consumption state and requires a fresh same-session upward flip.
    [Fact]
    public void RetestEntry_NewSessionRequiresNewFlipAndDropsConsumedEvidence()
    {
        var arm = Arm(1);
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (evaluator, snapshot) = Prime([arm], [target]);
        Assert.NotNull(Evaluate(evaluator, snapshot, Bar(15, 102, 103, 99.8m, 101)).Signal);
        IntradayZoneDecisionContext? priorClose = null;
        for (var minute = 20; minute < 390; minute += 5)
            priorClose = Evaluate(evaluator, snapshot, Bar(minute, 102, 103, 101, 102));
        Assert.Single(priorClose!.Trace!.RetestConsumptions!);
        var nextOpen = Open.AddDays(1);
        var nextDate = Day.AddDays(1);
        var nextSession = new IntradayZoneSessionContext(nextDate, nextOpen, nextOpen.AddHours(6.5),
            [Day, Day.AddDays(-1), Day.AddDays(-4), Day.AddDays(-5), Day.AddDays(-6)], Open.AddHours(6.5));
        var before = arm.Zone with { Role = IntradayZoneRole.Resistance, Version = 4, KnownAtUtc = nextOpen };
        var nextSnapshot = new IntradayZoneSnapshot("CRDO", nextOpen, nextDate, [before, target], []);
        // Shift only fixture clocks; all source/feed values remain on the same causal symbol stream.
        OhlcvBar NextBar(int minute, bool touch = false) => Bar(minute, 102, 103, touch ? 99.8m : 101m, touch ? 101m : 102m)
            with { Timestamp = nextOpen.AddMinutes(minute), KnownAtUtc = nextOpen.AddMinutes(minute + 5) };
        // Use the next authoritative exchange session instead of pretending a UTC date change alone establishes continuity.
        IntradayZoneDecisionContext EvaluateNext(IntradayZoneSnapshot state, OhlcvBar bar) =>
            evaluator.EvaluateCompleted5m(state, bar, Indicator(bar), nextSession, bar.Timestamp.AddMinutes(5), .01m);
        var first = EvaluateNext(nextSnapshot, NextBar(0, touch: true));
        Assert.Null(first.Signal);
        Assert.Empty(first.ArmedRetests!);
        Assert.Empty(first.Trace!.RetestConsumptions!);
        Assert.Equal("python_zone_previous_same_session_bar_unavailable", first.RejectionReason);
        EvaluateNext(nextSnapshot, NextBar(5));
        var after = before with { Role = IntradayZoneRole.Support, Version = 5, KnownAtUtc = nextOpen.AddMinutes(15) };
        var flip = NextBar(0) with { Timeframe = "15m", Open = 100, Low = 99, Close = 103, High = 104, KnownAtUtc = nextOpen.AddMinutes(15) };
        var transition = new IntradayZoneTransition(before, after, nextOpen.AddMinutes(15), true, 1m, flip);
        var flipped = nextSnapshot with { AsOfUtc = nextOpen.AddMinutes(15), Zones = [after, target], Transitions = [transition] };
        evaluator.ObserveZoneUpdate(new(flipped));
        Assert.Null(EvaluateNext(nextSnapshot, NextBar(10)).Signal);
        var nextRetest = EvaluateNext(flipped, NextBar(15, touch: true));
        Assert.Equal("breakout_retest", nextRetest.Signal!.Pattern);
        Assert.Equal(5, nextRetest.Signal.Evidence.Zone.Version);
        Assert.Equal(nextOpen.AddMinutes(20), Assert.Single(nextRetest.Trace!.RetestConsumptions!).EmittedAtUtc);
    }

    // A same-close fifteen-minute expiry cannot rewrite the consumption proof attached to the five-minute candle that just completed.
    [Fact]
    public void RetestEntry_ConsumedExpiryTraceIsIndependentOfSameCloseArrivalOrder()
    {
        var arm = Arm(1);
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (first, snapshot) = Prime([arm], [target]);
        var (second, _) = Prime([arm], [target]);
        var confirmation = Bar(15, 102, 103, 99.8m, 101);
        Evaluate(first, snapshot, confirmation);
        Evaluate(second, snapshot, confirmation);
        Evaluate(first, snapshot, Bar(20, 102, 103, 101, 102));
        Evaluate(second, snapshot, Bar(20, 102, 103, 101, 102));
        var expired = Snapshot(30, [target]);
        first.ObserveZoneUpdate(new(expired));
        var fifteenFirst = Evaluate(first, snapshot, Bar(25, 102, 103, 101, 102));
        var fiveFirst = Evaluate(second, snapshot, Bar(25, 102, 103, 101, 102));
        second.ObserveZoneUpdate(new(expired));
        Assert.Equal(JsonSerializer.Serialize(fiveFirst), JsonSerializer.Serialize(fifteenFirst));
        Assert.Single(fifteenFirst.Trace!.RetestConsumptions!);
        Assert.Contains(fifteenFirst.Trace.Alternatives, item => item.Reason == "breakout_retest_source_consumed_at_emission");
        var nextFirst = Evaluate(first, expired, Bar(30, 102, 103, 101, 102));
        var nextSecond = Evaluate(second, expired, Bar(30, 102, 103, 101, 102));
        Assert.Equal(JsonSerializer.Serialize(nextSecond), JsonSerializer.Serialize(nextFirst));
        Assert.Empty(nextFirst.Trace!.RetestConsumptions!);
    }

    // A target may predate confirmation yet still be absent from the supplied older snapshot; matching substituted signals cannot legitimize it.
    [Fact]
    public void RetestEntry_PureGuardRejectsTargetKnownAfterStartSnapshot()
    {
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (evaluator, snapshot) = Prime([Arm(1)], [target]);
        var previous = Bar(15, 102, 103, 101, 102);
        Evaluate(evaluator, snapshot, previous);
        var bar = Bar(20, 102, 103, 99.8m, 101);
        var decision = Evaluate(evaluator, snapshot, bar);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(decision, bar, bar.Timestamp.AddMinutes(5), Indicator(bar), previous, Rules()));
        var substituted = target with { KnownAtUtc = bar.Timestamp };
        var signal = decision.Signal! with { Evidence = decision.Signal.Evidence with { TargetZone = substituted } };
        var forged = decision with { Snapshot = snapshot with { Zones = snapshot.Zones.Select(zone => zone.ZoneId == 99 ? substituted : zone).ToArray() },
            Signal = signal, OrderedAlternatives = [signal] };
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(forged, bar, bar.Timestamp.AddMinutes(5), Indicator(bar), previous, Rules()));
    }

    // Expiration/opposite role cancels using the contemporaneous start snapshot; frozen evidence cannot revive a missing live source.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestEntry_RequiresActiveSupportIdentity(bool oppositeRole)
    {
        var arm = Arm(1);
        var target = Zone(99, IntradayZoneRole.Resistance, 104, 105);
        var (evaluator, _) = Prime([arm], [target]);
        var snapshot = Snapshot(15, oppositeRole ? [arm.Zone with { Role = IntradayZoneRole.Resistance }, target] : [target]);
        var result = Evaluate(evaluator, snapshot, Bar(15, 102, 103, 99.8m, 101));
        Assert.Null(result.Signal);
        Assert.Contains(result.RetestCancellations!, item => item.Reason == (oppositeRole ? "breakout_retest_source_role_changed" : "breakout_retest_source_expired"));
    }

    // Last-entry equality is executable; a later decision expires the unconsumed arm, independently of the 15:55 liquidation.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestEntry_CutoffIsInclusive(bool later)
    {
        var (evaluator, snapshot) = Prime([Arm(1)], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        for (var minute = 15; minute < (later ? 380 : 375); minute += 5)
            Evaluate(evaluator, snapshot, Bar(minute, 102, 103, 101, 102));
        var result = Evaluate(evaluator, snapshot, Bar(later ? 380 : 375, 102, 103, 99.8m, 101));
        Assert.Equal(!later, result.Signal?.Direction == "long");
        if (later) Assert.Contains(result.RetestCancellations!, item => item.Reason == "breakout_retest_entry_cutoff_expired");
        else Assert.Equal(Open.AddMinutes(380), result.Signal!.EntryCutoffUtc);
    }

    // Supplied causal proof is checked without mutating state; altered transition, active source or target snapshot evidence is rejected.
    [Theory]
    [InlineData("created_at_flip")]
    [InlineData("flip_close")]
    [InlineData("active_role")]
    [InlineData("future_snapshot")]
    [InlineData("absent_arms")]
    public void RetestEntry_PureGuardRejectsForgedEvidence(string mutation)
    {
        var (evaluator, snapshot) = Prime([Arm(1)], [Zone(99, IntradayZoneRole.Resistance, 104, 105)]);
        var bar = Bar(15, 102, 103, 99.8m, 101);
        var decision = Evaluate(evaluator, snapshot, bar);
        var arms = decision.ArmedRetests!.ToArray();
        if (mutation == "created_at_flip") arms[0] = arms[0] with { Transition = arms[0].Transition with { Before = arms[0].Transition.Before with { CreatedAtUtc = Open.AddMinutes(15) } } };
        if (mutation == "flip_close") arms[0] = arms[0] with { Transition = arms[0].Transition with { FlipBar = arms[0].Transition.FlipBar! with { Close = 100m } } };
        decision = decision with { ArmedRetests = mutation == "absent_arms" ? null : arms };
        if (mutation == "active_role") decision = decision with { Snapshot = snapshot with { Zones = snapshot.Zones.Select(zone => zone.ZoneId == 1 ? zone with { Role = IntradayZoneRole.Resistance } : zone).ToArray() } };
        if (mutation == "future_snapshot") decision = decision with { Snapshot = snapshot with { AsOfUtc = bar.Timestamp.AddMinutes(5) } };
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(decision, bar, bar.Timestamp.AddMinutes(5), Indicator(bar), Bar(10, 102, 103, 101, 102), Rules()));
    }

    // Selecting the new long sequence leaves every existing short alternative and its fixed economics unchanged.
    [Fact]
    public void RetestEntry_ShortParityWithControlD()
    {
        var rules = Rules();
        var control = new PythonZoneComparisonEvaluator(rules with { PythonVolumeSpreadComparison = rules.PythonVolumeSpreadComparison! with { LongEntryMode = null } });
        var treatment = new PythonZoneComparisonEvaluator(rules);
        var snapshot = Snapshot(0, [Zone(1, IntradayZoneRole.Resistance, 94, 95), Zone(2, IntradayZoneRole.Support, 99, 100)]);
        var previous = Bar(0, 100, 102, 99, 101);
        var current = Bar(5, 100, 101, 97, 98);
        Evaluate(control, snapshot, previous);
        Evaluate(treatment, snapshot, previous);
        var old = Evaluate(control, snapshot, current);
        var result = Evaluate(treatment, snapshot, current);
        Assert.Equal(old.Signal, result.Signal);
        Assert.Equal(old.OrderedAlternatives, result.OrderedAlternatives);
    }

    // Replay continuous completed prefixes before exposing the completed flip; the candle ending at the flip cannot use it.
    internal static (PythonZoneComparisonEvaluator Evaluator, IntradayZoneSnapshot Snapshot) Prime(
        IntradayZoneArmedRetestEvidence[] arms, IntradayZoneEvidence[] targets)
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules());
        var before = Snapshot(0, arms.Select(arm => arm.Transition.Before).Concat(targets).ToArray());
        Evaluate(evaluator, before, Bar(0, 102, 103, 101, 102));
        Evaluate(evaluator, before, Bar(5, 102, 103, 101, 102));
        var current = Snapshot(15, arms.Select(arm => arm.Zone).Concat(targets).ToArray()) with { Transitions = arms.Select(arm => arm.Transition).ToArray() };
        evaluator.ObserveZoneUpdate(new(current));
        Assert.Null(Evaluate(evaluator, before, Bar(10, 102, 103, 101, 102)).Signal);
        return (evaluator, current);
    }

    // Use the registered ordinary recipe; tests do not create run-specific executable paths or mutate its identity.
    internal static IntradayZoneRules Rules() => new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "zone-breakout-retest.bt-v1.yaml")).EntryRules.IntradayZone!;

    // Freeze a preexisting resistance and a genuine completed upward flip with the same source/feed as five-minute confirmation.
    internal static IntradayZoneArmedRetestEvidence Arm(long id, decimal bottom = 99, decimal top = 100)
    {
        var before = Zone(id, IntradayZoneRole.Resistance, bottom, top) with { Version = 2 };
        var after = before with { Version = 3, Role = IntradayZoneRole.Support, KnownAtUtc = Open.AddMinutes(15) };
        var flip = Bar(0, 100, 104, 99, 103) with { Timeframe = "15m", KnownAtUtc = Open.AddMinutes(15) };
        return new(new(before, after, Open.AddMinutes(15), true, 1, flip), Day);
    }

    // Keep source geometry available before the first fixture candle with enough pivot observations for the approved recipe.
    internal static IntradayZoneEvidence Zone(long id, IntradayZoneRole role, decimal bottom, decimal top) => new(id, 1, role, bottom, top, 2,
        Open.AddMinutes(-60), Open.AddMinutes(-15), Open.AddMinutes(-90), Day.AddDays(-1),
        new(IntradayZonePivotKind.Peak, Open.AddMinutes(-90), Open.AddMinutes(-75), Open.AddMinutes(-15), Day.AddDays(-1), bottom, top));

    // Each snapshot contains only the caller-supplied immutable source versions, with explicit availability.
    internal static IntradayZoneSnapshot Snapshot(int minute, IntradayZoneEvidence[] zones) => new("CRDO", Open.AddMinutes(minute), Day, zones, []);

    // The authoritative session retains D's 15:50 entry and separate 15:55 liquidation clocks.
    internal static IntradayZoneSessionContext Session() => new(Day, Open, Open.AddHours(6.5), [Day.AddDays(-1), Day.AddDays(-4), Day.AddDays(-5), Day.AddDays(-6), Day.AddDays(-7)]);

    // Completed raw SIP fixture observations never touch provider caches or operator datasets.
    internal static OhlcvBar Bar(int minute, decimal open, decimal high, decimal low, decimal close) => new("CRDO", Open.AddMinutes(minute), "5m", open, high, low, close, 120, "sip", "raw", Open.AddMinutes(minute + 5));

    // Valid historical volume remains mandatory while the existing long threshold is diagnostic only.
    internal static IndicatorSnapshot Indicator(OhlcvBar bar) => new(bar.Ticker, bar.Timestamp, bar.Timeframe, bar.Close, bar.Volume,
        null, null, 2m, null, null, null, null, null, null, null, null, null, null,
        SlotRelativeVolume: 1.2m, SlotMedianVolume: 100m, SlotRelativeVolumeSampleCount: 20,
        MarketEvidenceProfileVersion: MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort: "regular", RelativeVolumeMinimumSamples: 20,
        DataFeed: bar.DataFeed, AdjustmentPolicy: bar.AdjustmentPolicy, MarketEvidenceReliability: "verified_same_feed");

    // All focused cases use the same public completed-bar evaluator path and exchange clocks.
    internal static IntradayZoneDecisionContext Evaluate(PythonZoneComparisonEvaluator evaluator, IntradayZoneSnapshot snapshot, OhlcvBar bar) =>
        evaluator.EvaluateCompleted5m(snapshot, bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(5), .01m);
}
