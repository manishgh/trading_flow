using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed partial class IntradayPortfolioExecutionTests
{
    // A too-close known box fails at one risk-sized share, while the next source's same box covers costs at its unchanged larger quantity.
    [Fact]
    public void FullCostTarget_ContinuesSourcesWithoutSkippingKnownBox()
    {
        var bars = VolumeBars();
        var first = TargetTreatmentSignal(VolumeSignal(bars, bottom: 50m, top: 99m, target: 100.5m, minimumRewardRisk: 0m));
        var second = TargetTreatmentSignal(VolumeSignal(bars, index: 1, target: 100.5m, minimumRewardRisk: 0m));
        var result = RunTargetTreatment(bars, [VolumeCandidate([first, second])]);
        Assert.Equal("target_does_not_cover_costs", result.AlternativeDecisions[0].Reason);
        Assert.Equal(1, result.AlternativeDecisions[0].PlannedQuantity);
        Assert.Equal("known_zone", result.AlternativeDecisions[0].TargetPlan!.TargetOrigin);
        Assert.Equal(100.5m, result.AlternativeDecisions[0].TargetPlan!.ResolvedTargetPrice);
        Assert.Equal("known_zone_target_covers_costs", result.AlternativeDecisions[1].Reason);
        Assert.True(result.AlternativeDecisions[1].RewardRisk is > 0m and < 1.5m);
        var selected = Assert.Single(result.SelectedCandidates);
        Assert.Equal(1, selected.ZoneExecution!.Evidence.VolumeSpread!.OrderedAlternativeIndex);
        Assert.Equal(100.5m, selected.TakeProfitPrice);
        Assert.Single(result.PortfolioState.AdmittedOrders);
        Assert.Empty(result.Failures);
    }

    // A known cost-failing target is terminal for that source even when risk fallback is enabled; no farther target is invented.
    [Fact]
    public void FullCostTarget_KnownUnprofitableBoxCannotFallBack()
    {
        var bars = VolumeBars();
        var signal = TargetTreatmentSignal(VolumeSignal(bars, target: 100.06m, minimumRewardRisk: 0m));
        var result = RunTargetTreatment(bars, [VolumeCandidate([signal])]);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Equal("target_does_not_cover_costs", Assert.Single(result.AlternativeDecisions).Reason);
        Assert.Equal(100.06m, result.AlternativeDecisions[0].TargetPlan!.ResolvedTargetPrice);
        Assert.Equal("no_ordered_alternative_passed_sizing_and_target_costs", Assert.Single(result.Dispositions).Reason);
    }

    // Missing-box targets resolve once before execution and stay fixed through actual opening gaps and partial entry fills.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void FullCostTarget_FreezesRiskTargetAcrossFutureGapsAndPartials(string direction)
    {
        var bars = VolumeBars(direction);
        bars[1] = bars[1] with { Volume = 50m };
        bars[2] = bars[2] with { Volume = 50m };
        var signal = TargetTreatmentSignal(VolumeSignal(bars, direction, minimumRewardRisk: 0m), absentTarget: true);
        var candidate = VolumeCandidate([signal]);
        var normal = RunTargetTreatment(bars, [candidate], VolumePortfolio(10));
        var planned = Assert.Single(normal.SelectedCandidates).ZoneExecution!;
        Assert.Equal("risk_derived", planned.Evidence.TargetPlan!.TargetOrigin);
        Assert.Null(planned.Evidence.TargetZone);
        Assert.Null(planned.Evidence.RawTargetPrice);
        Assert.True(planned.Evidence.TargetPlan.EstimatedNetRewardRisk >= 2m);
        Assert.Equal(planned.FixedTargetPrice, Assert.Single(normal.Trades).TakeProfitPrice);
        Assert.True(normal.PortfolioState.Fills.Count(fill => fill.IsEntry) > 1);
        Assert.All(normal.Events.Where(e => e.State == "protection_initialized"), e => Assert.Equal(planned.FixedTargetPrice, e.TargetPrice));
        var gap = direction == "long" ? 98m : 102m;
        bars[2] = Bar(10, gap, gap, gap, gap, 50m);
        var altered = RunTargetTreatment(bars, [candidate], VolumePortfolio(10));
        Assert.Equal(planned.Evidence.TargetPlan, Assert.Single(altered.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan);
        Assert.Equal(planned.FixedTargetPrice, Assert.Single(altered.Trades).TakeProfitPrice);
        Assert.Equal("gap_fill_through_structural_stop", altered.Trades[0].ExitReason);
        Assert.Empty(normal.Failures);
        Assert.Empty(altered.Failures);
    }

    // Full-cost risk is an estimate at the unchanged planner quantity and may exceed the planner's budget when configured impact is added.
    [Fact]
    public void FullCostTarget_DoesNotResizeQuantityForFullCostLoss()
    {
        var bars = VolumeBars();
        var portfolio = VolumePortfolio() with { ImpactBpsAtMaxParticipation = 250m };
        var control = RunVolume(bars, [VolumeCandidate([VolumeSignal(bars, minimumRewardRisk: 0m)])], portfolio, noRewardRiskFilter: true);
        var signal = TargetTreatmentSignal(VolumeSignal(bars, minimumRewardRisk: 0m), absentTarget: true);
        var treatment = RunTargetTreatment(bars, [VolumeCandidate([signal])], portfolio);
        var planned = Assert.Single(treatment.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan!;
        Assert.Equal(Assert.Single(control.PortfolioState.AdmittedOrders).RequestedQuantity, planned.PlannedQuantity);
        Assert.Equal(24, planned.PlannedQuantity);
        Assert.True(planned.PlannerLossDollars <= planned.PlannerRiskBudgetDollars);
        Assert.True(planned.EstimatedNetStopRisk > planned.PlannerRiskBudgetDollars);
        Assert.Equal(265m, planned.AdverseCostBps);
        Assert.Empty(treatment.Failures);
    }

    // The normal evaluator and shared decision kernel preserve a genuinely missing target until chronological portfolio admission resolves it.
    [Fact]
    public void FullCostTarget_SharedKernelToPortfolioResolvesPendingTarget()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var runtime = new StrategyRunArtifactValidator(reader, new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader))
            .ReadAndValidateRuntimeArtifact(Path.Combine(root, "configs", "backtest", "strategies", "zone-risk-target.bt-v1.yaml"), StrategySelectionMode.Backtest);
        var bars = VolumeBars(minute: 40).Concat(Enumerable.Range(9, 10).Select(i => Bar(i * 5, 100m, 100.5m, 99.5m, 100m)))
            .Select(b => b with { DataFeed = "sip", AdjustmentPolicy = "raw" }).ToArray();
        var source = TargetTreatmentSignal(VolumeSignal(bars, minute: 40, minimumRewardRisk: 0m), absentTarget: true);
        var other = source.Evidence.Zone with { ZoneId = 99, Bottom = 90m, Top = 91m };
        var snapshot = new IntradayZoneSnapshot("X", Start, Day, [other, source.Evidence.Zone], []);
        var session = new IntradayZoneSessionContext(Day, Start, Start.AddMinutes(95),
            [Day.AddDays(-1), Day.AddDays(-2), Day.AddDays(-3), Day.AddDays(-4), Day.AddDays(-5)]);
        var evaluator = new PythonZoneComparisonEvaluator(runtime.Definition.EntryRules.IntradayZone!);
        IntradayZoneDecisionContext? decision = null;
        var prefix = bars.Where(b => b.Timestamp < Start.AddMinutes(40)).ToArray();
        foreach (var bar in prefix)
            decision = evaluator.EvaluateCompleted5m(snapshot, bar, TargetIndicator(bar), session, bar.Timestamp.AddMinutes(5), .01m);
        Assert.Null(decision!.Signal!.FixedTargetPrice);
        var signal = decision.Signal;
        var fifteen = new[] { bars[0] with { Timeframe = "15m", KnownAtUtc = Start.AddMinutes(15) },
            bars[3] with { Timeframe = "15m", KnownAtUtc = Start.AddMinutes(30) } };
        var asOf = signal.ActiveAtUtc;
        var key = StrategyDecisionRequestAssembler.CreateSetupKey(runtime.Definition.StrategyId, "5m", signal.ConfirmationBar.Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery("X", key, asOf, signal.SessionDeadlineUtc) with { IsPersisted = true };
        var eligibility = new StrategyEligibilityEvidence(true, "synthetic_fixture", "fixture", asOf, "{}");
        var request = StrategyDecisionRequestAssembler.Create(runtime, "X",
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = prefix, ["15m"] = fifteen },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = prefix.Select(TargetIndicator).ToArray(), ["15m"] = fifteen.Select(TargetIndicator).ToArray() },
            Array.Empty<CatalystEvent>(), asOf, discovery, eligibility, eligibility, StrategyCandidateState.DataWarming, 0,
            key, asOf, signal.SessionDeadlineUtc, 2, Guid.NewGuid(), zoneContext: decision);
        var committed = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(committed.IsTriggered);
        Assert.Null(committed.OrderPlan!.ZoneEntry!.FixedTargetPrice);
        var candidate = VolumeCandidate(committed.OrderPlan.ZoneAlternatives!);
        var outcome = RunTargetTreatment(bars, [candidate], longSession: true);
        var selected = Assert.Single(outcome.SelectedCandidates);
        Assert.Equal("risk_derived", selected.ZoneExecution!.Evidence.TargetPlan!.TargetOrigin);
        Assert.True(selected.TakeProfitPrice > 100m);
        Assert.Equal(selected.TakeProfitPrice, Assert.Single(outcome.Trades).TakeProfitPrice);
        Assert.Empty(outcome.Failures);
    }

    // Mark only the approved target package; an absent target remains null in source evidence until the planner publishes its legal price.
    private static IntradayZoneSignal TargetTreatmentSignal(IntradayZoneSignal signal, bool absentTarget = false) => signal with {
        FixedTargetPrice = absentTarget ? null : signal.FixedTargetPrice,
        Evidence = signal.Evidence with { TargetZone = absentTarget ? null : signal.Evidence.TargetZone,
            RawTargetPrice = absentTarget ? null : signal.Evidence.RawTargetPrice,
            TargetPlan = new(absentTarget ? "risk_derived" : "known_zone", 2m) } };

    // Load the ordinary immutable recipe and replay the same shared portfolio/candle path used by saved plans.
    private static IntradayPortfolioReplayOutcome RunTargetTreatment(OhlcvBar[] bars, BacktestCandidateTrade[] candidates,
        PortfolioConfig? portfolio = null, bool longSession = false)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "zone-risk-target.bt-v1.yaml"));
        var sessions = longSession ? new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = new(Day, true, new(9, 30), new(11, 5)) } : Sessions();
        var end = Start.AddMinutes(longSession ? 95 : 45);
        var selected = candidates.Select(c => c with { StrategyId = strategy.StrategyId }).ToArray();
        var result = IntradayPortfolioExecution.Replay(new(portfolio ?? VolumePortfolio(), [strategy], selected, bars, sessions, Start, end));
        NativeDailySessionLedger.VerifyRecordedState((portfolio ?? VolumePortfolio()).StartingCapital, result.PortfolioState, new(Start, end, sessions, true));
        return result;
    }

    // Supply exact completed source/feed evidence for kernel verification, with ATR and the existing verified same-time volume baseline.
    private static IndicatorSnapshot TargetIndicator(OhlcvBar bar) => new(bar.Ticker, bar.Timestamp, bar.Timeframe,
        bar.Close, bar.Volume, null, null, 1m, null, null, null, null, null, null, null, null, null, null,
        SlotRelativeVolume: 2m, SlotMedianVolume: bar.Volume / 2m, SlotRelativeVolumeSampleCount: 20,
        MarketEvidenceProfileVersion: MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort: "regular", RelativeVolumeMinimumSamples: 20,
        DataFeed: bar.DataFeed, AdjustmentPolicy: bar.AdjustmentPolicy, MarketEvidenceReliability: "verified_same_feed");
}
