using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed partial class IntradayPortfolioExecutionTests
{
    // Changing the stop reruns normal quantity/cost planning; only the fallback target moves while a known target stays fixed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCandleStop_RecomputesQuantityCostsAndFallbackTarget(bool absentTarget)
    {
        var bars = VolumeBars();
        var seed = VolumeSignal(bars, bottom: 95m, minimumRewardRisk: 0m);
        var controlSignal = StopBasisSignal(seed, twoCandle: false, absentTarget);
        var treatmentSignal = StopBasisSignal(seed, twoCandle: true, absentTarget);
        var control = RunStopBasisFixture(bars, controlSignal, twoCandle: false);
        var treatment = RunStopBasisFixture(bars, treatmentSignal, twoCandle: true);
        var oldSelected = Assert.Single(control.SelectedCandidates);
        var selected = Assert.Single(treatment.SelectedCandidates);
        var oldPlan = oldSelected.ZoneExecution!.Evidence.TargetPlan!;
        var plan = selected.ZoneExecution!.Evidence.TargetPlan!;
        Assert.Equal(94.85m, oldSelected.StopLossPrice);
        Assert.Equal(98.35m, selected.StopLossPrice);
        Assert.True(plan.PlannedQuantity > oldPlan.PlannedQuantity);
        Assert.Equal(24, plan.PlannedQuantity);
        Assert.NotEqual(oldPlan.EstimatedNetStopRisk, plan.EstimatedNetStopRisk);
        Assert.NotEqual(oldPlan.EstimatedStopFillPrice, plan.EstimatedStopFillPrice);
        Assert.True(plan.PlannerLossDollars <= plan.PlannerRiskBudgetDollars);
        Assert.Equal(1m, plan.EstimatedEntryFees);
        Assert.Equal(1m, plan.EstimatedStopExitFees);
        Assert.Equal(1m, plan.EstimatedTargetFees);
        Assert.Equal(15m, plan.AdverseCostBps);
        if (absentTarget)
        {
            Assert.Equal("risk_derived", plan.TargetOrigin);
            Assert.True(plan.EstimatedNetRewardRisk >= 2m);
            Assert.True(selected.TakeProfitPrice < oldSelected.TakeProfitPrice);
            Assert.Null(selected.ZoneExecution.Evidence.TargetZone);
            Assert.Null(selected.ZoneExecution.Evidence.RawTargetPrice);
        }
        else
        {
            Assert.Equal("known_zone", plan.TargetOrigin);
            Assert.Equal(oldSelected.TakeProfitPrice, selected.TakeProfitPrice);
            Assert.Equal(oldSelected.ZoneExecution.Evidence.TargetZone, selected.ZoneExecution.Evidence.TargetZone);
        }
        Assert.Equal(selected.StopLossPrice, Assert.Single(treatment.Trades).StopLossPrice);
        Assert.Equal(selected.TakeProfitPrice, treatment.Trades[0].TakeProfitPrice);
        Assert.Empty(control.Failures);
        Assert.Empty(treatment.Failures);
    }

    // Future gaps cannot change the new stop's pre-entry sizing/target, and a shared-bar stop wins when both exits are touched.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCandleStop_PreservesPreEntryPlanAcrossGapsAndAdverseOrdering(bool gap)
    {
        var bars = VolumeBars();
        var signal = StopBasisSignal(VolumeSignal(bars, minimumRewardRisk: 0m), twoCandle: true, absentTarget: true);
        var normal = RunStopBasisFixture(bars, signal, twoCandle: true);
        bars[2] = gap ? Bar(10, 97m, 97m, 97m, 97m) : Bar(10, 100m, 110m, 97m, 100m);
        var altered = RunStopBasisFixture(bars, signal, twoCandle: true);
        Assert.Equal(normal.AlternativeDecisions, altered.AlternativeDecisions);
        Assert.Equal(Assert.Single(normal.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan,
            Assert.Single(altered.SelectedCandidates).ZoneExecution!.Evidence.TargetPlan);
        var trade = Assert.Single(altered.Trades);
        Assert.Equal(gap ? "gap_fill_through_structural_stop" : "stop_loss", trade.ExitReason);
        Assert.Equal(gap ? 97m * 1.0015m : 100m * 1.0015m, trade.EntryPrice);
        Assert.Equal(gap ? 97m * .9985m : 98.35m * .9985m, trade.ExitPrice);
        Assert.Equal(98.35m, trade.StopLossPrice);
        Assert.Equal(2m, trade.Fees);
        Assert.Empty(normal.Failures);
        Assert.Empty(altered.Failures);
    }

    // Executors reject a substituted zone stop or source/basis evidence instead of silently restoring the older rule.
    [Theory]
    [InlineData("old_stop")]
    [InlineData("basis")]
    [InlineData("missing_basis")]
    [InlineData("feed")]
    [InlineData("adjustment")]
    [InlineData("future_previous")]
    public void TwoCandleStop_ExecutorRejectsSubstitutedEvidence(string mutation)
    {
        var bars = VolumeBars();
        var signal = StopBasisSignal(VolumeSignal(bars, minimumRewardRisk: 0m), twoCandle: true, absentTarget: true);
        if (mutation == "old_stop") signal = signal with { InitialStopPrice = 98.85m,
            Evidence = signal.Evidence with { RawStopPrice = 98.85m } };
        if (mutation == "basis") signal = signal with { Evidence = signal.Evidence with { StopBasis = IntradayZoneStopGeometry.SourceZone } };
        if (mutation == "missing_basis") signal = signal with { Evidence = signal.Evidence with { StopBasis = null } };
        if (mutation is "feed" or "adjustment" or "future_previous")
        {
            bars[0] = bars[0] with {
                DataFeed = mutation == "feed" ? "different_feed" : bars[0].DataFeed,
                AdjustmentPolicy = mutation == "adjustment" ? "different_adjustment" : bars[0].AdjustmentPolicy,
                KnownAtUtc = mutation == "future_previous" ? Start.AddMinutes(6) : bars[0].KnownAtUtc };
            signal = signal with { Evidence = signal.Evidence with { PreviousBar = bars[0] } };
        }
        var result = RunStopBasisFixture(bars, signal, twoCandle: true);
        Assert.Empty(result.PortfolioState.Fills);
        Assert.Empty(result.SelectedCandidates);
        Assert.False(Assert.Single(result.Dispositions).Accepted);
    }

    // The long-only selection leaves short fixed levels, quantity, full costs and solved fallback target exactly unchanged.
    [Fact]
    public void TwoCandleStop_ExecutorKeepsShortPlanningUnchanged()
    {
        var bars = VolumeBars("short");
        var seed = VolumeSignal(bars, "short", minimumRewardRisk: 0m);
        var control = RunStopBasisFixture(bars, StopBasisSignal(seed, twoCandle: false, absentTarget: true), twoCandle: false);
        var treatment = RunStopBasisFixture(bars, StopBasisSignal(seed, twoCandle: true, absentTarget: true), twoCandle: true);
        var oldSelected = Assert.Single(control.SelectedCandidates);
        var selected = Assert.Single(treatment.SelectedCandidates);
        Assert.Equal(101.15m, selected.StopLossPrice);
        Assert.Equal(oldSelected.StopLossPrice, selected.StopLossPrice);
        Assert.Equal(oldSelected.TakeProfitPrice, selected.TakeProfitPrice);
        Assert.Equal(oldSelected.ZoneExecution!.Evidence.TargetPlan, selected.ZoneExecution!.Evidence.TargetPlan);
        Assert.Equal(IntradayZoneStopGeometry.SourceZone, selected.ZoneExecution.Evidence.StopBasis);
        Assert.Equal(control.Trades, treatment.Trades);
        Assert.Empty(control.Failures);
        Assert.Empty(treatment.Failures);
    }

    // Freeze the same diagnostic-volume/full-cost-target contract as D and select only the approved stop-basis delta.
    private static IntradayZoneSignal StopBasisSignal(IntradayZoneSignal seed, bool twoCandle, bool absentTarget)
    {
        var signal = TargetTreatmentSignal(seed, absentTarget);
        var basis = twoCandle ? PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop : null;
        var (raw, stop) = IntradayZoneStopGeometry.Calculate(signal.Direction == "long", signal.Evidence.Zone,
            signal.Evidence.PreviousBar!, signal.ConfirmationBar, signal.Evidence.Atr!.Value, signal.TickSize, .15m, basis);
        return signal with { InitialStopPrice = stop, Evidence = signal.Evidence with {
            RawStopPrice = raw, StopBasis = IntradayZoneStopGeometry.Basis(signal.Direction == "long", basis),
            LongBounceVolumeMode = PythonZoneVolumeSpreadComparisonRules.DiagnosticLongVolume } };
    }

    // Replay isolated synthetic bars through the ordinary executor and reconcile its recorded account state.
    private static IntradayPortfolioReplayOutcome RunStopBasisFixture(OhlcvBar[] bars, IntradayZoneSignal signal, bool twoCandle)
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies",
            "zone-bounce-volume-risk-target.bt-v1.yaml"));
        strategy = strategy with { EntryRules = strategy.EntryRules with { IntradayZone = strategy.EntryRules.IntradayZone! with {
            PythonVolumeSpreadComparison = strategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison! with {
                LongStopBasis = twoCandle ? PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop : null } } } };
        var candidate = VolumeCandidate([signal]) with { StrategyId = strategy.StrategyId };
        var portfolio = VolumePortfolio();
        var result = IntradayPortfolioExecution.Replay(new(portfolio, [strategy], [candidate], bars, Sessions(), Start, Start.AddMinutes(45)));
        NativeDailySessionLedger.VerifyRecordedState(portfolio.StartingCapital, result.PortfolioState, new(Start, Start.AddMinutes(45), Sessions(), true));
        return result;
    }
}
