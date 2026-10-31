using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PreviousSessionBreakoutTests
{
    // The normal assembler and persisted orchestrator reach the shared day kernel without daily indicator warm-up or operational activation.
    [Fact]
    public async Task SharedCandidatePath_PersistsDayIntentAndRejectsOperationalSelection()
    {
        var fixture = Fixture();
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var strategy = ReadStrategy();
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(strategy, catalog);
        var snapshots = fixture.Bars.Select(bar => new IndicatorSnapshot(bar.Ticker, bar.Timestamp, "5m", bar.Close,
            bar.Volume, null, null, null, null, null, null, null, null, null, null, null, null, null,
            DataFeed: "sip", AdjustmentPolicy: "all", MarketEvidenceReliability: "verified_same_feed")).ToArray();
        var key = StrategyDecisionRequestAssembler.CreateSetupKey(strategy.StrategyId, "5m", fixture.Bars[^2].Timestamp);
        var expiry = fixture.AsOf.AddMinutes(5);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery("SPY", key, fixture.AsOf, expiry);
        var eligibility = new StrategyEligibilityEvidence(true, "static_fixture", "SPY", fixture.AsOf, "{}");
        var run = BacktestCandidateJournal.CreateRun(new { fixture.AsOf }, "previous-session-fixture", fixture.AsOf);
        var request = StrategyDecisionRequestAssembler.Create(runtime, "SPY",
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = fixture.Bars },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = snapshots },
            Array.Empty<CatalystEvent>(), fixture.AsOf, discovery, eligibility, eligibility,
            StrategyCandidateState.DataWarming, 0, key, fixture.AsOf, expiry, 2, run.RunId,
            previousSessionContext: fixture.Context);
        using var journal = new BacktestCandidateJournal();
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
        var result = await orchestrator.EvaluateAsync(run, request);
        Assert.True(result.IsNewTrigger);
        Assert.Equal("day", result.Candidate.Horizon);
        Assert.Equal(97m, result.Decision!.OrderPlan!.InitialStopPrice);
        Assert.Equal(fixture.Bars[^2].Timestamp, result.Decision.OrderPlan.StopContextTimestampUtc);
        Assert.Equal("deep_pullback", result.Decision.OrderPlan.PendingEntry!.EntryPattern);
        Assert.Single(request.Candidate.MarketState.RequiredWarmupBarsByTimeframe);
        Assert.Equal(2, request.Candidate.MarketState.RequiredWarmupBarsByTimeframe["5m"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.EvaluateAsync(run,
            request with { SelectionMode = StrategySelectionMode.RunLive }));
    }

    // A missing mandatory source interval suppresses economics from its first affected session, while unused warm-up dates stay excluded.
    [Theory]
    [InlineData("complete", -1)]
    [InlineData("prior_gap", 0)]
    [InlineData("current_gap", 5)]
    [InlineData("empty_current", 0)]
    public void SourceCoverage_ReportsFirstAffectedRequestedInterval(string mutation, int missingMinute)
    {
        var fixture = Fixture();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var sessions = fixture.Bars.GroupBy(bar => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var priorDay = new DateOnly(2026, 7, 2);
        var currentDay = new DateOnly(2026, 7, 6);
        if (mutation == "prior_gap") sessions[priorDay] = sessions[priorDay].Skip(1).ToArray();
        if (mutation == "current_gap") sessions[currentDay] = sessions[currentDay].Take(1).ToArray();
        if (mutation == "empty_current") sessions.Remove(currentDay);
        var start = fixture.Bars[^2].Timestamp;
        var boundary = BacktestRunner.FindIntradaySourceGap(sessions, fixture.Context.SessionSchedules,
            new Dictionary<DateOnly, DateOnly> { [currentDay] = priorDay }, zone, start, fixture.AsOf);
        Assert.Equal(missingMinute < 0 ? (DateTimeOffset?)null : start.AddMinutes(missingMinute), boundary);
    }

    // Mirror each approved route with the same calendar and price geometry, including the approved deep short above PDL.
    [Theory]
    [InlineData(false, "deep_pullback", 98, 98)]
    [InlineData(false, "retest", 100, 100)]
    [InlineData(false, "shallow_pullback", 101, 101)]
    [InlineData(true, "deep_pullback", 98, 97)]
    [InlineData(true, "retest", 100, 95)]
    [InlineData(true, "shallow_pullback", 101, 94)]
    public void AllRoutes_PreserveBreakoutStopAndActivateAfterConfirmation(bool shortSide, string pattern,
        int longExtreme, int expectedLimit)
    {
        var fixture = Fixture(shortSide, longExtreme);
        var result = new PreviousSessionBreakoutEvaluator().Evaluate(ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf);
        Assert.Null(result.RejectionReason);
        var signal = Assert.IsType<PreviousSessionBreakoutSignal>(result.Signal);
        Assert.Equal(shortSide ? "short" : "long", signal.Direction);
        Assert.Equal(pattern, signal.EntryPattern);
        Assert.Equal(expectedLimit, signal.LimitPrice);
        Assert.Equal(shortSide ? 98 : 97, signal.InitialStopPrice);
        Assert.Equal(fixture.Bars[^2].Timestamp, signal.BreakoutTimestampUtc);
        Assert.Equal(fixture.Bars[^1].Timestamp, signal.ConfirmationTimestampUtc);
        Assert.Equal(fixture.AsOf, signal.ActiveAtUtc);
        Assert.Equal(fixture.AsOf.AddMinutes(5), signal.ExpiresAtUtc);
        Assert.Equal(100m, signal.PreviousSessionHigh);
        Assert.Equal(95m, signal.PreviousSessionLow);
    }

    // The calendar explicitly skips the observed July holiday and weekend instead of taking the previous calendar date.
    [Fact]
    public void HolidayAndWeekend_UsePrecedingExchangeSession()
    {
        var fixture = Fixture();
        Assert.Equal(new DateOnly(2026, 7, 2), DateOnly.FromDateTime(fixture.Bars[0].Timestamp.UtcDateTime));
        Assert.NotNull(new PreviousSessionBreakoutEvaluator().Evaluate(ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf).Signal);
        var missingDate = fixture.Context.SessionSchedules.Where(pair => pair.Key != new DateOnly(2026, 7, 3))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("previous_exchange_session_unavailable", new PreviousSessionBreakoutEvaluator().Evaluate(
            ReadStrategy(), fixture.Bars, fixture.Context with { SessionSchedules = missingDate }, fixture.AsOf).RejectionReason);
    }

    // Incomplete prior coverage, a missing immediate confirmation interval and future receipt evidence cannot create an order.
    [Theory]
    [InlineData("prior_gap", "previous_session_five_minute_coverage_incomplete")]
    [InlineData("confirmation_gap", "confirmation_not_immediate_completed_candle")]
    [InlineData("future_receipt", "previous_session_bar_evidence_invalid")]
    [InlineData("no_confirmation", "breakout_or_immediate_confirmation_not_satisfied")]
    public void MissingOrFutureEvidence_RejectsWithoutInventingLevels(string mutation, string reason)
    {
        var fixture = Fixture();
        if (mutation == "prior_gap") fixture.Bars.RemoveAt(10);
        if (mutation == "confirmation_gap") fixture.Bars[^1] = fixture.Bars[^1] with { Timestamp = fixture.Bars[^1].Timestamp.AddMinutes(-1) };
        if (mutation == "future_receipt") fixture.Bars[^1] = fixture.Bars[^1] with { KnownAtUtc = fixture.AsOf.AddSeconds(1) };
        if (mutation == "no_confirmation") fixture.Bars[^1] = fixture.Bars[^1] with { Close = 101m };
        var result = new PreviousSessionBreakoutEvaluator().Evaluate(ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf);
        Assert.Null(result.Signal);
        Assert.Equal(reason, result.RejectionReason);
    }

    // A crossing consumed before the prior position resolved cannot be retained as a later opportunity.
    [Fact]
    public void Rearm_RequiresFreshBreakoutAfterResolution()
    {
        var fixture = Fixture();
        Assert.Equal("breakout_precedes_prior_order_resolution", new PreviousSessionBreakoutEvaluator().Evaluate(
            ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf, fixture.Bars[^2].Timestamp.AddSeconds(1)).RejectionReason);
    }

    // Entry normalization never worsens the chosen limit, while an off-grid structural stop remains an explicit rejection.
    [Fact]
    public void TickNormalization_PreservesExactStructuralStop()
    {
        var fixture = Fixture();
        fixture.Bars[^1] = fixture.Bars[^1] with { Low = 98.005m };
        var evaluator = new PreviousSessionBreakoutEvaluator();
        Assert.Equal(98m, evaluator.Evaluate(ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf).Signal!.LimitPrice);
        fixture.Bars[^2] = fixture.Bars[^2] with { Low = 97.005m };
        Assert.Equal("entry_or_structural_stop_incompatible_with_tick", evaluator.Evaluate(
            ReadStrategy(), fixture.Bars, fixture.Context, fixture.AsOf).RejectionReason);
    }

    // The valid increment must be supplied and every exact order price must lie on it.
    [Theory]
    [InlineData(0, "previous_session_tick_or_calendar_unavailable")]
    [InlineData(3, "entry_or_structural_stop_incompatible_with_tick")]
    public void TickEvidence_CannotBeInvented(decimal tick, string expectedReason)
    {
        var fixture = Fixture();
        Assert.Equal(expectedReason, new PreviousSessionBreakoutEvaluator().Evaluate(ReadStrategy(), fixture.Bars,
            fixture.Context with { TickSize = tick }, fixture.AsOf).RejectionReason);
    }

    // Regulatory boundaries remain independent of the selected study dates and cannot be guessed across the one-dollar boundary.
    [Theory]
    [InlineData(1, 100, 0.01)]
    [InlineData(0.1, 0.9, 0.0001)]
    public void DocumentedOrderIncrement_PreservesSourceAndPriceBand(decimal low, decimal high, decimal tick)
    {
        var evidence = UsEquityOrderIncrementResolver.Resolve(new DateOnly(2026, 7, 6), low, high);
        Assert.Equal(tick, evidence!.TickSize);
        Assert.Equal(UsEquityOrderIncrementResolver.PolicyId, evidence.PolicyId);
        Assert.Equal(UsEquityOrderIncrementResolver.SourceUrl, evidence.SourceUrl);
        Assert.NotNull(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2018, 10, 1), low, high));
        Assert.NotNull(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2027, 10, 29), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2018, 9, 28), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2027, 11, 1), low, high));
        Assert.Null(UsEquityOrderIncrementResolver.Resolve(new DateOnly(2026, 7, 6), 0.9m, 1.1m));
    }

    // A scheduled early close truncates eligibility using the calendar close, never the generic 16:00 wall clock.
    [Fact]
    public void EarlyClose_UsesFinalBarOpeningAsDeadline()
    {
        var fixture = Fixture();
        var schedules = fixture.Context.SessionSchedules.ToDictionary(pair => pair.Key, pair => pair.Value);
        var date = new DateOnly(2026, 7, 6);
        schedules[date] = schedules[date] with { RegularClose = new TimeOnly(13, 0) };
        var signal = new PreviousSessionBreakoutEvaluator().Evaluate(ReadStrategy(), fixture.Bars,
            fixture.Context with { SessionSchedules = schedules }, fixture.AsOf).Signal;
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 55, 0, TimeSpan.Zero), signal!.SessionDeadlineUtc);
    }

    // Saved-plan snapshots retain the optional fixed policy and content identity; existing swing serialization omits it.
    [Fact]
    public void ResearchCatalogAndYaml_RoundTripWithoutChangingSwingIdentityShape()
    {
        var strategy = ReadStrategy();
        Assert.Equal("day", StrategyHorizonResolver.Resolve(strategy));
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var artifact = Assert.Single(catalog.GetSelectable(StrategySelectionMode.Backtest),
            item => item.Identity.StrategyId == strategy.StrategyId);
        Assert.Equal("1.0.0", artifact.Identity.SemanticVersion);
        Assert.DoesNotContain(artifact, catalog.GetSelectable(StrategySelectionMode.RunLive, authorizedValidated: [artifact.Identity]));
        var path = Path.Combine(Path.GetTempPath(), "previous-session-fixture-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(strategy));
            Assert.Equal(strategy, new SimpleYamlReader().ReadStrategy(path));
        }
        finally { File.Delete(path); }
        var swing = new SimpleYamlReader().ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies", "recovery-sma5-exit.bt-v2.yaml"));
        Assert.DoesNotContain("previousSessionBreakout", JsonSerializer.Serialize(swing, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // Load the ordinary catalog recipe rather than manufacturing a second strategy definition for the fixtures.
    private static StrategyDefinition ReadStrategy() => new SimpleYamlReader().ReadStrategy(Path.Combine(
        TestRepository.FindRoot(), "configs", "backtest", "strategies", "previous-day-breakout.bt-v1.yaml"));

    // Build complete regular-session evidence followed by the opening breakout and its immediately completed confirmation.
    private static (List<OhlcvBar> Bars, PreviousSessionBreakoutContext Context, DateTimeOffset AsOf) Fixture(bool shortSide = false, int extreme = 98)
    {
        var bars = new List<OhlcvBar>();
        var priorOpen = new DateTimeOffset(2026, 7, 2, 13, 30, 0, TimeSpan.Zero);
        for (var index = 0; index < 78; index++) bars.Add(Bar(priorOpen.AddMinutes(5 * index), 98, 100, 95, 99));
        var open = new DateTimeOffset(2026, 7, 6, 13, 30, 0, TimeSpan.Zero);
        bars.Add(Bar(open, 99, 103, 97, 101));
        bars.Add(Bar(open.AddMinutes(5), 101, 104, extreme, 102));
        if (shortSide)
            for (var index = 78; index < bars.Count; index++)
            {
                var bar = bars[index];
                bars[index] = bar with { Open = 195 - bar.Open, High = 195 - bar.Low, Low = 195 - bar.High, Close = 195 - bar.Close };
            }
        var schedules = Enumerable.Range(0, 5).Select(day => new DateOnly(2026, 7, 2).AddDays(day))
            .ToDictionary(day => day, day => day.Day == 2 || day.Day == 6 ? MarketSessionSchedule.RegularDay(day) : MarketSessionSchedule.Closed(day));
        return (bars, new PreviousSessionBreakoutContext(schedules, 0.01m, priorOpen.AddDays(-1)), open.AddMinutes(10));
    }

    // Keep every fixture candle on the same explicit SIP adjustment basis.
    private static OhlcvBar Bar(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close) =>
        new("SPY", time, "5m", open, high, low, close, 1000m, "sip", "all", time.AddMinutes(5));
}
