using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed partial class PythonZoneVolumeSpreadComparisonTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 27, 13, 30, 0, TimeSpan.Zero);
    private static IntradayZoneRules Rules => new(IntradayZoneRules.PythonVolumeSpreadComparisonPolicyId,
        PythonVolumeSpreadComparison: new(2, 2, .15m, 1.2m, .15m, 10, 10m, 20m, 1.5m));

    // Long diagnostic volume permits low but valid RVOL, while short breakdowns retain the same threshold under both modes.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PatternVolumeMode_ChangesOnlyLongThreshold(bool diagnostic, bool shortSide, bool expected)
    {
        var rules = Treatment(diagnostic, false);
        var previous = shortSide ? Bar(0, 100m, 102m, 99m, 101m) : Bar(0, 99m, 100m, 98m, 99m);
        var current = shortSide ? Bar(5, 100m, 101m, 97m, 98m) : Bar(5, 100m, 102m, 100m, 101m);
        var indicator = Indicator(current) with { SlotRelativeVolume = .6m, SlotMedianVolume = 200m };
        var zones = shortSide ? new[] { Zone(1, IntradayZoneRole.Resistance, 94m, 95m), Zone(2, IntradayZoneRole.Support, 99m, 100m) } : DefaultZones();
        var result = EvaluateWith(rules, previous, current, indicator, Session(), zones);
        Assert.Equal(expected, result.Signal is not null);
        var trace = Assert.Single(result.Trace!.Alternatives, t => t.Zone.ZoneId == (shortSide ? 2 : 1) && t.Direction == (shortSide ? "short" : "long"));
        Assert.True(trace.VolumeEvidenceValid);
        Assert.False(trace.VolumeThresholdPassed);
        Assert.Equal(!diagnostic || shortSide, trace.VolumeThresholdRequired);
    }

    // Diagnostic volume never turns a missing baseline into an executable entry and retains the evaluated rejection directly.
    [Fact]
    public void DiagnosticVolume_MissingEvidenceStillRejectsAndTraces()
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var result = EvaluateWith(Treatment(true, false), Bar(0, 99m, 100m, 98m, 99m), current,
            Indicator(current) with { SlotRelativeVolume = null, SlotMedianVolume = null }, Session(), DefaultZones());
        Assert.Null(result.Signal);
        Assert.Equal("python_zone_volume_unavailable", result.Trace!.RejectionReason);
        Assert.All(result.Trace.Alternatives, t => Assert.False(t.VolumeEvidenceValid));
    }

    // Only the target package retains a qualified source with an absent next zone; it never invents a replacement box or early price.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetPackage_RetainsMissingTargetForAdmission(bool shortSide)
    {
        var current = shortSide ? Bar(5, 100m, 101m, 97m, 98m) : Bar(5, 100m, 102m, 100m, 101m);
        var previous = shortSide ? Bar(0, 100m, 102m, 99m, 101m) : Bar(0, 95m, 96m, 94m, 95m);
        IntradayZoneEvidence[] zones = shortSide ? [Zone(1, IntradayZoneRole.Support, 99m, 100m), Zone(2, IntradayZoneRole.Resistance, 110m, 111m)]
            : [Zone(1, IntradayZoneRole.Support, 90m, 91m), Zone(2, IntradayZoneRole.Support, 99m, 100m)];
        var old = EvaluateWith(Treatment(false, false), previous, current, Indicator(current), Session(), zones);
        Assert.Null(old.Signal);
        var treatment = EvaluateWith(Treatment(false, true), previous, current, Indicator(current), Session(), zones);
        var signal = Assert.Single(treatment.OrderedAlternatives!);
        Assert.Null(signal.FixedTargetPrice);
        Assert.Null(signal.Evidence.TargetZone);
        Assert.Null(signal.Evidence.RawTargetPrice);
        Assert.Equal("risk_derived", signal.Evidence.TargetPlan!.TargetOrigin);
        Assert.Null(signal.Evidence.TargetPlan.PlannedQuantity);
        Assert.Equal("risk_target_pending_sizing", Assert.Single(treatment.Trace!.Alternatives,
            t => t.Direction == signal.Direction && t.Zone.ZoneId == signal.Evidence.Zone.ZoneId).Reason);
        Assert.True(PythonZoneComparisonEvaluator.MatchesDecision(treatment, current, current.Timestamp.AddMinutes(5),
            Indicator(current), previous, Treatment(false, true)));
    }

    // The trace uses the same legal geometry as entry construction, including raw targets that become unfavourable after rounding.
    [Theory]
    [InlineData(false, "rounded_target_not_favourable")]
    [InlineData(true, "rounded_structural_stop_invalid")]
    public void Trace_PreservesExactRoundedGeometryRejection(bool badStop, string reason)
    {
        var current = Bar(5, 99.9m, 100.5m, 99.5m, 100m);
        var source = Zone(1, IntradayZoneRole.Support, badStop ? .1m : 99m, 99.5m);
        var target = Zone(2, IntradayZoneRole.Resistance, 100.051m, 101m);
        var result = EvaluateWith(Treatment(false, true), Bar(0, 99m, 100m, 98m, 99m), current, Indicator(current), Session(), source, target);
        Assert.Null(result.Signal);
        var trace = Assert.Single(result.Trace!.Alternatives, t => t.Zone.ZoneId == 1 && t.Direction == "long");
        Assert.Equal(reason, trace.Reason);
        Assert.Equal(.01m, trace.TickSize);
        Assert.Equal(100.051m, trace.RawTargetPrice);
        Assert.Equal(100.05m, trace.RoundedTargetPrice);
        if (badStop) Assert.True(trace.RoundedStopPrice <= 0m);
    }

    // Appending future candles cannot mutate an already published signal or its exact rejected-bar predicate evidence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraceAndSignal_ArePrefixInvariant(bool twoCandleStop)
    {
        var rules = Treatment(true, true);
        if (twoCandleStop) rules = rules with { PythonVolumeSpreadComparison = rules.PythonVolumeSpreadComparison! with {
            LongStopBasis = PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop } };
        var evaluator = new PythonZoneComparisonEvaluator(rules);
        var zones = Snapshot(DefaultZones());
        var previous = Bar(0, 99m, 100m, 98m, 99m);
        var current = Bar(5, 100m, 102m, 100m, 101m);
        evaluator.EvaluateCompleted5m(zones, previous, Indicator(previous), Session(), previous.Timestamp.AddMinutes(5), .01m);
        var prefix = evaluator.EvaluateCompleted5m(zones, current, Indicator(current), Session(), current.Timestamp.AddMinutes(5), .01m);
        var before = JsonSerializer.Serialize(prefix);
        var future = Bar(10, 200m, 300m, 1m, 250m);
        evaluator.EvaluateCompleted5m(zones, future, Indicator(future), Session(), future.Timestamp.AddMinutes(5), .01m);
        Assert.Equal(before, JsonSerializer.Serialize(prefix));
    }

    // Omitted optional selections preserve both previously published canonical strategy identities exactly.
    [Fact]
    public void OptionalTreatmentFields_PreserveOriginalCanonicalHashes()
    {
        var root = TestRepository.FindRoot();
        var artifacts = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader())
            .GetSelectable(StrategySelectionMode.Backtest);
        Assert.Equal("8da17b5cbe5a09051135bce2de3f6ba551a14dbfc5f23339898d454f3bdeb620",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.zone-volume-spread-no-rr").Identity.ContentSha256);
        Assert.Equal("b0ed33501f19b6850d82afe06ef22534a73f81a776f0b8afec9b2d6831316362",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.zone-volume-spread-comparison").Identity.ContentSha256);
        Assert.Equal("bdc4a74d5f5d5cce493e2d7ad25f67cf31f5d7bb24a5798ac9dfa8f940290329",
            Assert.Single(artifacts, a => a.Identity.StrategyId == "technical.zone-bounce-volume-risk-target").Identity.ContentSha256);
    }

    // Each approved comparison cell round-trips only its explicitly selected treatment fields through the ordinary recipe writer.
    [Theory]
    [InlineData("zone-bounce-volume.bt-v1.yaml", true, false)]
    [InlineData("zone-risk-target.bt-v1.yaml", false, true)]
    [InlineData("zone-bounce-volume-risk-target.bt-v1.yaml", true, true)]
    public void TreatmentRecipes_RoundTripExplicitSelections(string file, bool diagnostic, bool targets)
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies", file));
        var rules = strategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!;
        Assert.Equal(diagnostic, rules.LongVolumeIsDiagnostic);
        Assert.Equal(targets, rules.UsesFullCostTargets);
        Assert.Equal(targets ? (decimal?)2m : null, rules.FallbackNetRiskMultiple);
        var path = Path.Combine(Path.GetTempPath(), "tf-zone-treatments-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equivalent(strategy, reader.ReadStrategy(path), strict: true);
        }
        finally { File.Delete(path); }
        var control = reader.ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies", "python-zone-volume-spread-no-rr.bt-v1.yaml"));
        Assert.DoesNotContain("LongBounceVolumeMode", JsonSerializer.Serialize(control));
        Assert.DoesNotContain("TargetPolicy", JsonSerializer.Serialize(control));
        Assert.DoesNotContain("FallbackNetRiskMultiple", JsonSerializer.Serialize(control));
        Assert.Throws<InvalidOperationException>(() => (rules with { LongBounceVolumeMode = "unknown" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { TargetPolicy = "unknown" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (rules with { TargetPolicy = PythonZoneVolumeSpreadComparisonRules.ZoneOrFullCostRiskTarget,
            FallbackNetRiskMultiple = null }).Validate());
    }

    // Express the paired treatments through ordinary optional recipe selections without altering baseline arithmetic or sizing.
    private static IntradayZoneRules Treatment(bool diagnosticVolume, bool riskTarget) => Rules with {
        PythonVolumeSpreadComparison = Rules.PythonVolumeSpreadComparison! with { MinimumRewardRisk = 0m,
            LongBounceVolumeMode = diagnosticVolume ? PythonZoneVolumeSpreadComparisonRules.DiagnosticLongVolume : null,
            TargetPolicy = riskTarget ? PythonZoneVolumeSpreadComparisonRules.ZoneOrFullCostRiskTarget : null,
            FallbackNetRiskMultiple = riskTarget ? 2m : null } };

    // Volume-qualified longs ignore absent or arbitrarily distant VWAP and keep synthetic quotes separate from midpoint sizing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_RequiresVolumeAndNoVwap(bool absent)
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var decision = Evaluate(Bar(0, 95m, 96m, 94m, 95m), current, Indicator(current) with { Vwap = absent ? null : 1000m });
        var signal = Assert.Single(decision.OrderedAlternatives!);
        Assert.Equal(signal, decision.Signal);
        Assert.Equal(101m, signal.EntryReferencePrice);
        Assert.Equal(100.9495m, signal.Evidence.VolumeSpread!.BidPrice);
        Assert.Equal(101.0505m, signal.Evidence.VolumeSpread.AskPrice);
        Assert.Equal(10m, signal.Evidence.VolumeSpread.FullSpreadBps);
        Assert.Equal(98.7m, signal.InitialStopPrice);
        Assert.Equal(104m, signal.FixedTargetPrice);
        Assert.Null(signal.Evidence.VolumeSpread.PlannedQuantity);
    }

    // Both directions require inclusive verified same-clock RVOL; no zero/default volume substitutes for missing evidence.
    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "low")]
    [InlineData(true, "missing")]
    [InlineData(true, "low")]
    public void BothSides_RejectUnavailableOrLowRelativeVolume(bool shortSide, string failure)
    {
        var previous = shortSide ? Bar(0, 100m, 102m, 99m, 101m) : Bar(0, 99m, 100m, 98m, 99m);
        var current = shortSide ? Bar(5, 100m, 101m, 97m, 98m) : Bar(5, 100m, 102m, 100m, 101m);
        var indicator = Indicator(current) with { SlotRelativeVolume = failure == "missing" ? null : 1.19m,
            SlotMedianVolume = failure == "missing" ? null : 120m / 1.19m };
        // An exact ratio from the declared baseline distinguishes low participation from unavailable evidence.
        if (failure == "low") indicator = indicator with { SlotRelativeVolume = current.Volume / indicator.SlotMedianVolume };
        var result = Evaluate(previous, current, indicator, shortSide ?
            [Zone(1, IntradayZoneRole.Resistance, 94m, 95m), Zone(2, IntradayZoneRole.Support, 99m, 100m)] : DefaultZones());
        Assert.Null(result.Signal);
        Assert.Equal(failure == "missing" ? "python_zone_volume_unavailable" : "python_zone_relative_volume_below_minimum", result.RejectionReason);
    }

    // Keep an earlier poor-ratio geometry so chronological sizing can continue to a later alternative.
    [Fact]
    public void FreezeAllGeometricAlternativesWithoutPrematureRewardRiskSelection()
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var decision = Evaluate(Bar(0, 90m, 91m, 89m, 90m), current, Indicator(current),
            Zone(1, IntradayZoneRole.Support, 90m, 91m), Zone(2, IntradayZoneRole.Support, 99m, 100m),
            Zone(3, IntradayZoneRole.Resistance, 104m, 105m));
        Assert.Equal(2, decision.OrderedAlternatives!.Count);
        Assert.Equal(new long[] { 1, 2 }, decision.OrderedAlternatives.Select(signal => signal.Evidence.Zone.ZoneId));
        Assert.Equal(new[] { 0, 1 }, decision.OrderedAlternatives.Select(signal => signal.Evidence.VolumeSpread!.OrderedAlternativeIndex));
        var first = decision.OrderedAlternatives[0];
        Assert.True((first.FixedTargetPrice - first.Evidence.VolumeSpread!.AskPrice) /
            (first.Evidence.VolumeSpread.AskPrice - first.InitialStopPrice) < 1.5m);
    }

    // Stable bottom sorting retains the supplied order when two eligible zones share a bottom.
    [Fact]
    public void EqualBottoms_PreservePythonStableSourceOrder()
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var result = Evaluate(Bar(0, 95m, 96m, 94m, 95m), current, Indicator(current),
            Zone(8, IntradayZoneRole.Support, 99m, 100m), Zone(2, IntradayZoneRole.Support, 99m, 100.5m),
            Zone(9, IntradayZoneRole.Resistance, 104m, 105m));
        Assert.Equal(new long[] { 8, 2 }, result.OrderedAlternatives!.Select(signal => signal.Evidence.Zone.ZoneId));
    }

    // Long targets must clear estimated ask rather than midpoint; a merely higher close target cannot qualify.
    [Fact]
    public void Long_TargetFiltersByEstimatedAsk()
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var result = Evaluate(Bar(0, 99m, 100m, 98m, 99m), current, Indicator(current),
            Zone(1, IntradayZoneRole.Support, 99m, 100m), Zone(2, IntradayZoneRole.Resistance, 101.03m, 101.04m),
            Zone(3, IntradayZoneRole.Resistance, 104m, 105m));
        Assert.Equal(3, result.Signal!.Evidence.TargetZone!.ZoneId);
    }

    // A support-role breakdown is allowed, uses bid, and selects the last eligible prefix in bottom order.
    [Fact]
    public void Short_TargetFiltersByEstimatedBidAndKeepsPrefixOrder()
    {
        var current = Bar(5, 100m, 100m, 97m, 98m);
        var result = Evaluate(Bar(0, 101m, 102m, 100m, 101m), current, Indicator(current),
            Zone(1, IntradayZoneRole.Resistance, 90m, 97m), Zone(2, IntradayZoneRole.Resistance, 94m, 95m),
            Zone(3, IntradayZoneRole.Resistance, 97.96m, 97.97m), Zone(4, IntradayZoneRole.Support, 100m, 101m));
        Assert.Equal("short", result.Signal!.Direction);
        Assert.Equal(2, result.Signal.Evidence.TargetZone!.ZoneId);
        Assert.Equal(97.951m, result.Signal.Evidence.VolumeSpread!.BidPrice);
        Assert.Equal(101.3m, result.Signal.InitialStopPrice);
    }

    // The modeled spread threshold is inclusive, with explicit rejection above it.
    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void SyntheticSpread_UsesInclusiveMaximum(int spread, bool expected)
    {
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var rules = Rules with { PythonVolumeSpreadComparison = Rules.PythonVolumeSpreadComparison! with { SyntheticSpreadBps = spread } };
        var result = EvaluateWith(rules, Bar(0, 99m, 100m, 98m, 99m), current, Indicator(current), Session(), DefaultZones());
        Assert.Equal(expected, result.Signal is not null);
        if (!expected) Assert.Equal("python_zone_estimated_spread_exceeds_limit", result.RejectionReason);
    }

    // The kernel verifier must reproduce the complete alternatives, their order, quotes and all causal indicator evidence.
    [Fact]
    public void Guard_RejectsDroppedReorderedOrForgedAlternatives()
    {
        var previous = Bar(0, 90m, 91m, 89m, 90m);
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var indicator = Indicator(current) with { Vwap = null };
        var result = Evaluate(previous, current, indicator, Zone(1, IntradayZoneRole.Support, 90m, 91m),
            Zone(2, IntradayZoneRole.Support, 99m, 100m), Zone(3, IntradayZoneRole.Resistance, 104m, 105m));
        Assert.True(Matches(result, previous, current, indicator));
        Assert.False(Matches(result with { OrderedAlternatives = result.OrderedAlternatives!.Take(1).ToArray() }, previous, current, indicator));
        Assert.False(Matches(result with { OrderedAlternatives = result.OrderedAlternatives!.Reverse().ToArray() }, previous, current, indicator));
        var forged = result.Signal! with { Evidence = result.Signal!.Evidence with {
            VolumeSpread = result.Signal.Evidence.VolumeSpread! with { AskPrice = 101m } } };
        Assert.False(Matches(result with { Signal = forged }, previous, current, indicator));
        Assert.False(Matches(result, previous, current, indicator with { Atr = 3m }));
    }

    // Early closes keep the inclusive close-minus-ten entry window and close-minus-five liquidation.
    [Fact]
    public void ActualSessionClose_DrivesBothCutoffs()
    {
        var evaluator = new PythonZoneComparisonEvaluator(Rules);
        var session = Session() with { CloseUtc = Open.AddMinutes(25) };
        IntradayZoneDecisionContext? last = null;
        for (var minute = 0; minute < 20; minute += 5)
        {
            var bar = Bar(minute, 100m, 102m, 99m, 101m);
            last = evaluator.EvaluateCompleted5m(Snapshot(DefaultZones()), bar, Indicator(bar), session, bar.Timestamp.AddMinutes(5), .01m);
            if (minute == 10)
            {
                Assert.Equal(Open.AddMinutes(15), last.Signal!.EntryCutoffUtc);
                Assert.Equal(Open.AddMinutes(20), last.Signal.SessionDeadlineUtc);
            }
        }
        Assert.Null(last!.Signal);
        Assert.Equal("outside_python_zone_entry_window", last.RejectionReason);
    }

    // No future target, invalid stop or unavailable ATR can enter the frozen geometry group.
    [Theory]
    [InlineData("future_target")]
    [InlineData("invalid_stop")]
    [InlineData("missing_atr")]
    public void InvalidEvidence_CannotFreezeSignal(string failure)
    {
        var zones = DefaultZones();
        var current = Bar(5, 100m, 102m, 100m, 101m);
        var indicator = Indicator(current);
        if (failure == "future_target") zones[1] = zones[1] with { KnownAtUtc = current.Timestamp.AddMinutes(5) };
        if (failure == "invalid_stop") zones[0] = zones[0] with { Bottom = .1m };
        if (failure == "missing_atr") indicator = indicator with { Atr = null };
        Assert.Null(Evaluate(Bar(0, 99m, 100m, 98m, 99m), current, indicator, zones).Signal);
    }

    // Freeze a normal immutable recipe and preserve its complete values through the ordinary generated snapshot writer.
    [Theory]
    [InlineData("python-zone-volume-spread-comparison.bt-v1.yaml", "technical.zone-volume-spread-comparison", 1.5)]
    [InlineData("python-zone-volume-spread-no-rr.bt-v1.yaml", "technical.zone-volume-spread-no-rr", 0)]
    public void Recipe_RoundtripsAndDoesNotChangeOriginalPolicySerialization(string filename, string strategyId, double minimumRewardRisk)
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies", filename));
        Assert.Equal(strategyId, strategy.StrategyId);
        Assert.Equal(Rules with { PythonVolumeSpreadComparison = Rules.PythonVolumeSpreadComparison! with { MinimumRewardRisk = (decimal)minimumRewardRisk } }, strategy.EntryRules.IntradayZone);
        var originalVolumeSpread = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies/python-zone-volume-spread-comparison.bt-v1.yaml"));
        Assert.Equivalent(originalVolumeSpread, strategy with { StrategyId = originalVolumeSpread.StrategyId,
            StrategyName = originalVolumeSpread.StrategyName, Source = originalVolumeSpread.Source,
            EntryRules = strategy.EntryRules with { IntradayZone = strategy.EntryRules.IntradayZone! with {
                PythonVolumeSpreadComparison = strategy.EntryRules.IntradayZone.PythonVolumeSpreadComparison! with { MinimumRewardRisk = 1.5m } } } }, strict: true);
        var path = Path.Combine(Path.GetTempPath(), "tf-zone-volume-spread-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equivalent(strategy, reader.ReadStrategy(path), strict: true);
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy).Replace(
                "      minimum_reward_risk: " + minimumRewardRisk.ToString(System.Globalization.CultureInfo.InvariantCulture), "      minimum_reward_risk: -0.1"));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(path));
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy).Replace("      minimum_relative_volume: 1.2" + Environment.NewLine, ""));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(path));
        }
        finally { File.Delete(path); }
        var original = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies/python-zone-comparison.bt-v1.yaml"));
        Assert.True(original.EntryRules.IntradayZone!.IsPythonComparison);
        Assert.False(original.EntryRules.IntradayZone.IsPythonVolumeSpreadComparison);
        Assert.DoesNotContain("PythonVolumeSpreadComparison", JsonSerializer.Serialize(original));
        Assert.DoesNotContain("python_volume_spread_comparison", RunConfigWriter.SerializeStrategyYaml(original));
        Assert.Throws<InvalidOperationException>(() => (Rules with { PythonVolumeSpreadComparison = null }).ValidateParameters());
        Assert.Throws<InvalidOperationException>(() => (Rules with { PythonComparison = new(2, 2, .15m, .5m, 2m, 1.2m, .15m, 30) }).ValidateParameters());
    }

    // Invoke the independent pure verifier with the exact separate previous candle evidence.
    private static bool Matches(IntradayZoneDecisionContext result, OhlcvBar previous, OhlcvBar current, IndicatorSnapshot indicator) =>
        PythonZoneComparisonEvaluator.MatchesDecision(result, current, current.Timestamp.AddMinutes(5), indicator, previous, Rules);

    // Establish the source prefix before producing a completed confirmation decision.
    private static IntradayZoneDecisionContext Evaluate(OhlcvBar previous, OhlcvBar current, IndicatorSnapshot indicator,
        params IntradayZoneEvidence[] zones) => EvaluateWith(Rules, previous, current, indicator, Session(), zones.Length == 0 ? DefaultZones() : zones);

    // Reuse the normal evaluator chronology for variant parameters and actual exchange-session close tests.
    private static IntradayZoneDecisionContext EvaluateWith(IntradayZoneRules rules, OhlcvBar previous, OhlcvBar current,
        IndicatorSnapshot indicator, IntradayZoneSessionContext session, params IntradayZoneEvidence[] zones)
    {
        var evaluator = new PythonZoneComparisonEvaluator(rules);
        var snapshot = Snapshot(zones);
        evaluator.EvaluateCompleted5m(snapshot, previous, Indicator(previous), session, previous.Timestamp.AddMinutes(5), .01m);
        return evaluator.EvaluateCompleted5m(snapshot, current, indicator, session, current.Timestamp.AddMinutes(5), .01m);
    }

    // Deterministic independent source and next resistance provide a valid long geometry.
    private static IntradayZoneEvidence[] DefaultZones() => [Zone(1, IntradayZoneRole.Support, 99m, 100m), Zone(2, IntradayZoneRole.Resistance, 104m, 105m)];
    // Snapshot availability precedes the completed confirmation candle's opening.
    private static IntradayZoneSnapshot Snapshot(params IntradayZoneEvidence[] zones) => new("CRDO", Open, new(2026, 10, 27), zones, []);
    // Confirmed pivots are fixture evidence, never generated from future entry candles.
    private static IntradayZoneEvidence Zone(long id, IntradayZoneRole role, decimal bottom, decimal top) => new(id, 1, role, bottom, top,
        2, Open.AddMinutes(-15), Open, Open.AddMinutes(-75), new(2026, 10, 27),
        new(IntradayZonePivotKind.Valley, Open.AddMinutes(-75), Open.AddMinutes(-60), Open.AddMinutes(-15), new(2026, 10, 27), bottom, top));
    // Use the declared New York session calendar with five prior sessions for normal evidence guards.
    private static IntradayZoneSessionContext Session() => new(new(2026, 10, 27), Open, Open.AddHours(6.5),
        [new(2026, 10, 26), new(2026, 10, 23), new(2026, 10, 22), new(2026, 10, 21), new(2026, 10, 20)]);
    // Every synthetic fixture candle explicitly declares raw SIP data.
    private static OhlcvBar Bar(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new("CRDO", Open.AddMinutes(minute), "5m", open, high, low, close, 120m, "sip", "raw");
    // Verified twenty-session same-slot volume is exactly at the recipe's inclusive participation threshold.
    private static IndicatorSnapshot Indicator(OhlcvBar bar) => new(bar.Ticker, bar.Timestamp, bar.Timeframe,
        bar.Close, bar.Volume, null, null, 2m, null, null, null, null, null, null, null, null, null, null,
        SlotRelativeVolume: 1.2m, SlotMedianVolume: 100m, SlotRelativeVolumeSampleCount: 20,
        MarketEvidenceProfileVersion: MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort: "regular",
        RelativeVolumeMinimumSamples: 20, DataFeed: bar.DataFeed, AdjustmentPolicy: bar.AdjustmentPolicy, MarketEvidenceReliability: "verified_same_feed");
}
