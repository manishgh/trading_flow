using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class OpeningRangeBreakoutTests
{
    // Fractional provider extrema stay untouched while submitted levels retain the full tick buffer and round away from the range.
    [Theory]
    [InlineData(false, 100.0051, 94.9976, 0.01, 100.02, 94.99)]
    [InlineData(true, 100.0051, 94.9976, 0.01, 94.98, 100.01)]
    [InlineData(false, 100, 95, 0.01, 100.01, 95)]
    [InlineData(true, 100, 95, 0.01, 94.99, 100)]
    [InlineData(false, 0.95001, 0.90009, 0.0001, 0.9502, 0.9)]
    [InlineData(true, 0.95001, 0.90009, 0.0001, 0.8999, 0.9501)]
    public void OutwardRounding_PreservesRawOpeningAndFullTickBuffer(bool shortSide, decimal high, decimal low,
        decimal tick, decimal trigger, decimal stop)
    {
        var fixture = Fixture(shortSide);
        var open = (high + low) / 2m;
        for (var i = fixture.Bars.Count - 3; i < fixture.Bars.Count; i++)
            fixture.Bars[i] = fixture.Bars[i] with { Open = open, High = high, Low = low,
                Close = open + (shortSide ? -1m : 1m) * (high - low) / 4m };
        fixture = fixture with { Context = fixture.Context with { TickSize = tick }, Snapshots = Compute(fixture.Bars, fixture.Context) };
        var originalBars = fixture.Bars.ToArray();
        var signal = Evaluate(fixture, outwardRounding: true).Signal!;
        Assert.NotNull(signal);
        Assert.Equal(trigger, signal.LimitPrice);
        Assert.Equal(stop, signal.InitialStopPrice);
        Assert.Equal(high, signal.OpeningRangeHigh);
        Assert.Equal(low, signal.OpeningRangeLow);
        Assert.Equal(originalBars, fixture.Bars);
        Assert.Equal(0m, trigger % tick);
        Assert.Equal(0m, stop % tick);
        Assert.True(shortSide ? trigger <= low - tick && stop >= high : trigger >= high + tick && stop <= low);
        if (high % tick != 0m || low % tick != 0m)
            Assert.Equal("entry_or_structural_stop_incompatible_with_tick", Evaluate(fixture).RejectionReason);
        else
            Assert.Equal(Evaluate(fixture).Signal, signal);
    }

    // The first completed fifteen minutes determine both directions, opposite-range stops and a full-session stop intent.
    [Theory]
    [InlineData(false, "long", 100.01, 95)]
    [InlineData(true, "short", 94.99, 100)]
    public void OpeningRange_ArmsAt0945UsingOpeningBodyAndTwentySessionMedian(bool shortSide,
        string direction, decimal trigger, decimal stop)
    {
        var fixture = Fixture(shortSide);
        var signal = Evaluate(fixture).Signal!;
        Assert.NotNull(signal);
        Assert.Equal(direction, signal.Direction);
        Assert.Equal(IntradayEntryOrderMode.StopMarket, signal.EntryOrderMode);
        Assert.Equal(trigger, signal.LimitPrice);
        Assert.Equal(stop, signal.InitialStopPrice);
        Assert.Equal(100m, signal.OpeningRangeHigh);
        Assert.Equal(95m, signal.OpeningRangeLow);
        Assert.Equal(fixture.AsOf, signal.ActiveAtUtc);
        Assert.Equal(fixture.AsOf, signal.OpeningRangeCompletedAtUtc);
        Assert.Equal(2m, signal.OpeningRelativeVolume);
        Assert.Equal(20, signal.OpeningVolumeBaselineSampleCount);
        Assert.Equal(2, signal.MaxFilledParentTradesPerSession);
        Assert.True(signal.RequireRangeReturnBeforeReentry);
        Assert.Equal(signal.SessionDeadlineUtc, signal.ExpiresAtUtc);
        Assert.Equal(105m, signal.PreviousSessionHigh);
        Assert.Equal(90m, signal.PreviousSessionLow);
        Assert.Equal("outside_opening_range_entry_window", new OpeningRangeBreakoutEvaluator().Evaluate(
            ReadStrategy(), fixture.Bars.Take(fixture.Bars.Count - 1).ToArray(), fixture.Snapshots,
            fixture.Context, fixture.AsOf.AddMinutes(-5)).RejectionReason);
    }

    // Later prices and volume cannot shift the already completed range or substitute a later cumulative-volume ratio.
    [Fact]
    public void LaterSessionEvidence_DoesNotMoveRangeOrOpeningVolume()
    {
        var fixture = Fixture();
        var initial = Evaluate(fixture).Signal!;
        fixture.Bars.Add(Bar(fixture.AsOf, 99, 130, 80, 120, 1000000m));
        var later = fixture with { AsOf = fixture.AsOf.AddMinutes(5) };
        var signal = Evaluate(later).Signal!;
        Assert.Equal(initial.OpeningRangeHigh, signal.OpeningRangeHigh);
        Assert.Equal(initial.OpeningRangeLow, signal.OpeningRangeLow);
        Assert.Equal(initial.OpeningRelativeVolume, signal.OpeningRelativeVolume);
        Assert.Equal(initial.OpeningRangeCompletedAtUtc, signal.OpeningRangeCompletedAtUtc);
        Assert.Equal(later.AsOf, signal.ActiveAtUtc);
    }

    // Missing immediate historical samples, missing range candles and future receipts remain explicit unavailable evidence.
    [Theory]
    [InlineData("baseline_gap", "opening_range_volume_baseline_unavailable")]
    [InlineData("opening_gap", "opening_range_current_session_coverage_incomplete")]
    [InlineData("prior_gap", "previous_session_five_minute_coverage_incomplete")]
    [InlineData("future_receipt", "opening_range_completed_bar_evidence_invalid")]
    [InlineData("doji", "opening_range_doji")]
    [InlineData("equal_volume", "opening_range_relative_volume_not_above_one")]
    [InlineData("zero_median", "opening_range_volume_baseline_unavailable")]
    public void MissingOrUnqualifiedOpeningEvidence_DoesNotArm(string mutation, string reason)
    {
        var fixture = Fixture();
        if (mutation == "baseline_gap") fixture.Bars.RemoveAt(1);
        if (mutation == "opening_gap") fixture.Bars.RemoveAt(fixture.Bars.Count - 2);
        if (mutation == "prior_gap") fixture.Bars.RemoveAt(fixture.Bars.Count - 10);
        if (mutation == "future_receipt") fixture.Bars[^1] = fixture.Bars[^1] with { KnownAtUtc = fixture.AsOf.AddSeconds(1) };
        if (mutation == "doji") fixture.Bars[^1] = fixture.Bars[^1] with { Close = fixture.Bars[^3].Open };
        if (mutation == "equal_volume")
            for (var i = fixture.Bars.Count - 3; i < fixture.Bars.Count; i++) fixture.Bars[i] = fixture.Bars[i] with { Volume = 1000m };
        if (mutation == "zero_median")
            for (var i = 0; i < fixture.Bars.Count - 3; i++) fixture.Bars[i] = fixture.Bars[i] with { Volume = 0m };
        fixture = fixture with { Snapshots = Compute(fixture.Bars, fixture.Context), Context = fixture.Context with {
            OpeningRangeBaselineBars = OpeningBaseline(fixture.Bars, fixture.Context.SessionSchedules, new DateOnly(2026, 10, 27)) } };
        var result = Evaluate(fixture);
        Assert.Null(result.Signal);
        Assert.Equal(reason, result.RejectionReason);
    }

    // Explicit session schedules determine early-close expiry and New York daylight-saving changes.
    [Theory]
    [InlineData(2026, 10, 27, 13)]
    [InlineData(2026, 11, 2, 14)]
    public void Calendar_UsesHistoricalExchangeOffsetAndAuthoritativeEarlyClose(int year, int month, int day, int utcHour)
    {
        var fixture = Fixture(date: new DateOnly(year, month, day), earlyClose: true);
        var signal = Evaluate(fixture).Signal!;
        Assert.Equal(utcHour, signal.ActiveAtUtc.Hour);
        Assert.Equal(45, signal.ActiveAtUtc.Minute);
        Assert.Equal(signal.ActiveAtUtc.AddHours(3).AddMinutes(10), signal.SessionDeadlineUtc);
        var calendar = fixture.Context.SessionSchedules.ToDictionary(pair => pair.Key, pair => pair.Value);
        calendar.Remove(new DateOnly(year, month, day).AddDays(-1));
        Assert.Equal("previous_exchange_session_unavailable", new OpeningRangeBreakoutEvaluator().Evaluate(ReadStrategy(),
            fixture.Bars, fixture.Snapshots, fixture.Context with { SessionSchedules = calendar }, fixture.AsOf).RejectionReason);
    }

    // Frozen RVOL evidence must retain its profile, sample count, denominator and feed instead of trusting a bare ratio.
    [Theory]
    [InlineData("samples")]
    [InlineData("profile")]
    [InlineData("ratio")]
    [InlineData("feed")]
    public void VolumeEvidence_RejectsUnboundOrInconsistentSnapshot(string mutation)
    {
        var fixture = Fixture();
        var snapshots = fixture.Snapshots.ToArray();
        snapshots[^1] = mutation switch
        {
            "samples" => snapshots[^1] with { RelativeVolumeSampleCount = 19 },
            "profile" => snapshots[^1] with { MarketEvidenceProfileVersion = "other" },
            "ratio" => snapshots[^1] with { RelativeVolume = 3m },
            _ => snapshots[^1] with { DataFeed = "iex" }
        };
        Assert.Equal("opening_range_volume_baseline_unavailable", Evaluate(fixture with { Snapshots = snapshots }).RejectionReason);
    }

    // Query coverage and generic RVOL counts cannot replace an actual historical 09:35 candle.
    [Fact]
    public void HistoricalOpeningGap_WithValidWatermarkAndTwentyGenericSamples_DoesNotArm()
    {
        var fixture = Fixture();
        fixture.Bars.RemoveAt(1);
        var snapshots = Compute(fixture.Bars, fixture.Context);
        Assert.Equal(20, snapshots[^1].RelativeVolumeSampleCount);
        Assert.All(fixture.Bars, bar => Assert.NotNull(bar.CoverageVerifiedThroughUtc));
        var context = fixture.Context with { OpeningRangeBaselineBars = OpeningBaseline(fixture.Bars,
            fixture.Context.SessionSchedules, new DateOnly(2026, 10, 27)) };
        Assert.Equal("opening_range_volume_baseline_unavailable", Evaluate(fixture with { Context = context, Snapshots = snapshots }).RejectionReason);
    }

    // The immediate twentieth session and intervening calendar dates are required even when an older substitute exists.
    [Theory]
    [InlineData("missing_twentieth")]
    [InlineData("missing_calendar")]
    [InlineData("older_substitute")]
    public void HistoricalOpeningBaseline_CannotSkipMissingSessionOrCalendar(string mutation)
    {
        var fixture = Fixture();
        var calendar = fixture.Context.SessionSchedules.ToDictionary(pair => pair.Key, pair => pair.Value);
        var first = calendar.Values.Where(session => session.IsTradingDay).Min(session => session.TradeDate);
        var baseline = fixture.Context.OpeningRangeBaselineBars!.ToList();
        if (mutation == "missing_calendar") calendar.Remove(first);
        else baseline.RemoveRange(0, 3);
        if (mutation == "older_substitute")
        {
            var older = first.AddDays(-1);
            calendar[older] = MarketSessionSchedule.RegularDay(older);
            var open = ExchangeOpen(older, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
            baseline.InsertRange(0, Enumerable.Range(0, 3).Select(slot => Bar(open.AddMinutes(slot * 5), 98, 105, 90, 99, 1000)));
        }
        Assert.Equal("opening_range_volume_baseline_unavailable", Evaluate(fixture with { Context = fixture.Context with {
            SessionSchedules = calendar, OpeningRangeBaselineBars = baseline } }).RejectionReason);
    }

    // Real zero-volume opening candles remain observed samples; an absent slot remains a separate missing observation.
    [Fact]
    public void HistoricalZeroVolumeBars_AreValidObservedOpeningWindow()
    {
        var fixture = Fixture();
        for (var slot = 0; slot < 3; slot++) fixture.Bars[slot] = fixture.Bars[slot] with { Volume = 0m };
        var context = fixture.Context with { OpeningRangeBaselineBars = OpeningBaseline(fixture.Bars,
            fixture.Context.SessionSchedules, new DateOnly(2026, 10, 27)) };
        var result = Evaluate(fixture with { Context = context, Snapshots = Compute(fixture.Bars, context) });
        Assert.NotNull(result.Signal);
        Assert.Equal(20, result.Signal.OpeningVolumeBaselineSampleCount);
        Assert.NotNull(result.Signal.OpeningVolumeBaselineSha256);
    }

    // The ORB policy must be a mapping; malformed scalar/list nodes cannot silently enable its defaults.
    [Theory]
    [InlineData("  opening_range_breakout: true")]
    [InlineData("  opening_range_breakout:\n    - opening-range-breakout-v1")]
    public void Yaml_RejectsScalarOrListOpeningRangePolicy(string replacement)
    {
        var source = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", "opening-range-breakout-stop-market.v1.fixture.yaml"));
        var malformed = System.Text.RegularExpressions.Regex.Replace(source,
            @"(?m)^  opening_range_breakout:\r?\n    policy_id: [^\r\n]+\r?\n    max_filled_parent_trades_per_session: [^\r\n]+", replacement);
        Assert.NotEqual(source, malformed);
        var path = Path.Combine(Path.GetTempPath(), "orb-yaml-shape-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, malformed);
            var exception = Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
            Assert.Contains("mapping section", exception.Message);
            Assert.Contains("entry_rules.opening_range_breakout", exception.Message);
        }
        finally { File.Delete(path); }
    }

    // Both immutable versions use the shared journal/kernel/planner; sizing uses submitted geometry and legacy JSON stays unchanged.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedKernel_UsesOpeningRangePolicyAndPreservesLegacyJsonShape(bool outwardRounding, bool shortSide)
    {
        var fixture = Fixture(shortSide, fractional: outwardRounding);
        var strategy = ReadStrategy(outwardRounding);
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(strategy, catalog);
        var expiry = fixture.AsOf.AddHours(6);
        var key = StrategyDecisionRequestAssembler.CreateSetupKey(strategy.StrategyId, "5m", fixture.Bars[^1].Timestamp);
        var eligibility = new StrategyEligibilityEvidence(true, "fixture", "SPY", fixture.AsOf, "{}");
        var run = BacktestCandidateJournal.CreateRun(new { fixture.AsOf }, "opening-range-fixture", fixture.AsOf);
        var request = StrategyDecisionRequestAssembler.Create(runtime, "SPY",
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = fixture.Bars },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = fixture.Snapshots }, [], fixture.AsOf,
            StrategyDecisionRequestAssembler.CreateBacktestDiscovery("SPY", key, fixture.AsOf, expiry), eligibility, eligibility,
            StrategyCandidateState.DataWarming, 0, key, fixture.AsOf, expiry, 3, run.RunId, previousSessionContext: fixture.Context);
        Assert.False(request.Candidate.Discovery.IsPersisted);
        using var journal = new BacktestCandidateJournal();
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
        var persisted = await orchestrator.EvaluateAsync(run, request);
        var result = persisted.Decision!;
        Assert.True(result.IsTriggered, result.NoEntryReason);
        Assert.Equal(fixture.AsOf, result.OrderPlan!.SetupAvailableAtUtc);
        Assert.Equal(IntradayEntryOrderMode.StopMarket, result.OrderPlan.PendingEntry!.EntryOrderMode);
        var submitted = Evaluate(fixture, outwardRounding).Signal!;
        Assert.Equal(submitted.LimitPrice, result.OrderPlan.TriggerReferencePrice);
        Assert.Equal(submitted.InitialStopPrice, result.OrderPlan.InitialStopPrice);
        Assert.Equal(submitted.OpeningRangeHigh, result.OrderPlan.PendingEntry.OpeningRangeHigh);
        Assert.Equal(submitted.OpeningRangeLow, result.OrderPlan.PendingEntry.OpeningRangeLow);
        var planned = new SharedOrderRiskPlanner().Plan(new("SPY",
            shortSide ? PlannedOrderSide.Short : PlannedOrderSide.Long, 10000m, result.OrderPlan.TriggerReferencePrice,
            PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, result.OrderPlan.InitialStopPrice),
            new(1m, 25m), new(strategy.Execution.SlippageBps, 0m, 0m)));
        Assert.True(planned.IsAccepted);
        Assert.Equal(submitted.InitialStopPrice, planned.Order!.StopTriggerPrice);
        Assert.True(planned.Order.PlannedLoss <= 100m);
        Assert.True(planned.Order.DeployedNotional <= 2500m);
        Assert.Equal(3m, result.OrderPlan.TargetRMultiple);
        Assert.Equal("day", StrategyHorizonResolver.Resolve(strategy));
        var swing = new SimpleYamlReader().ReadStrategy(Path.Combine(root, "configs", "backtest", "strategies", "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        Assert.DoesNotContain("openingRangeBreakout", JsonSerializer.Serialize(swing, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var pending = result.OrderPlan.PendingEntry with { EntryOrderMode = IntradayEntryOrderMode.NextBarLimit,
            OpeningRangeHigh = null, OpeningRangeLow = null, OpeningRangeOpen = null, OpeningRangeClose = null,
            OpeningRangeCompletedAtUtc = null, OpeningRelativeVolume = null, OpeningVolumeBaselineSampleCount = null,
            MaxFilledParentTradesPerSession = 0, RequireRangeReturnBeforeReentry = false, OpeningVolumeBaselineSha256 = null };
        var legacyJson = JsonSerializer.Serialize(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("openingRange", legacyJson);
        Assert.DoesNotContain("entryOrderMode", legacyJson);
        Assert.DoesNotContain("maxFilledParentTradesPerSession", legacyJson);
    }

    // Session limits must be positive while the fixed baseline rejects undeclared additional filters.
    [Fact]
    public void Policy_RequiresPositiveSessionLimitAndRejectsCompetingFilters()
    {
        var strategy = ReadStrategy();
        Assert.Throws<InvalidOperationException>(() => (strategy.EntryRules.OpeningRangeBreakout! with {
            MaxFilledParentTradesPerSession = 0 }).Validate(strategy));
        (strategy.EntryRules.OpeningRangeBreakout! with { MaxFilledParentTradesPerSession = 1 }).Validate(strategy);
        Assert.Throws<InvalidOperationException>(() => strategy.EntryRules.OpeningRangeBreakout!.Validate(strategy with {
            EntryRules = strategy.EntryRules with { RequirePriceAboveVwap = true } }));
    }

    // Only the retained exact-price policy and the separately approved rounding policy can select ORB behavior.
    [Theory]
    [InlineData("opening-range-breakout-v1", true)]
    [InlineData(OpeningRangeBreakoutRules.OutwardOrderRoundingPolicyId, true)]
    [InlineData("opening-range-breakout-v2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Policy_RejectsUnknownOrMissingIdentity(string? policyId, bool supported)
    {
        var strategy = ReadStrategy();
        var policy = new OpeningRangeBreakoutRules(policyId!);
        strategy = strategy with { EntryRules = strategy.EntryRules with { OpeningRangeBreakout = policy } };
        if (supported) policy.Validate(strategy);
        else Assert.Throws<InvalidOperationException>(() => policy.Validate(strategy));
    }

    // Completed-breakout mode preserves opening filters, requires a strict post-range close and freezes that candle stop.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedBreakout_UsesCompletedPostRangeCloseAndOutwardCandleStop(bool shortSide)
    {
        var fixture = Fixture(shortSide, fractional: true);
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "opening-range-breakout.bt-v3.yaml"));
        Assert.Null(new OpeningRangeBreakoutEvaluator().Evaluate(strategy, fixture.Bars, fixture.Snapshots, fixture.Context, fixture.AsOf).Signal);
        var candle = Bar(fixture.AsOf, shortSide ? 95m : 100m, shortSide ? 95.1234m : 102m,
            shortSide ? 93m : 99.8765m, shortSide ? 94m : 101m, 1000);
        fixture.Bars.Add(candle);
        var result = new OpeningRangeBreakoutEvaluator().Evaluate(strategy, fixture.Bars, Compute(fixture.Bars, fixture.Context), fixture.Context, fixture.AsOf.AddMinutes(5));
        var signal = Assert.IsType<IntradayBreakoutSignal>(result.Signal);
        Assert.Equal(IntradayEntryOrderMode.NextBarMarket, signal.EntryOrderMode);
        Assert.Equal(shortSide ? 95.13m : 99.87m, signal.InitialStopPrice);
        Assert.Equal(candle.Close, signal.LimitPrice);
        Assert.Equal(candle.Timestamp, signal.BreakoutTimestampUtc);
        Assert.Equal(fixture.AsOf.AddMinutes(5), signal.ActiveAtUtc);
        Assert.Equal(shortSide ? 94.98m : 100.02m, signal.BreakoutTriggerPrice);
        var exact = candle with { Close = signal.BreakoutTriggerPrice!.Value };
        fixture.Bars[^1] = exact;
        Assert.Null(new OpeningRangeBreakoutEvaluator().Evaluate(strategy, fixture.Bars, Compute(fixture.Bars, fixture.Context), fixture.Context, signal.ActiveAtUtc).Signal);
    }

    // Keep evaluator calls on the same ordinary recipe used by saved-plan research selections.
    private static IntradayBreakoutEvaluation Evaluate(OpeningFixture fixture, bool outwardRounding = false) => new OpeningRangeBreakoutEvaluator().Evaluate(
        ReadStrategy(outwardRounding), fixture.Bars, fixture.Snapshots, fixture.Context, fixture.AsOf);

    // Read the stop-market recipe preserved as a test fixture (retired catalog versions 1.0.0 and 1.1.0).
    private static StrategyDefinition ReadStrategy(bool outwardRounding = false) => new SimpleYamlReader().ReadStrategy(Path.Combine(
        TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "Strategies", outwardRounding
            ? "opening-range-breakout-stop-market.v2.fixture.yaml" : "opening-range-breakout-stop-market.v1.fixture.yaml"));

    // Build twenty contiguous calendar-selected sessions and a completed opening range, all isolated synthetic SIP evidence.
    private static OpeningFixture Fixture(bool shortSide = false, DateOnly? date = null, bool earlyClose = false, bool fractional = false)
    {
        var current = date ?? new DateOnly(2026, 10, 27);
        var prior = new List<DateOnly>();
        var calendar = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var cursor = current.AddDays(-1); prior.Count < 20; cursor = cursor.AddDays(-1))
        {
            var trading = cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            calendar[cursor] = trading ? MarketSessionSchedule.RegularDay(cursor) : MarketSessionSchedule.Closed(cursor);
            if (trading) prior.Add(cursor);
        }
        calendar[current] = MarketSessionSchedule.RegularDay(current) with { RegularClose = new TimeOnly(earlyClose ? 13 : 16, 0) };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var bars = new List<OhlcvBar>();
        foreach (var day in prior.Order())
        {
            var start = ExchangeOpen(day, zone);
            for (var index = 0; index < 78; index++) bars.Add(Bar(start.AddMinutes(index * 5), 98m, 105m, 90m, 99m, 1000m));
        }
        var open = ExchangeOpen(current, zone);
        for (var index = 0; index < 3; index++) bars.Add(Bar(open.AddMinutes(index * 5), 98m,
            fractional ? 100.0051m : 100m, fractional ? 94.9976m : 95m, shortSide ? 97m : 99m, 2000m));
        var context = new IntradayBreakoutContext(calendar, 0.01m, bars[0].Timestamp.AddDays(-1),
            OpeningRangeBaselineBars: OpeningBaseline(bars, calendar, current));
        return new(bars, Compute(bars, context), context, open.AddMinutes(15));
    }

    // Use the production median implementation so missing-session and denominator tests exercise the real evidence calculation.
    private static IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> bars, IntradayBreakoutContext context) =>
        new IndicatorEngine(MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(context.SessionSchedules)).Compute(bars);

    // Select actual bars in prior opening windows without filling a missing opening slot from a later candle.
    private static IReadOnlyList<OhlcvBar> OpeningBaseline(IReadOnlyList<OhlcvBar> bars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar, DateOnly current)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var windows = calendar.Values.Where(session => session.IsTradingDay && session.TradeDate < current)
            .OrderByDescending(session => session.TradeDate).Take(20)
            .Select(session => ExchangeOpen(session.TradeDate, zone)).ToArray();
        return bars.Where(bar => windows.Any(open => bar.Timestamp >= open && bar.Timestamp < open.AddMinutes(15)))
            .OrderBy(bar => bar.Timestamp).ToArray();
    }

    // Resolve the synthetic session's local opening through the actual New York daylight-saving rules.
    private static DateTimeOffset ExchangeOpen(DateOnly date, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(9, 30), DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    // Mark each actual synthetic interval covered through completion without using that watermark to invent absent slots.
    private static OhlcvBar Bar(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new("SPY", time, "5m", open, high, low, close, volume, "sip", "all", time.AddMinutes(5), time.AddMinutes(5));

    private sealed record OpeningFixture(List<OhlcvBar> Bars, IReadOnlyList<IndicatorSnapshot> Snapshots,
        IntradayBreakoutContext Context, DateTimeOffset AsOf);
}
