using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class PythonZoneComparisonTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 27, 13, 30, 0, TimeSpan.Zero);
    private static IntradayZoneRules Rules => new(IntradayZoneRules.PythonComparisonPolicyId,
        PythonComparison: new(2, 2, .15m, .5m, 2m, 1.2m, .15m, 30));

    // A long uses the previous low's literal one-sided dollar tolerance and does not require RVOL evidence.
    [Fact]
    public void Long_UsesPreviousLowWithoutOverlapAndOptionalRvol()
    {
        var previous = Bar(0, 95, 96, 94, 95);
        var current = Bar(5, 100, 102, 100, 101);
        var result = Evaluate(previous, current, Indicator(current) with { Vwap = 102, SlotRelativeVolume = null, SlotMedianVolume = null });
        var signal = Assert.IsType<IntradayZoneSignal>(result.Signal);
        Assert.Equal("long", signal.Direction);
        Assert.Equal("python_support_bounce", signal.Pattern);
        Assert.Null(signal.Evidence.SlotRelativeVolume);
        Assert.Equal(98.7m, signal.InitialStopPrice);
        Assert.Equal(104m, signal.FixedTargetPrice);
        Assert.Equal(2m, signal.Evidence.Atr);
        Assert.Equal(previous, signal.Evidence.PreviousBar);
        Assert.Equal(2, signal.Evidence.TargetZone!.ZoneId);
    }

    // RVOL equality is admitted for shorts and a support box can break down without a recorded flip or retest.
    [Fact]
    public void Short_AcceptsSupportRoleAndInclusiveRvolWithoutFlip()
    {
        var previous = Bar(0, 101, 102, 99, 100);
        var current = Bar(5, 100, 101, 97, 98);
        var lower = Zone(1, IntradayZoneRole.Resistance, 94, 95);
        var support = Zone(2, IntradayZoneRole.Support, 99, 100);
        var result = Evaluate(previous, current, Indicator(current, 100) with { SlotRelativeVolume = 1.2m, SlotMedianVolume = 100 }, lower, support);
        Assert.Equal("short", result.Signal!.Direction);
        Assert.Equal("python_zone_breakdown", result.Signal.Pattern);
        Assert.Null(result.Signal.Evidence.FlippedAtUtc);
        Assert.Equal(100.3m, result.Signal.InitialStopPrice);
        Assert.Equal(95m, result.Signal.FixedTargetPrice);
    }

    // The target is last in bottom order, not the greatest lower top or nearest geometric edge.
    [Fact]
    public void Short_TargetPreservesBottomOrderInsteadOfNearestTop()
    {
        var previous = Bar(0, 101, 102, 100, 101);
        var current = Bar(5, 100, 100, 97, 98);
        var highTop = Zone(1, IntradayZoneRole.Resistance, 90, 97);
        var laterBottom = Zone(2, IntradayZoneRole.Resistance, 94, 95);
        var source = Zone(3, IntradayZoneRole.Support, 100, 101);
        var result = Evaluate(previous, current, Indicator(current, 100), source, laterBottom, highTop);
        Assert.Equal(2, result.Signal!.Evidence.TargetZone!.ZoneId);
        Assert.Equal(95m, result.Signal.FixedTargetPrice);
    }

    // Lowest-bottom support wins even when a different candidate lies closer to the confirmation close.
    [Fact]
    public void Long_SourceAndTargetUseBottomOrder()
    {
        var current = Bar(5, 100, 103, 100, 102);
        var first = Zone(1, IntradayZoneRole.Support, 97, 99);
        var nearer = Zone(2, IntradayZoneRole.Support, 100, 101);
        var higher = Zone(3, IntradayZoneRole.Support, 104, 105);
        var far = Zone(4, IntradayZoneRole.Resistance, 110, 111);
        var result = Evaluate(Bar(0, 98, 99, 98, 98), current, Indicator(current, 102), far, nearer, higher, first);
        Assert.Equal(1, result.Signal!.Evidence.Zone.ZoneId);
        Assert.Equal(3, result.Signal.Evidence.TargetZone!.ZoneId);
    }

    // A single known, sufficiently merged box is not enough; target candidates have the same eligibility filter.
    [Theory]
    [InlineData("count")]
    [InlineData("future")]
    [InlineData("missing")]
    public void IneligibleTarget_CannotCreateOrder(string reason)
    {
        var zones = DefaultZones();
        if (reason == "count") zones[1] = zones[1] with { PivotCount = 1 };
        if (reason == "future") zones[1] = zones[1] with { KnownAtUtc = Open.AddMinutes(10) };
        if (reason == "missing") zones = zones.Take(1).ToArray();
        var bar = Bar(5, 100, 102, 100, 101);
        Assert.Null(Evaluate(Bar(0, 100, 101, 99, 100), bar, Indicator(bar), zones).Signal);
    }

    // Missing ATR or VWAP never substitutes zero or silently disables the distance gate.
    [Theory]
    [InlineData("atr")]
    [InlineData("vwap")]
    [InlineData("zero_atr")]
    public void MissingPriceIndicators_FailClosed(string field)
    {
        var bar = Bar(5, 100, 102, 100, 101);
        var indicator = Indicator(bar);
        indicator = field switch { "atr" => indicator with { Atr = null }, "zero_atr" => indicator with { Atr = 0 }, _ => indicator with { Vwap = null } };
        var result = Evaluate(Bar(0, 100, 101, 99, 100), bar, indicator);
        Assert.Null(result.Signal);
        Assert.Equal("python_zone_atr_or_vwap_unavailable", result.RejectionReason);
    }

    // No consumption or departure/rearm gate is added to repeated qualified completed candles.
    [Fact]
    public void ConsecutiveLongs_DoNotRequireFreshTouchOrConsumeZone()
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var snapshot = Snapshot(DefaultZones());
        var previous = Bar(0, 99, 101, 99, 100);
        evaluator.EvaluateCompleted5m(snapshot, previous, Indicator(previous), Session(), Open.AddMinutes(5), .01m);
        var first = Bar(5, 100, 102, 99, 101);
        var second = first with { Timestamp = Open.AddMinutes(10) };
        Assert.NotNull(evaluator.EvaluateCompleted5m(snapshot, first, Indicator(first), Session(), Open.AddMinutes(10), .01m).Signal);
        Assert.NotNull(evaluator.EvaluateCompleted5m(snapshot, second, Indicator(second), Session(), Open.AddMinutes(15), .01m).Signal);
    }

    // Dollar/ATR boundaries are inclusive while strict candle comparisons remain strict.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_PreviousLowToleranceHasDollarUnits(bool above)
    {
        var current = Bar(5, 100, 102, 100, 101);
        var previous = Bar(0, 100.2m, 100.3m, above ? 100.1501m : 100.15m, 100.2m);
        Assert.Equal(!above, Evaluate(previous, current, Indicator(current)).Signal is not null);
    }

    // Raw stops remain inspectable while submitted stops and targets use outward/toward-entry tick rounding.
    [Fact]
    public void FractionalLevels_RoundOnlyExecutableGeometry()
    {
        var zones = new[] { Zone(1, IntradayZoneRole.Support, 99.005m, 100), Zone(2, IntradayZoneRole.Resistance, 104.009m, 105) };
        var current = Bar(5, 100, 102, 100, 101);
        var result = Evaluate(Bar(0, 99, 100, 98, 99), current, Indicator(current), zones);
        Assert.Equal(98.705m, result.Signal!.Evidence.RawStopPrice);
        Assert.Equal(98.7m, result.Signal.InitialStopPrice);
        Assert.Equal(104.009m, result.Signal.Evidence.RawTargetPrice);
        Assert.Equal(104m, result.Signal.FixedTargetPrice);
    }

    // A conflict in fifteen-minute history poisons later entry intent and an identical five-minute transport retry.
    [Fact]
    public void UnknownZoneCoverage_PoisonsRetryAndLaterDecision()
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var previous = Bar(0, 99, 100, 98, 99);
        var current = Bar(5, 100, 102, 100, 101);
        var snapshot = Snapshot(DefaultZones());
        evaluator.EvaluateCompleted5m(snapshot, previous, Indicator(previous), Session(), Open.AddMinutes(5), .01m);
        Assert.NotNull(evaluator.EvaluateCompleted5m(snapshot, current, Indicator(current), Session(), Open.AddMinutes(10), .01m).Signal);
        evaluator.ObserveZoneUpdate(new(snapshot with { CoverageKnown = false }, "intraday_zone_duplicate_or_source_conflict"));
        Assert.Null(evaluator.EvaluateCompleted5m(snapshot, current, Indicator(current), Session(), Open.AddMinutes(10), .01m).Signal);
        var later = current with { Timestamp = Open.AddMinutes(10) };
        Assert.Null(evaluator.EvaluateCompleted5m(snapshot, later, Indicator(later), Session(), Open.AddMinutes(15), .01m).Signal);
    }

    // Every gap is unknown source evidence, never an inferred absence of a price touch.
    [Fact]
    public void MissingFiveMinuteSlot_SuppressesSuffix()
    {
        var current = Bar(10, 100, 102, 100, 101);
        var result = Evaluate(Bar(0, 99, 100, 98, 99), current, Indicator(current));
        Assert.Equal("intraday_zone_5m_or_15m_coverage_unknown", result.RejectionReason);
    }

    // Only the recipe's inclusive close-relative entry cutoff is used; liquidation remains five minutes before close.
    [Fact]
    public void Cutoff_EqualityAllowedLaterConfirmationRejected()
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var session = Session() with { CloseUtc = Open.AddMinutes(45) };
        var snapshot = Snapshot(DefaultZones());
        IntradayZoneDecisionContext? result = null;
        for (var slot = 0; slot < 4; slot++)
        {
            var bar = Bar(slot * 5, 100, 102, 99, 101);
            result = evaluator.EvaluateCompleted5m(snapshot, bar, Indicator(bar), session, bar.Timestamp.AddMinutes(5), .01m);
            if (slot == 2)
            {
                Assert.NotNull(result.Signal);
                Assert.Equal(Open.AddMinutes(15), result.Signal.EntryCutoffUtc);
                Assert.Equal(Open.AddMinutes(40), result.Signal.SessionDeadlineUtc);
            }
        }
        Assert.Null(result!.Signal);
        Assert.Equal("outside_python_zone_entry_window", result.RejectionReason);
    }

    // The pure kernel verifier reselects the source/target and checks the exact independently supplied previous candle.
    [Fact]
    public void Guard_RejectsForgedTargetIndicatorAndPreviousBar()
    {
        var previous = Bar(0, 99, 100, 98, 99);
        var bar = Bar(5, 100, 102, 100, 101);
        var indicator = Indicator(bar);
        var result = Evaluate(previous, bar, indicator);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(result, bar, Open.AddMinutes(10), indicator, previous, Rules));
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(result with { Signal = result.Signal! with { FixedTargetPrice = 999 } }, bar, Open.AddMinutes(10), indicator, previous, Rules));
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(result, bar, Open.AddMinutes(10), indicator with { Atr = 4 }, previous, Rules));
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(result, bar, Open.AddMinutes(10), indicator, previous with { Low = 97 }, Rules));
    }

    // The first candle of a new RTH session cannot borrow its previous-bar condition from yesterday.
    [Fact]
    public void SessionOpen_RequiresLaterSameSessionPreviousBar()
    {
        var bar = Bar(0, 100, 102, 99, 101);
        var result = new PythonZoneComparisonEvaluator(Rules).EvaluateCompleted5m(Snapshot(DefaultZones()), bar,
            Indicator(bar), Session(), Open.AddMinutes(5), .01m);
        Assert.Equal("python_zone_previous_same_session_bar_unavailable", result.RejectionReason);
    }

    // Short missing-volume evidence stays visible while no such requirement is added to longs.
    [Fact]
    public void Short_MissingRvolHasSpecificUnavailableReason()
    {
        var current = Bar(5, 100, 100, 97, 98);
        var result = Evaluate(Bar(0, 100, 102, 99, 101), current,
            Indicator(current, 100) with { SlotRelativeVolume = null, SlotMedianVolume = null },
            Zone(1, IntradayZoneRole.Resistance, 94, 95), Zone(2, IntradayZoneRole.Support, 99, 100));
        Assert.Null(result.Signal);
        Assert.Equal("python_zone_short_volume_unavailable", result.RejectionReason);
    }

    // The coordinator must propagate a bad 15m prefix even when that failed update is not retained as a snapshot.
    [Fact]
    public void Coordinator_ConflictingFifteenMinuteUpdatePoisonsPreviousHealthySnapshot()
    {
        var evaluator = new IntradayZoneEvaluator(Rules);
        for (var minute = 0; minute < 15; minute += 5)
        {
            var bar = Bar(minute, 100, 102, 99, 101);
            evaluator.ProcessCompleted5m(bar, Indicator(bar), Session(), bar.Timestamp.AddMinutes(5), .01m);
        }
        var quarter = Bar(0, 100, 102, 99, 101) with { Timeframe = "15m" };
        evaluator.ProcessCompleted15m(quarter, Indicator(quarter), Session(), Open.AddMinutes(15));
        var failure = evaluator.ProcessCompleted15m(quarter with { Volume = 999 }, Indicator(quarter), Session(), Open.AddMinutes(15));
        Assert.False(failure.Snapshot.CoverageKnown);
        var next = Bar(15, 100, 102, 99, 101);
        var decision = evaluator.ProcessCompleted5m(next, Indicator(next), Session(), Open.AddMinutes(20), .01m);
        Assert.Null(decision.Signal);
        Assert.Equal("intraday_zone_5m_or_15m_coverage_unknown", decision.RejectionReason);
    }

    // A later forged confirmation remains outside an early-close entry window even if its source and target are copied.
    [Fact]
    public void Guard_UsesActualEarlyCloseAndActualOpenEvidence()
    {
        var session = Session() with { CloseUtc = Open.AddMinutes(45) };
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var snapshot = Snapshot(DefaultZones());
        IntradayZoneDecisionContext? decision = null;
        for (var slot = 0; slot < 3; slot++)
        {
            var bar = Bar(slot * 5, 100, 102, 99, 101);
            decision = evaluator.EvaluateCompleted5m(snapshot, bar, Indicator(bar), session, bar.Timestamp.AddMinutes(5), .01m);
        }
        var actual = decision!.Signal!;
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(decision, actual.ConfirmationBar, actual.ActiveAtUtc,
            Indicator(actual.ConfirmationBar), actual.Evidence.PreviousBar, Rules));
        var lateBar = actual.ConfirmationBar with { Timestamp = Open.AddMinutes(15) };
        var late = actual with { ConfirmationBar = lateBar, ActiveAtUtc = Open.AddMinutes(20),
            Evidence = actual.Evidence with { ConfirmationCompletedAtUtc = Open.AddMinutes(20), PreviousBar = actual.ConfirmationBar } };
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(decision with { Signal = late }, lateBar, Open.AddMinutes(20),
            Indicator(lateBar), actual.ConfirmationBar, Rules));
        Assert.False(PythonZoneComparisonEvaluator.MatchesDecision(decision with { Signal = actual with {
            Evidence = actual.Evidence with { SessionOpenUtc = actual.ConfirmationBar.Timestamp } } }, actual.ConfirmationBar,
            actual.ActiveAtUtc, Indicator(actual.ConfirmationBar), actual.Evidence.PreviousBar, Rules));
    }

    // Policy identity and frozen numeric values remain explicit and parser omission cannot substitute engine defaults.
    [Fact]
    public void Recipe_HasSeparateFixedExitPolicyAndNoContinuation()
    {
        var root = FindRoot();
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(root, "configs/backtest/strategies/python-zone-comparison.bt-v1.yaml"));
        Assert.Equal("technical.zone-python-comparison", strategy.StrategyId);
        Assert.True(strategy.EntryRules.IntradayZone!.IsPythonComparison);
        Assert.Equal(Rules.PythonComparison, strategy.EntryRules.IntradayZone.PythonComparison);
        Assert.Equal("zone_atr", strategy.ExitRules.InitialStopMode);
        Assert.Equal("next_zone", strategy.ExitRules.ProfitTargetMode);
        Assert.Null(strategy.ExitRules.ContinuationProtection);
        Assert.Equal("raw", MarketEvidenceOperationalRules.ForExecutionTimingPolicy(IntradayZoneRules.PythonComparisonTimingPolicy).RequiredAdjustmentPolicy);
        Assert.Throws<InvalidOperationException>(() => (strategy.EntryRules.IntradayZone with { PythonComparison = null }).Validate(strategy));
        Assert.Throws<InvalidOperationException>(() => strategy.EntryRules.IntradayZone.Validate(strategy with { ExitRules = strategy.ExitRules with { TargetRMultiple = 3 } }));
    }

    // Replay the first observed slot so chronological completeness is established before evaluating the requested candle.
    private static IntradayZoneDecisionContext Evaluate(OhlcvBar previous, OhlcvBar current, IndicatorSnapshot indicator, params IntradayZoneEvidence[] zones)
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var snapshot = Snapshot(zones.Length == 0 ? DefaultZones() : zones);
        evaluator.EvaluateCompleted5m(snapshot, previous, Indicator(previous), Session(), previous.Timestamp.AddMinutes(5), .01m);
        return evaluator.EvaluateCompleted5m(snapshot, current, indicator, Session(), current.Timestamp.AddMinutes(5), .01m);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "configs/strategy-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static IntradayZoneEvidence[] DefaultZones() => [Zone(1, IntradayZoneRole.Support, 99, 100), Zone(2, IntradayZoneRole.Resistance, 104, 105)];
    private static IntradayZoneSnapshot Snapshot(params IntradayZoneEvidence[] zones) => new("CRDO", Open, new(2026, 10, 27), zones, []);
    private static IntradayZoneEvidence Zone(long id, IntradayZoneRole role, decimal bottom, decimal top) => new(id, 1, role, bottom, top,
        2, Open.AddMinutes(-15), Open, Open.AddMinutes(-75), new(2026, 10, 27),
        new(IntradayZonePivotKind.Valley, Open.AddMinutes(-75), Open.AddMinutes(-60), Open.AddMinutes(-15), new(2026, 10, 27), bottom, top));
    private static IntradayZoneSessionContext Session() => new(new(2026, 10, 27), Open, Open.AddHours(6.5),
        [new(2026, 10, 26), new(2026, 10, 23), new(2026, 10, 22), new(2026, 10, 21), new(2026, 10, 20)]);
    private static OhlcvBar Bar(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new("CRDO", Open.AddMinutes(minute), "5m", open, high, low, close, 120m, "sip", "raw");
    private static IndicatorSnapshot Indicator(OhlcvBar bar, decimal vwap = 101) => new(bar.Ticker, bar.Timestamp, bar.Timeframe,
        bar.Close, bar.Volume, vwap, null, 2, null, null, null, null, null, null, null, null, null, null,
        SlotRelativeVolume: 1.2m, SlotMedianVolume: 100, SlotRelativeVolumeSampleCount: 20,
        MarketEvidenceProfileVersion: MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort: "regular",
        RelativeVolumeMinimumSamples: 20, DataFeed: bar.DataFeed, AdjustmentPolicy: bar.AdjustmentPolicy, MarketEvidenceReliability: "verified_same_feed");
}
