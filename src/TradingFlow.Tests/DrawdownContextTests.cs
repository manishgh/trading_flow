using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class DrawdownContextTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly DateTimeOffset Start = new(2025, 1, 2, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = Start.AddDays(50);
    private static OhlcvBar[] Bars() => Enumerable.Range(0, 50).Select(i =>
        new OhlcvBar("TEST", Start.AddDays(i), "1d", 100, 110, 90, 100, 1000, "sip", "all")).ToArray();
    private static DrawdownSecurityIdentity Identity() => new("security-test", "TEST", "sip", "all", "all-v1",
        Start, AsOf.AddYears(1), Start, Hash, Binding("security-test", Bars()));
    private static DrawdownPricePrefixBinding Binding(string securityId, IReadOnlyList<OhlcvBar> bars) =>
        new(securityId, "all-v1", bars[^1].Timestamp, AsOf, bars.Count,
            DrawdownPriceBasisBinding.ComputeSha256(securityId, "all-v1", bars), Hash);
    private static DrawdownDecisionContext Context()
    {
        var bars = Bars().Select(bar => bar with { Ticker = "XLK" }).ToImmutableArray();
        var sector = new DrawdownSectorContext(Identity(), "technology", "XLK", Start, AsOf.AddYears(1),
            Start, Hash, Start, Hash, bars.Select(bar => bar.Timestamp).ToImmutableArray(),
            new("XLK", "security-xlk", "all-v1", Start, Hash, bars, Binding("security-xlk", bars)),
            new("SPY", "security-spy", "all-v1", Start, Hash,
                bars.Select(bar => bar with { Ticker = "SPY" }).ToImmutableArray(),
                Binding("security-spy", bars.Select(bar => bar with { Ticker = "SPY" }).ToArray())));
        var evaluation = new DrawdownClassifierEvaluation("catalyst-context-v1", "evaluated-classifier-v1",
            "catalyst-human-labeling-v1", Hash, Hash, true, Start);
        var coverage = new[] { (DrawdownEventScope.Company, "TEST"), (DrawdownEventScope.Sector, "technology"),
            (DrawdownEventScope.Global, "*") }.Select(pair => new DrawdownEventScopeCoverage(pair.Item1,
                pair.Item2, AsOf.AddDays(-5), AsOf, AsOf, Hash)).ToImmutableArray();
        return new(new("security-test", "all-v1", bars.Select(bar => bar.Timestamp).ToArray(), Identity(), Start, Hash),
            sector, new(Identity(), "technology", evaluation, coverage, [],
                new("technology", Start, AsOf.AddYears(1), Start, Hash)));
    }
    private static DrawdownClassifiedEvent Event(DrawdownEventScope scope = DrawdownEventScope.Global,
        DrawdownEventCategory category = DrawdownEventCategory.Policy, string target = "*",
        CatalystDirection direction = CatalystDirection.Negative, bool adverse = false) =>
        new("alpaca", "article-1", "revision-1", Hash, AsOf.AddHours(-2), null,
            AsOf.AddHours(-1), AsOf.AddMinutes(-30), AsOf.AddMinutes(-30),
            "catalyst-context-v1", "evaluated-classifier-v1", Hash, category, scope, target, direction, adverse);
    private static DrawdownQualificationAudit Analyze(DrawdownDecisionContext context,
        DrawdownQualificationRules? rules = null, OhlcvBar[]? bars = null)
    {
        bars ??= Bars();
        var snapshots = bars.Select(bar => new IndicatorSnapshot(bar.Ticker, bar.Timestamp, "1d", bar.Close,
            bar.Volume, null, 50, 1, null, null, null, null, null, null, null, null, null, null,
            DataFeed: "sip", AdjustmentPolicy: "all")).ToArray();
        return DrawdownQualificationAnalyzer.Analyze(bars, snapshots, 49, AsOf,
            rules ?? new("gate", RequireSectorContext: true, RequireEventContext: true), context: context);
    }

    [Fact]
    public void CompleteContextQualifiesAndStockMinusSectorUsesFiveSessionDifference()
    {
        var context = Context();
        var sectorBars = context.Sector!.SectorBenchmark.Bars.SetItem(49,
            context.Sector.SectorBenchmark.Bars[49] with { Close = 105 });
        context = context with { Sector = context.Sector with { SectorBenchmark = context.Sector.SectorBenchmark with { Bars = sectorBars,
            PricePrefixBinding = Binding("security-xlk", sectorBars) } } };
        var audit = Analyze(context, new("gate", "stock_minus_sector_return_5", -.05m, -.05m, RequireEventContext: true));
        Assert.Equal("qualifies", audit.Verdict);
        Assert.Equal(-.05m, audit.Context!.Metrics["stock_minus_sector_return_5"].Value);
        Assert.Equal(0, audit.Context.EventCount);
    }

    [Theory]
    [InlineData("ticker", "historical_security_identity_mismatch")]
    [InlineData("mapping", "historical_sector_mapping_interval_missing")]
    [InlineData("future", "sector_mapping_or_calendar_not_available_asof")]
    [InlineData("session", "sector_benchmark_session_mismatch")]
    [InlineData("revision", "history_security_or_adjustment_revision_mismatch")]
    public void SectorAndHistoryNeverUseMismatchedOrUnavailableEvidence(string mutation, string reason)
    {
        var context = Context();
        context = mutation switch {
            "ticker" => context with { Sector = context.Sector! with { Identity = Identity() with { Ticker = "OTHER" } } },
            "mapping" => context with { Sector = context.Sector! with { MappingEffectiveFromUtc = AsOf.AddDays(-2) } },
            "future" => context with { Sector = context.Sector! with { MappingKnownAtUtc = AsOf.AddSeconds(1) } },
            "session" => context with { Sector = context.Sector! with { SectorBenchmark = context.Sector.SectorBenchmark with {
                Bars = context.Sector.SectorBenchmark.Bars.RemoveAt(47) } } },
            _ => context with { History = context.History! with { AdjustmentVersion = "revised-later" } }
        };
        var audit = Analyze(context, mutation == "revision" ? new("gate", MinimumMaturedEpisodes: 1) : null);
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal(reason, audit.Reason);
    }

    [Theory]
    [InlineData(DrawdownEventCategory.Global)]
    [InlineData(DrawdownEventCategory.Financial)]
    [InlineData(DrawdownEventCategory.Policy)]
    public void NegativeGlobalEventsVetoOnlyWhenAdverseGateIsEnabled(DrawdownEventCategory category)
    {
        var context = Context();
        context = context with { Events = context.Events! with { Events = [Event(category: category)] } };
        var sufficient = Analyze(context, new("gate", RequireEventContext: true));
        Assert.Equal("qualifies", sufficient.Verdict);
        var rejected = Analyze(context, new("gate", RejectAdverseEvents: true));
        Assert.Equal("rejected", rejected.Verdict);
        Assert.Equal("observed_adverse_global_event", rejected.Reason);
        Assert.Equal(1, rejected.Context!.AdverseGlobalEventCount);
    }

    [Theory]
    [InlineData("coverage", "event_scope_coverage_unavailable")]
    [InlineData("stale", "event_scope_coverage_interval_or_age_mismatch")]
    [InlineData("evaluation", "event_classifier_evaluation_unavailable")]
    [InlineData("classifier", "event_revision_or_classifier_provenance_mismatch")]
    [InlineData("ticker", "event_target_or_scope_mismatch")]
    [InlineData("sector", "event_target_or_scope_mismatch")]
    [InlineData("revision", "event_revision_order_ambiguous")]
    public void EmptyEventsAndUnrelatedEvaluationsAreNotProofOfCoverage(string mutation, string reason)
    {
        var original = Context();
        var events = original.Events! with { Events = [Event()] };
        events = mutation switch {
            "coverage" => events with { ScopeCoverages = [] },
            "stale" => events with { ScopeCoverages = events.ScopeCoverages.SetItem(0,
                events.ScopeCoverages[0] with { ThroughUtc = AsOf.AddDays(-1), KnownAtUtc = AsOf.AddDays(-1) }) },
            "evaluation" => events with { Evaluation = events.Evaluation with { Passed = false } },
            "classifier" => events with { Events = [Event() with { ClassifierVersion = "display-keyword-v1" }] },
            "ticker" => events with { Events = [Event(DrawdownEventScope.Company, DrawdownEventCategory.Company, "OTHER")] },
            "sector" => events with { Events = [Event(DrawdownEventScope.Sector, DrawdownEventCategory.Sector, "energy")] },
            _ => events with { Events = [Event(), Event() with { RevisionId = "conflicting-revision" }] }
        };
        var audit = Analyze(original with { Events = events }, new("gate", RejectAdverseEvents: true));
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal(reason, audit.Reason);
    }

    [Fact]
    public void LateAssessmentOfOldArticleCannotReplaceNewerAdverseRevision()
    {
        var context = Context();
        var older = Event(direction: CatalystDirection.Positive) with {
            ClassifiedAtUtc = AsOf.AddMinutes(-15), KnownAtUtc = AsOf.AddMinutes(-15) };
        var newer = Event() with { RevisionId = "revision-2", RevisionContentSha256 = new string('b', 64),
            UpdatedAtUtc = AsOf.AddHours(-1), ReceivedAtUtc = AsOf.AddMinutes(-50),
            ClassifiedAtUtc = AsOf.AddMinutes(-45), KnownAtUtc = AsOf.AddMinutes(-45) };
        var audit = Analyze(context with { Events = context.Events! with { Events = [newer, older] } },
            new("gate", RejectAdverseEvents: true));
        Assert.Equal("rejected", audit.Verdict);
        Assert.Equal("observed_adverse_global_event", audit.Reason);
    }

    [Fact]
    public void EqualSourceRevisionTimesWithDifferentContentsRemainAmbiguous()
    {
        var context = Context();
        var older = Event(direction: CatalystDirection.Positive);
        var conflicting = older with { RevisionId = "conflicting", RevisionContentSha256 = new string('b', 64),
            ClassifiedAtUtc = AsOf.AddMinutes(-15), KnownAtUtc = AsOf.AddMinutes(-15) };
        var audit = Analyze(context with { Events = context.Events! with { Events = [older, conflicting] } },
            new("gate", RejectAdverseEvents: true));
        Assert.Equal("unknown", audit.Verdict);
        Assert.Equal("event_revision_order_ambiguous", audit.Reason);
    }

    [Fact]
    public void OutOfWindowEventsDoNotRequireCurrentClassifierOrChangeContextHash()
    {
        var context = Context();
        var rules = new DrawdownQualificationRules("gate", RejectAdverseEvents: true);
        var first = Analyze(context, rules);
        var old = Event() with { PublishedAtUtc = AsOf.AddDays(-31), ReceivedAtUtc = AsOf.AddDays(-30),
            ClassifiedAtUtc = AsOf.AddDays(-30), KnownAtUtc = AsOf.AddDays(-30),
            ClassifierVersion = "previous-evaluated-v1" };
        var second = Analyze(context with { Events = context.Events! with { Events = [old] } }, rules);
        Assert.Equal("qualifies", second.Verdict);
        Assert.Equal(EvidenceCanonicalJson.ComputeSha256(first), EvidenceCanonicalJson.ComputeSha256(second));
    }

    [Fact]
    public void FutureBarsAndRevisionsCannotChangeContextOrHash()
    {
        var context = Context();
        context = context with { Events = context.Events! with { Events = [Event(direction: CatalystDirection.Positive)] } };
        var first = Analyze(context, new("gate", RejectAdverseEvents: true));
        var futureBar = Bars()[^1] with { Timestamp = AsOf.AddDays(1), Ticker = "XLK", Close = 105 };
        var appended = context with {
            Sector = context.Sector! with { SectorBenchmark = context.Sector.SectorBenchmark with {
                Bars = context.Sector.SectorBenchmark.Bars.Add(futureBar) } },
            Events = context.Events! with { Events = context.Events.Events.Add(Event() with {
                RevisionId = "future", KnownAtUtc = AsOf.AddSeconds(1), ClassifiedAtUtc = AsOf.AddSeconds(1) }) } };
        var second = Analyze(appended, new("gate", RejectAdverseEvents: true));
        Assert.Equal(EvidenceCanonicalJson.ComputeSha256(first), EvidenceCanonicalJson.ComputeSha256(second));
        Assert.Equal("qualifies", first.Verdict);
    }

    [Fact]
    public void NewRuleFieldsAreOptionalAndOnlyDeclaredEventPolicyFilters()
    {
        Assert.DoesNotContain("RejectAdverseEvents", JsonSerializer.Serialize(new DrawdownQualificationRules()));
        Assert.DoesNotContain("EventLookbackDays", JsonSerializer.Serialize(new DrawdownQualificationRules()));
        Assert.Throws<InvalidOperationException>(() => new DrawdownQualificationRules("gate", RejectAdverseEvents: true,
            EventLookbackDays: 0).Validate());
        Assert.Throws<InvalidOperationException>(() => new DrawdownQualificationRules("gate", RejectAdverseEvents: true,
            EventMaxCoverageAgeHours: 169).Validate());
    }

    [Fact]
    public void DefaultSemanticHashIgnoresOptionalContextAndActiveGateBindsItsConsumedPrefix()
    {
        var baseline = StrategyDecisionKernelTests.CreateRequest();
        var context = Context();
        var kernel = new StrategyDecisionKernel();
        Assert.Equal(kernel.Evaluate(baseline).SemanticDecisionSha256,
            kernel.Evaluate(baseline with { DrawdownContext = context }).SemanticDecisionSha256);
        var strategy = baseline.Strategy with { EntryRules = baseline.Strategy.EntryRules with {
            DrawdownQualification = new("gate", RequireEventContext: true) } };
        var hash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(baseline.StrategyIdentity.StrategyId,
            baseline.StrategyIdentity.SemanticVersion, strategy, baseline.AdmissionProfile));
        var request = baseline with { Strategy = strategy,
            StrategyIdentity = new(baseline.StrategyIdentity.StrategyId, baseline.StrategyIdentity.SemanticVersion, hash),
            Candidate = baseline.Candidate with { Identity = baseline.Candidate.Identity with { StrategyContentSha256 = hash } },
            DrawdownContext = context };
        var first = kernel.Evaluate(request);
        var changed = context with { Events = context.Events! with { Evaluation = context.Events.Evaluation with {
            EvaluationSha256 = new string('b', 64) } } };
        Assert.NotEqual(first.SemanticDecisionSha256,
            kernel.Evaluate(request with { DrawdownContext = changed }).SemanticDecisionSha256);
        var future = context with { Events = context.Events! with { Events = [Event() with {
            KnownAtUtc = request.AsOfUtc.AddSeconds(1), ClassifiedAtUtc = request.AsOfUtc.AddSeconds(1) }] } };
        Assert.Equal(first.SemanticDecisionSha256,
            kernel.Evaluate(request with { DrawdownContext = future }).SemanticDecisionSha256);
    }
}
