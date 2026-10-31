using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class IntradayZoneWorkflowIntegrationTests
{
    // The comparison uses its explicit candle assumptions without accepting a quote manifest or changing the original zone policy.
    [Fact]
    public void PythonComparisonProfile_UsesSeparateCandlePolicyAndRejectsSipManifest()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifact = Assert.Single(new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader)
            .GetSelectable(StrategySelectionMode.Backtest), item => item.Identity.StrategyId == "technical.zone-python-comparison");
        var strategy = artifact.ResolvedStrategy;
        var profile = reader.ReadBacktestRun(Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));
        var run = BacktestExecutionProfileResolver.Resolve(profile, [strategy]);
        Assert.Equal("Python zone comparison", strategy.StrategyName);
        Assert.Equal(ExecutionReplayPolicy.PythonZoneBounceBreakdownV1, run.Engine.ExecutionReplayPolicyId);
        Assert.False(ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId));
        Assert.Equal("raw", MarketEvidenceOperationalRules.ForExecutionTimingPolicy(artifact.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy], ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1));
        Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile with {
            Engine = profile.Engine with { ExecutionSourceManifestPath = "foreign-sip-manifest.json" } }, [strategy]));
    }

    // Both clients display fixed target geometry and distinguish optional long volume from a missing mandatory input.
    [Fact]
    public void PythonComparisonPresentation_PreservesFixedPricesAndDoesNotRequireLongVolume()
    {
        var time = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var detail = new BacktestZoneDecisionDetail("candidate", "technical.zone-python-comparison", "ABC", "long", "python_bounce",
            4, 2, "Support", 99.5m, 100.5m, 2, time.AddHours(-2), time.AddMinutes(-105), time.AddHours(-1),
            time.AddHours(-1), null, time.AddMinutes(-5), time, 101, 100, null, null, 0, 99,
            time.AddHours(4), "closed", "target_hit", 101.1m, 105, IntradayZoneRules.PythonComparisonPolicyId,
            time.AddHours(3).AddMinutes(30), 105, 2, 7, 105, 106);
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        Assert.Equal(105, wire.FixedTargetPrice);
        Assert.Equal(detail, JsonSerializer.Deserialize<BacktestZoneDecisionDetail>(JsonSerializer.Serialize(detail)));
        Assert.Equal(wire, JsonSerializer.Deserialize<TradingFlow.Contracts.V1.RunZoneDecisionDetail>(JsonSerializer.Serialize(wire)));
        var lines = IntradayZoneDecisionPresentation.Lines(wire);
        Assert.Contains(lines, line => line.Contains("Fixed target 105") && line.Contains("ATR14 2") && line.Contains("next zone 7"));
        Assert.Contains(lines, line => line.Contains("long entry has no volume filter"));
        Assert.Contains(lines, line => line.Contains("Five-minute candle execution diagnostic"));
        Assert.DoesNotContain(lines, line => line.Contains("RVOL unavailable") || line.Contains("Historical SIP quote/trade execution"));
    }

    // Normal catalog selection keeps a distinct immutable identity and forbids all operational modes.
    [Fact]
    public void OrdinaryCatalog_RegistersApprovedZoneRecipeOnlyForBacktests()
    {
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var artifact = Assert.Single(catalog.GetSelectable(StrategySelectionMode.Backtest), item => item.Identity.StrategyId == "technical.zone-intraday");
        Assert.Equal("Strategy 1 Intraday", artifact.ResolvedStrategy.StrategyName);
        Assert.Equal("15m", artifact.ResolvedStrategy.Timeframe);
        Assert.Equal("5m", artifact.ResolvedStrategy.Execution.Timeframe);
        Assert.Equal(IntradayZoneRules.ExecutionTimingPolicy, artifact.AdmissionProfile.ExecutionTimingPolicy);
        Assert.Equal("raw", MarketEvidenceOperationalRules.ForExecutionTimingPolicy(artifact.AdmissionProfile.ExecutionTimingPolicy).RequiredAdjustmentPolicy);
        foreach (var mode in new[] { StrategySelectionMode.CreatePaperExperiment, StrategySelectionMode.DiagnosticReplay,
            StrategySelectionMode.RunPaperExperiment, StrategySelectionMode.RunPaperShadow, StrategySelectionMode.RunLive })
            Assert.DoesNotContain(catalog.GetSelectable(mode, [artifact.Identity], [artifact.Identity]), item => item.Identity == artifact.Identity);
    }

    // Generated ordinary snapshots retain the exact reviewed recipe, including no EMA entry gate.
    [Fact]
    public void StrategySnapshot_RoundTripsZoneRulesAndPreservesNoEmaFilter()
    {
        var strategy = Runtime().Definition;
        var path = Path.Combine(Path.GetTempPath(), "tf-zone-snapshot-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equal(strategy, new SimpleYamlReader().ReadStrategy(path));
            Assert.False(strategy.EntryRules.RequirePriceAboveEma20);
            Assert.Null(strategy.EntryRules.PreviousSessionBreakout);
            Assert.Null(strategy.EntryRules.OpeningRangeBreakout);
        }
        finally { File.Delete(path); }
    }

    // Ordinary profiles resolve historical quote execution and reject every candle execution fallback.
    [Fact]
    public void SavedProfile_RequiresHistoricalSipMarketPolicy()
    {
        var profile = new SimpleYamlReader().ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
        var strategy = Runtime().Definition;
        var run = BacktestExecutionProfileResolver.Resolve(profile, [strategy]);
        Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1, run.Engine.ExecutionReplayPolicyId);
        Assert.Equal(new[] { "5m" }, run.Intervals);
        foreach (var policy in new[] { ExecutionReplayPolicy.IntradayRthFiveMinuteV1, ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1,
            ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, ExecutionReplayPolicy.DailyRthSipPortfolioV1 })
            Assert.Throws<InvalidOperationException>(() => BacktestExecutionProfileResolver.Resolve(profile, [strategy], policy));
    }

    // The normal kernel consumes an immutable completed signal once and returns a genuine zone plan with no ORB fields.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedKernel_UsesTypedZoneIntentAtConfirmationClock(bool shortSide)
    {
        var runtime = Runtime();
        var start = DateTimeOffset.Parse("2026-10-27T13:30:00Z");
        var bars = Enumerable.Range(0, 9).Select(index => Bar(start.AddMinutes(index * 5), "5m", 100)).ToArray();
        var confirmation = bars[^1] with { Close = shortSide ? 99 : 101, High = 102, Low = 98 };
        bars[^1] = confirmation;
        var fifteen = new[] { Bar(start, "15m", 100), Bar(start.AddMinutes(15), "15m", 100), Bar(start.AddMinutes(30), "15m", 100) };
        var asOf = confirmation.Timestamp.AddMinutes(5);
        var pivot = new IntradayZonePivotEvidence(shortSide ? IntradayZonePivotKind.Peak : IntradayZonePivotKind.Valley,
            start.AddDays(-1), start.AddDays(-1).AddMinutes(15), start, new(2026, 10, 26), 99.5m, 100.5m);
        var zone = new IntradayZoneEvidence(1, 1, shortSide ? IntradayZoneRole.Resistance : IntradayZoneRole.Support,
            99.5m, 100.5m, 1, start, start, pivot.PivotBarStartUtc, pivot.PivotSession, pivot);
        var evidence = new IntradayZoneSignalEvidence(zone, null, asOf, 100, 2, 1000, 20, MarketEvidenceProfile.ProductionVersion);
        var signal = new IntradayZoneSignal(shortSide ? "short" : "long", "rejection_bounce", confirmation, confirmation.Close,
            shortSide ? 102 : 98, .01m, asOf, start.AddHours(6).AddMinutes(25), evidence);
        var context = new IntradayZoneDecisionContext(new("ABC", start, new(2026, 10, 27), [zone], []), signal);
        var key = StrategyDecisionRequestAssembler.CreateSetupKey(runtime.Definition.StrategyId, "5m", confirmation.Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery("ABC", key, asOf, signal.SessionDeadlineUtc) with { IsPersisted = true };
        var eligibility = new StrategyEligibilityEvidence(true, "synthetic_fixture", "fixture", asOf, "{}");
        var request = StrategyDecisionRequestAssembler.Create(runtime, "ABC",
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = bars, ["15m"] = fifteen },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = bars.Select(Snapshot).ToArray(), ["15m"] = fifteen.Select(Snapshot).ToArray() },
            Array.Empty<CatalystEvent>(), asOf, discovery, eligibility, eligibility, StrategyCandidateState.DataWarming, 0,
            key, asOf, signal.SessionDeadlineUtc, 2, Guid.NewGuid(), zoneContext: context);
        var result = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(result.IsTriggered);
        Assert.Equal(signal, result.OrderPlan!.ZoneEntry);
        Assert.Null(result.OrderPlan.PendingEntry);
        Assert.Equal(asOf, result.OrderPlan.TriggeredAtUtc);
        Assert.Contains("slotRelativeVolume", result.CanonicalDecisionJson);
        var late = request with { AsOfUtc = asOf.AddMinutes(5) };
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(late));
    }

    // Shared client presentation copies original zone/filter clocks and never implies a fill from planned prices.
    [Fact]
    public void ApiPresentation_PreservesZoneEvidenceAndMissingFills()
    {
        var time = DateTimeOffset.Parse("2026-10-27T15:00:00Z");
        var detail = new BacktestZoneDecisionDetail("candidate", "technical.zone-intraday", "ABC", "short", "breakout_retest",
            4, 2, "Resistance", 99.5m, 100.5m, 2, time.AddHours(-2), time.AddMinutes(-105), time.AddHours(-1),
            time.AddHours(-1), time.AddMinutes(-15), time.AddMinutes(-5), time, 99, 100, 2, 1000, 20, 101,
            time.AddHours(4), "not_filled", "quote_stale", null, null);
        var wire = Assert.Single(RunPortfolioStateProjection.ProjectZones([detail]));
        var lines = IntradayZoneDecisionPresentation.Lines(wire);
        Assert.Contains(lines, line => line.Contains("quote_stale"));
        Assert.Contains(lines, line => line.Contains("actual entry unavailable"));
        Assert.DoesNotContain(lines, line => line.Contains("Opening range"));
        Assert.Equal(detail, JsonSerializer.Deserialize<BacktestZoneDecisionDetail>(JsonSerializer.Serialize(detail)));
    }

    // Use the same validated artifact the ordinary saved-plan workflow resolves.
    private static AuthorizedRuntimeStrategy Runtime()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        return new StrategyRunArtifactValidator(reader, new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader))
            .ReadAndValidateRuntimeArtifact(Path.Combine(root, "configs", "backtest", "strategies", "intraday-zone.bt-v1.yaml"), StrategySelectionMode.Backtest);
    }

    // Synthetic raw-SIP evidence is isolated from provider archives and the operator database.
    private static OhlcvBar Bar(DateTimeOffset time, string timeframe, decimal close) =>
        new("ABC", time, timeframe, 100, 102, 98, close, 2000, "sip", "raw");

    // Only direct consumers of this contract need initialized fields; standard indicators are not recomputed in kernel fixtures.
    private static IndicatorSnapshot Snapshot(OhlcvBar bar) => new IndicatorEngine(new MarketEvidenceProfile("fixture", 20, 20, "America/New_York",
        operationalRules: MarketEvidenceOperationalRules.Production with { RequiredAdjustmentPolicy = "raw" })).Compute([bar]).Single()
        with { Vwap = 100, SlotRelativeVolume = 2, SlotMedianVolume = 1000, SlotRelativeVolumeSampleCount = 20,
            MarketEvidenceProfileVersion = MarketEvidenceProfile.ProductionVersion, RelativeVolumeCohort = "regular",
            RelativeVolumeMinimumSamples = 20, MarketEvidenceReliability = "verified_same_feed" };
}
