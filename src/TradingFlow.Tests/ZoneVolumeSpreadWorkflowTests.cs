using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ZoneVolumeSpreadWorkflowTests
{
    // Preserve the numeric audit and explicit disabled selection reason through the API and shared Web/Android presentation.
    [Fact]
    public void DisabledRewardRiskFilter_IsExplicitThroughApiAndSharedPresentation()
    {
        var at = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var candidate = Candidate(at, true);
        candidate = candidate with { StrategyId = "technical.zone-volume-spread-no-rr", ZoneExecution = candidate.ZoneExecution! with {
            Evidence = candidate.ZoneExecution.Evidence with { VolumeSpread = candidate.ZoneExecution.Evidence.VolumeSpread! with {
                MinimumRewardRisk = 0m, EstimatedNetRewardDollars = -1m, SelectedRewardRisk = -1m / 48m } } } };
        var state = new BacktestPortfolioState("synthetic", at.AddHours(4), 1, 1, 0, [], [], [], [], []);
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state)
        {
            SelectedCandidates = [candidate], AlternativeDecisions = [new("candidate", 1, "short", 2, 23, 48m, -1m, -1m / 48m,
                true, "selected_with_minimum_reward_risk_filter_disabled")]
        };
        var outcome = new PortfolioBuildOutcome([], new PortfolioAdmissionAudit(), []) { State = state };
        var detail = Assert.Single(BacktestRunner.ProjectZoneDecisions([candidate], outcome, replay));
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var roundTrip = JsonSerializer.Deserialize<RunZoneDecisionDetail>(JsonSerializer.Serialize(wire))!;
        Assert.Equal(0m, roundTrip.VolumeSpread!.MinimumRewardRisk);
        Assert.True(roundTrip.VolumeSpread.SelectedRewardRisk < 0m);
        Assert.Equal("selected_with_minimum_reward_risk_filter_disabled", Assert.Single(roundTrip.AlternativeDecisions!).Reason);
        var lines = IntradayZoneDecisionPresentation.Lines(roundTrip);
        Assert.Contains(lines, line => line.Contains("minimum reward/risk filter disabled"));
        Assert.DoesNotContain(lines, line => line.Contains("minimum 0") || line.Contains("minimum 1.5"));
    }

    // Normal catalog/profile selection freezes the separate candle policy, raw source contract and existing portfolio settings.
    [Theory]
    [InlineData("technical.zone-volume-spread-comparison")]
    [InlineData("technical.zone-volume-spread-no-rr")]
    [InlineData("technical.zone-bounce-volume")]
    [InlineData("technical.zone-risk-target")]
    [InlineData("technical.zone-bounce-volume-risk-target")]
    [InlineData("technical.zone-two-candle-stop")]
    [InlineData("technical.zone-breakout-retest")]
    public void SavedProfile_ResolvesNewRecipeWithoutHistoricalQuoteManifest(string strategyId)
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifact = Assert.Single(new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader)
            .GetSelectable(StrategySelectionMode.Backtest), item => item.Identity.StrategyId == strategyId);
        var profile = reader.ReadBacktestRun(Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));
        var run = BacktestExecutionProfileResolver.Resolve(profile, [artifact.ResolvedStrategy]);
        Assert.Equal("1.0.0", artifact.Identity.SemanticVersion);
        Assert.Equal(ExecutionReplayPolicy.PythonZoneVolumeSpreadV1, run.Engine.ExecutionReplayPolicyId);
        Assert.Equal(profile.Portfolio, run.Portfolio);
        Assert.Equal(new[] { "5m" }, run.Intervals);
        Assert.False(ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId));
        Assert.Null(run.Engine.ExecutionSourceManifestPath);
        Assert.Equal("raw", MarketEvidenceOperationalRules.ForExecutionTimingPolicy(artifact.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
        var formatter = typeof(WorkflowRunConfiguration).GetMethod("ExecutionAssumptionNote",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var note = Assert.IsType<string>(formatter.Invoke(null, [run, artifact.ResolvedStrategy]));
        var rules = artifact.ResolvedStrategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!;
        if (rules.UsesFullCostTargets)
        {
            Assert.Contains("positive estimated net reward after full modeled costs", note);
            Assert.Contains("Only an absent next zone permits a planned net-2R target", note);
            Assert.Contains("unchanged shared-sized quantity, not a new sizing budget", note);
            Assert.Contains("does not guarantee realized reward/risk", note);
            Assert.DoesNotContain("estimate omits exit spread", note);
        }
        else if (rules.MinimumRewardRisk == 0m)
        {
            Assert.Contains("first successfully sized alternative; minimum reward/risk filter disabled", note);
            Assert.DoesNotContain("reward/risk >= 0", note);
            Assert.DoesNotContain("reward/risk >=0", note);
        }
        else Assert.Contains("reward/risk >= 1.5", note);
        Assert.Contains(rules.UsesBreakoutRetestEntry ? "Long-retest volume is diagnostic only" :
            rules.LongVolumeIsDiagnostic ? "Long-bounce volume is diagnostic only" : "Both directions require same-clock RVOL", note);
        if (rules.UsesBreakoutRetestEntry)
        {
            Assert.Contains("later five-minute actual overlap and close above the frozen box", note);
            Assert.Contains("Short breakdown rule unchanged", note);
            Assert.DoesNotContain("Long-bounce volume", note);
        }
        var settingsMethod = typeof(WorkflowRunConfiguration).GetMethod("ZoneSettings",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var settings = Assert.IsType<BacktestZoneSettingsResponse>(settingsMethod.Invoke(null, [run, artifact.ResolvedStrategy]));
        Assert.Equal(rules.LongBounceVolumeMode ?? "threshold", settings.VolumeMode);
        Assert.Equal(rules.TargetPolicy ?? "next_zone_only", settings.TargetPolicy);
        Assert.Equal(rules.FallbackNetRiskMultiple, settings.FallbackNetRiskMultiple);
        Assert.Equal(rules.SyntheticSpreadBps, settings.SyntheticSpreadBps);
        Assert.Contains(rules.LongStopBasis == PythonZoneVolumeSpreadComparisonRules.TwoCandleLowStop
            ? "lower low of the previous and confirmation candles" : "source-zone", settings.StopBasis);
        Assert.Contains(rules.UsesFullCostTargets ? "Full-cost target estimates include" : "No full-cost target calculation is applied", settings.PlanningCostAssumption);
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile with {
            Engine = profile.Engine with { ExecutionSourceManifestPath = "foreign-quotes.json" } }, [artifact.ResolvedStrategy]));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile,
            [artifact.ResolvedStrategy], ExecutionReplayPolicy.PythonZoneBounceBreakdownV1));
    }

    // A later zone alternative must replace the rejected seed geometry before result projection and shared-client presentation.
    [Fact]
    public void SelectedAlternative_ReplacesSeedGeometryAndPreservesAdmissionEvidenceThroughApi()
    {
        var at = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var seed = Candidate(at, false);
        var selected = Candidate(at, true);
        var candidates = BacktestRunner.ApplySelectedZoneAlternatives([seed], [selected]);
        Assert.Equal("short", Assert.Single(candidates).Direction);
        Assert.Equal(103, candidates[0].StopLossPrice);
        Assert.Equal(95, candidates[0].TakeProfitPrice);
        var state = new BacktestPortfolioState("synthetic", at.AddHours(4), 1, 1, 0,
            [new("candidate", selected.StrategyId, "ABC", false, true, 23, 99.8m, 1, at, new(2026, 10, 27))], [], [], [], []);
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state)
        {
            SelectedCandidates = [selected],
            AlternativeDecisions = [new("candidate", 0, "short", 1, 24, 50, 60, 1.2m, false, "fee_adjusted_reward_risk_below_minimum"),
                new("candidate", 1, "short", 2, 23, 48, 96, 2, true, "fee_adjusted_reward_risk_passed")]
        };
        var outcome = new PortfolioBuildOutcome([], new PortfolioAdmissionAudit(), []) { State = state };
        var detail = Assert.Single(BacktestRunner.ProjectZoneDecisions(candidates, outcome, replay));
        Assert.Equal("short", detail.Direction);
        Assert.Equal(2, detail.ZoneId);
        Assert.Equal(103, detail.FrozenStopPrice);
        Assert.Equal(95, detail.FixedTargetPrice);
        Assert.Equal(99.8m, detail.ActualEntryPrice);
        Assert.Equal("open", detail.Status);
        Assert.Null(detail.Vwap);
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var roundTrip = JsonSerializer.Deserialize<RunZoneDecisionDetail>(JsonSerializer.Serialize(wire))!;
        Assert.Equal(wire.VolumeSpread, roundTrip.VolumeSpread);
        Assert.Equal(2, roundTrip.AlternativeDecisions!.Count);
        var lines = IntradayZoneDecisionPresentation.Lines(roundTrip);
        Assert.Contains(lines, line => line.Contains("Synthetic bid 99.95 / ask 100.05"));
        Assert.Contains(lines, line => line.Contains("capped planned quantity 23") && line.Contains("after ticket fees 2"));
        Assert.Contains(lines, line => line.Contains("Alternative index 0") && line.Contains("rejected: fee_adjusted_reward_risk_below_minimum"));
        Assert.Contains(lines, line => line.Contains("Alternative index 1") && line.Contains("selected: fee_adjusted_reward_risk_passed"));
        Assert.Contains(lines, line => line.Contains("both directions minimum 1.2") && line.Contains("no VWAP"));
        Assert.DoesNotContain(lines, line => line.Contains("long entry has no volume filter") || line.Contains("Historical SIP quote/trade execution"));
    }

    // Complete artifact admission joins must retain early candidates even when client detail samples show only the latest hundred.
    [Fact]
    public void ResultProjection_RetainsEveryCandidateAndItsAdmissionFactsBeyondDisplayLimit()
    {
        var at = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var candidates = Enumerable.Range(0, 205)
            .Select(index => Candidate(at, true) with { CandidateId = $"candidate-{index}" }).ToArray();
        var state = new BacktestPortfolioState("synthetic", at.AddHours(4), 1, 1, 0, [], [], [], [], []);
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state)
        {
            SelectedCandidates = candidates,
            AlternativeDecisions = candidates.Select(candidate => new IntradayZoneAlternativeDecision(
                candidate.CandidateId!, 0, "short", 2, 23, 48, 96, 2, true, "known_zone_target_covers_costs")).ToArray()
        };
        var outcome = new PortfolioBuildOutcome([], new PortfolioAdmissionAudit(), []) { State = state };
        var projected = BacktestRunner.ProjectZoneDecisions(candidates, outcome, replay);
        Assert.Equal(205, projected.Count);
        Assert.Equal("candidate-0", projected[0].CandidateId);
        Assert.All(projected, item => Assert.Equal(23, Assert.Single(item.AlternativeDecisions!).PlannedQuantity));
    }

    // Preserve stop inputs through the durable result, wire JSON and both clients' shared formatter.
    [Fact]
    public void TwoCandleStop_EvidenceSurvivesResultApiAndSharedPresentation()
    {
        var at = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var seed = Candidate(at, true);
        var previous = new OhlcvBar("ABC", at.AddMinutes(-10), "5m", 99, 100, 98, 99.5m, 10000, "sip", "raw");
        var signal = seed.ZoneExecution! with
        {
            Direction = "long", InitialStopPrice = 97.7m, FixedTargetPrice = 105m,
            Evidence = seed.ZoneExecution.Evidence with
            {
                StopBasis = "two_candle_low", PreviousBar = previous, RawStopPrice = 97.7m,
                Zone = seed.ZoneExecution.Evidence.Zone with { Role = IntradayZoneRole.Support },
                LongBounceVolumeMode = "diagnostic_only"
            }
        };
        var candidate = seed with { Direction = "long", ZoneExecution = signal, StopLossPrice = 97.7m };
        var state = new BacktestPortfolioState("synthetic", at.AddHours(4), 1, 1, 0, [], [], [], [], []);
        var replay = new IntradayPortfolioReplayOutcome([], [], [], [], state);
        var detail = Assert.Single(BacktestRunner.ProjectZoneDecisions([candidate],
            new PortfolioBuildOutcome([], new PortfolioAdmissionAudit(), []) { State = state }, replay));
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var restored = JsonSerializer.Deserialize<RunZoneDecisionDetail>(JsonSerializer.Serialize(wire))!;
        Assert.Equal("two_candle_low", restored.StopBasis);
        Assert.Equal(98m, restored.PreviousLow);
        Assert.Equal(99m, restored.ConfirmationLow);
        Assert.Equal(97.7m, restored.RawStopPrice);
        Assert.Equal(97.7m, restored.FrozenStopPrice);
        Assert.Equal(.01m, restored.TickSize);
        var lines = IntradayZoneDecisionPresentation.Lines(restored);
        Assert.Contains(lines, line => line.Contains("Lower low of the two signal candles minus ATR buffer"));
        Assert.Contains(lines, line => line.Contains("previous low 98") && line.Contains("confirmation low 99"));
    }

    // Preserve frozen and active source versions through an unfilled result and both clients without recomputing the setup.
    [Fact]
    public void Retest_EvidenceSurvivesResultApiAndSharedPresentation()
    {
        var at = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var seed = Candidate(at, true);
        var resistance = seed.ZoneExecution!.Evidence.Zone;
        var frozen = resistance with { Version = 2, Role = IntradayZoneRole.Support, KnownAtUtc = at.AddMinutes(-30) };
        var active = frozen with { Version = 4, Top = 101.5m, KnownAtUtc = at.AddMinutes(-15) };
        var target = resistance with { ZoneId = 8, Version = 3, Bottom = 105, Top = 106 };
        var retest = new IntradayZoneRetestEvidence(new(resistance, frozen, at.AddMinutes(-30), true, 2), active, at.AddMinutes(-15));
        var signal = seed.ZoneExecution with { Direction = "long", Pattern = "breakout_retest", Evidence = seed.ZoneExecution.Evidence with {
            Zone = frozen, FlippedAtUtc = retest.ArmTransition.FlippedAtUtc, Retest = retest,
            TargetZone = target, LongBounceVolumeMode = "diagnostic_only" } };
        var candidate = seed with { Direction = "long", ZoneExecution = signal };
        var state = new BacktestPortfolioState("synthetic", at.AddHours(4), 1, 1, 0, [], [], [], [], []);
        var detail = Assert.Single(BacktestRunner.ProjectZoneDecisions([candidate],
            new PortfolioBuildOutcome([], new PortfolioAdmissionAudit(), []) { State = state },
            new IntradayPortfolioReplayOutcome([], [], [], [], state)));
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var restored = JsonSerializer.Deserialize<RunZoneDecisionDetail>(JsonSerializer.Serialize(wire))!;
        Assert.Equal(2, restored.ZoneVersion);
        Assert.Equal(4, restored.Retest!.ActiveSourceVersion);
        Assert.Equal(101.5m, restored.Retest.ActiveSourceTop);
        Assert.Equal(1, restored.Retest.ResistanceVersion);
        Assert.Equal(at.AddMinutes(-15), restored.Retest.ConfirmationSnapshotAsOfUtc);
        Assert.Equal(3, restored.TargetZoneVersion);
        Assert.Equal(target.KnownAtUtc, restored.TargetZoneKnownAtUtc);
        Assert.Null(restored.ActualEntryPrice);
        var lines = IntradayZoneDecisionPresentation.Lines(restored);
        Assert.Contains(lines, line => line.Contains("Frozen armed source 2 v2") && line.Contains("active source v4"));
        Assert.Contains(lines, line => line.Contains("emitted long setup consumed even if admission rejects it"));
        Assert.Contains(lines, line => line.Contains("target version 3"));
    }

    // A saved strategy retains the explicit entry package; old D serializes without the optional property.
    [Fact]
    public void Retest_RecipeRoundTripsAndRejectsUnsupportedSelection()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var control = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies/zone-bounce-volume-risk-target.bt-v1.yaml"));
        Assert.DoesNotContain("LongEntryMode", JsonSerializer.Serialize(control));
        Assert.DoesNotContain("long_entry_mode", RunConfigWriter.SerializeStrategyYaml(control));
        var strategy = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies/zone-breakout-retest.bt-v1.yaml"));
        var yaml = RunConfigWriter.SerializeStrategyYaml(strategy);
        Assert.Contains("long_entry_mode: breakout_retest", yaml);
        var path = Path.Combine(Path.GetTempPath(), "tf-zone-retest-contract-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, yaml);
            Assert.Equivalent(strategy, reader.ReadStrategy(path), strict: true);
            File.WriteAllText(path, yaml.Replace("long_entry_mode: breakout_retest", "long_entry_mode: unknown"));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(path));
        }
        finally { File.Delete(path); } // Only this synthetic, uniquely named fixture is owned by the test.
    }

    // Construct immutable alternatives with distinct zones, stops and targets; only the selected one carries sized fee evidence.
    private static BacktestCandidateTrade Candidate(DateTimeOffset at, bool selected)
    {
        var bar = new OhlcvBar("ABC", at.AddMinutes(-5), "5m", 100.5m, 101, 99, 100, 10000, "sip", "raw");
        var pivot = new IntradayZonePivotEvidence(IntradayZonePivotKind.Peak, at.AddHours(-3), at.AddMinutes(-165),
            at.AddHours(-2), new(2026, 10, 27), 99, 101);
        var zone = new IntradayZoneEvidence(selected ? 2 : 1, 1, IntradayZoneRole.Resistance, 99, 101, 2,
            at.AddHours(-2), at.AddHours(-1), pivot.PivotBarStartUtc, pivot.PivotSession, pivot);
        var evidence = new IntradayZoneSignalEvidence(zone, null, at, null, 2, 5000, 20, "fixture",
            Atr: 2, PolicyId: IntradayZoneRules.PythonVolumeSpreadComparisonPolicyId,
            VolumeSpread: new(99.95m, 100.05m, 10, 20, 1.5m, selected ? 1 : 0,
                selected ? 23 : null, selected ? 2 : null, selected ? 48 : null, selected ? 96 : null, selected ? 2 : null));
        var signal = new IntradayZoneSignal("short", "python_pattern", bar, 100,
            selected ? 103 : 102, .01m, at, at.AddHours(4), evidence, EntryCutoffUtc: at.AddMinutes(235),
            FixedTargetPrice: selected ? 95 : 97);
        return new("ABC", "Python zone volume/spread comparison", signal.Direction, at, 100, signal.InitialStopPrice,
            signal.FixedTargetPrice!.Value, signal.SessionDeadlineUtc, null, "session_deadline_pending", Math.Abs(100 - signal.InitialStopPrice),
            StrategyId: "technical.zone-volume-spread-comparison", CandidateId: "candidate") { ZoneExecution = signal };
    }
}
