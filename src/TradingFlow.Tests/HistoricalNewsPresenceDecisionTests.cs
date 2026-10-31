using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class HistoricalNewsPresenceDecisionTests
{
    // Null optional diagnostic fields must not change previously published technical strategy identities.
    [Theory]
    [InlineData("swing.connors-rsi2", "6.0.0", "f56ef8e2505d7e6118bfb7c534c5c71ad03936da4cbf13b5e13e5566e6876df0")]
    [InlineData("swing.minervini-vcp", "10.0.0", "1e19dbbcb6671980733bd78edf9f0a93e88f90ee47bf375424f4b11a8e7c8d0b")]
    [InlineData("technical.recovery-sma5-exit", "2.0.0", "ad27d9ed0855760bf17fb4fec8ed9b97938dc3a1f52710097b6c63dc4a64c4a8")]
    public void OriginalTechnicalArtifacts_RetainTheirPublishedCanonicalIdentities(string id, string version, string hash)
    {
        var root = TestRepository.FindRoot();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), new SimpleYamlReader());
        var artifact = catalog.RequireExact(new StrategyArtifactIdentity(id, version, hash));
        Assert.Null(artifact.ResolvedStrategy.EntryRules.HistoricalNewsDiagnostic);
        Assert.Null(artifact.AdmissionProfile.HistoricalNewsDiagnosticPolicyId);
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(new CanonicalStrategyDocument(id, version,
            artifact.ResolvedStrategy, artifact.AdmissionProfile));
        Assert.DoesNotContain("historicalNewsDiagnostic", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    // The control and veto arms must withhold exactly the same original trigger when evidence is unknown.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteEvidence_RejectsBothArmsAfterTheTechnicalTrigger(bool missingContext)
    {
        var control = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        control = control with { HistoricalNewsPresence = missingContext ? null : control.HistoricalNewsPresence! with {
            QueryEvidenceComplete = false, UnknownReason = "cursor_not_exhausted" } };
        var veto = WithTreatment(control, HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews);

        var first = new StrategyDecisionKernel().Evaluate(control);
        var second = new StrategyDecisionKernel().Evaluate(veto);

        Assert.Equal(StrategyCandidateState.Rejected, first.State);
        Assert.Equal(first.State, second.State);
        Assert.Equal("historical_news_evidence_unknown", first.NoEntryReason);
        Assert.Equal(first.NoEntryReason, second.NoEntryReason);
        Assert.Contains(first.Rules, rule => rule.Code == "execution_trigger_satisfied" && rule.Passed);
        Assert.Equal(first.Rules.Single(rule => rule.Code == "historical_news_evidence_checked").EvidenceJson,
            second.Rules.Single(rule => rule.Code == "historical_news_evidence_checked").EvidenceJson);
        Assert.DoesNotContain(second.Rules, rule => rule.Code == "historical_news_presence_checked");
        Assert.Null(second.OrderPlan);
    }

    // A valid future actual receipt stays real while only the separately declared assumed clock permits this diagnostic.
    [Fact]
    public void PresenceVeto_UsesSelectedPublicationEvidenceAndPreservesActualReceipt()
    {
        var control = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        var revision = Revision(control);
        control = control with { HistoricalNewsPresence = control.HistoricalNewsPresence! with { EligibleRevisions = [revision] } };
        var admitted = new StrategyDecisionKernel().Evaluate(control);
        var rejected = new StrategyDecisionKernel().Evaluate(WithTreatment(control, HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews));

        Assert.True(admitted.IsTriggered);
        Assert.Equal("historical_recent_company_news_veto", rejected.NoEntryReason);
        Assert.Equal(StrategyCandidateState.Rejected, rejected.State);
        Assert.True(revision.ActualReceivedAtUtc > control.AsOfUtc);
        Assert.Empty(control.Catalysts);
        using var audit = JsonDocument.Parse(rejected.CanonicalDecisionJson);
        var consumed = audit.RootElement.GetProperty("semantic").GetProperty("historicalNewsPresence");
        Assert.Equal(revision.ActualReceivedAtUtc, consumed.GetProperty("eligibleRevisions")[0].GetProperty("actualReceivedAtUtc").GetDateTimeOffset());
        Assert.Equal(control.AsOfUtc, consumed.GetProperty("decisionAtUtc").GetDateTimeOffset());
        Assert.Equal(control.HistoricalNewsPresence.PolicySha256, consumed.GetProperty("policySha256").GetString());
    }

    // Exact five-day boundary remains eligible; publication age is not reset by later update time.
    [Theory]
    [InlineData("old_publication")]
    [InlineData("future_assumption")]
    [InlineData("duplicate_identity")]
    [InlineData("wrong_symbol")]
    [InlineData("wrong_decision")]
    public void InvalidSelectedEvidence_CannotBecomeAnEmptyNewsClaim(string defect)
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        var revision = Revision(request);
        var context = request.HistoricalNewsPresence! with { EligibleRevisions = [revision] };
        context = defect switch {
            "old_publication" => context with { EligibleRevisions = [revision with { PublishedAtUtc = request.AsOfUtc.AddHours(-120).AddTicks(-1) }] },
            "future_assumption" => context with { EligibleRevisions = [revision with { AssumedAvailableAtUtc = request.AsOfUtc.AddTicks(1) }] },
            "duplicate_identity" => context with { EligibleRevisions = [revision, revision] },
            "wrong_symbol" => context with { EligibleRevisions = [revision with { Symbols = ["OTHER"] }] },
            _ => context with { DecisionAtUtc = request.AsOfUtc.AddSeconds(-1) }
        };
        request = request with { HistoricalNewsPresence = context };
        var control = new StrategyDecisionKernel().Evaluate(request);
        var veto = new StrategyDecisionKernel().Evaluate(WithTreatment(request, HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews));
        Assert.Equal("historical_news_evidence_unknown", control.NoEntryReason);
        Assert.Equal(control.NoEntryReason, veto.NoEntryReason);
    }

    // A complete query with no selected fresh article permits the same order plan in both diagnostic arms.
    [Fact]
    public void CompleteEmptyEvidence_PreservesTheSharedTechnicalOrderPlan()
    {
        var technical = new StrategyDecisionKernel().Evaluate(StrategyDecisionKernelTests.CreateRequest());
        var control = new StrategyDecisionKernel().Evaluate(Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked));
        var veto = new StrategyDecisionKernel().Evaluate(Request(HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews));
        Assert.True(control.IsTriggered);
        Assert.True(veto.IsTriggered);
        Assert.Equal(technical.OrderPlan, control.OrderPlan);
        Assert.Equal(control.OrderPlan, veto.OrderPlan);
    }

    // Missing optional evidence must not obscure a technical rejection or manufacture a later opportunity.
    [Fact]
    public void DiagnosticQualifier_IsNotAppliedBeforeTheTechnicalEntryPasses()
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews);
        request = WithRules(request, request.Strategy.EntryRules with { MinVolumeSpike = 99m });
        var result = new StrategyDecisionKernel().Evaluate(request with { HistoricalNewsPresence = null });
        Assert.Equal(StrategyCandidateState.Rejected, result.State);
        Assert.DoesNotContain(result.Rules, rule => rule.Code.StartsWith("historical_news_", StringComparison.Ordinal));
        Assert.NotEqual("historical_news_evidence_unknown", result.NoEntryReason);
    }

    // Policy/profile/transport mismatches must fail before diagnostic evidence can influence any strategy decision.
    [Theory]
    [InlineData(null)]
    [InlineData(StrategySelectionMode.RunPaperExperiment)]
    [InlineData(StrategySelectionMode.RunPaperShadow)]
    [InlineData(StrategySelectionMode.RunLive)]
    [InlineData(StrategySelectionMode.DiagnosticReplay)]
    public void DiagnosticEvidence_RejectsEveryUnboundOrNonBacktestMode(StrategySelectionMode? mode)
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked) with { SelectionMode = mode };
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(request));
    }

    // A presence snapshot may never be attached to a strict/default request or silently change the frozen assumption.
    [Fact]
    public void DiagnosticEvidence_RejectsStrictRequestsAndChangedPolicy()
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        var strict = StrategyDecisionKernelTests.CreateRequest() with { HistoricalNewsPresence = request.HistoricalNewsPresence };
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(strict));
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(request with {
            HistoricalNewsPresence = request.HistoricalNewsPresence! with { PolicySha256 = new string('9', 64) } }));
        var mixed = WithRules(request, request.Strategy.EntryRules with { RequirePositiveNews = true });
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(mixed));
        var actualCatalysts = StrategyDecisionKernelTests.CreateParityScenario("long_regular_catalyst").Catalysts;
        Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(request with { Catalysts = actualCatalysts }));
    }

    // Normal assembly carries the authorized selection mode and the separate diagnostic object without producing a catalyst.
    [Fact]
    public void Assembler_PreservesResearchBindingAndDoesNotRewriteAnyReceiptClock()
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        var context = request.HistoricalNewsPresence! with { EligibleRevisions = [Revision(request)] };
        var runtime = new AuthorizedRuntimeStrategy(request.StrategyIdentity, StrategySelectionMode.Backtest,
            request.Strategy, request.AdmissionProfile);
        var assembled = StrategyDecisionRequestAssembler.Create(runtime, request.Candidate.Identity.Symbol,
            request.BarsByTimeframe.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TradingFlow.Domain.Market.OhlcvBar>)pair.Value),
            request.SnapshotsByTimeframe.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TradingFlow.Domain.Market.IndicatorSnapshot>)pair.Value),
            [], request.AsOfUtc, request.Candidate.Discovery, request.Candidate.Universe, request.Candidate.Regime,
            request.Candidate.State, request.Candidate.Version, request.Candidate.Identity.SetupKey,
            request.Candidate.Identity.DiscoveryWindowStartUtc, request.Candidate.Identity.DiscoveryWindowEndUtc,
            2, Guid.NewGuid(), historicalNewsPresence: context);
        Assert.Equal(StrategySelectionMode.Backtest, assembled.SelectionMode);
        Assert.Same(context, assembled.HistoricalNewsPresence);
        Assert.Equal(context.EligibleRevisions[0].ActualReceivedAtUtc, assembled.HistoricalNewsPresence!.EligibleRevisions[0].ActualReceivedAtUtc);
        Assert.Empty(assembled.Catalysts);
    }

    // An earlier unknown rejection stays terminal even if a subsequent decision receives complete empty evidence.
    [Fact]
    public async Task UnknownOriginalTrigger_CannotBeRetriedAtALaterDecision()
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews);
        var journal = new BacktestCandidateJournal();
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
        var run = BacktestCandidateJournal.CreateRun(new { Test = "news-original-trigger" }, "news-original-trigger", request.AsOfUtc);
        var rejected = await orchestrator.EvaluateAsync(run, request with { HistoricalNewsPresence = null });
        var later = request.AsOfUtc.AddMinutes(1);
        var retried = await orchestrator.EvaluateAsync(run, request with { AsOfUtc = later,
            HistoricalNewsPresence = request.HistoricalNewsPresence! with { DecisionAtUtc = later } });
        Assert.Equal(StrategyCandidateState.Rejected, rejected.Decision!.State);
        Assert.True(retried.WasAlreadyTerminal);
        Assert.Null(retried.Decision);
    }

    // Consumed content and package hashes are economic decision inputs, including in the evidence-only control.
    [Fact]
    public void ExactConsumedRevisionAndManifest_AreBoundIntoSemanticDecision()
    {
        var request = Request(HistoricalNewsDiagnosticPolicy.EvidenceChecked);
        var revision = Revision(request);
        request = request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { EligibleRevisions = [revision] } };
        var first = new StrategyDecisionKernel().Evaluate(request);
        var second = new StrategyDecisionKernel().Evaluate(request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with {
            EligibleRevisions = [revision with { ContentSha256 = new string('7', 64) }] } });
        var third = new StrategyDecisionKernel().Evaluate(request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with {
            EvidenceSha256 = new string('8', 64) } });
        Assert.NotEqual(first.SemanticDecisionSha256, second.SemanticDecisionSha256);
        Assert.NotEqual(first.SemanticDecisionSha256, third.SemanticDecisionSha256);
    }

    // Build a fully bound Research fixture while leaving its technical signal and execution rules unchanged.
    private static StrategyDecisionRequest Request(string treatment)
    {
        var original = StrategyDecisionKernelTests.CreateRequest();
        var request = original with { AdmissionProfile = original.AdmissionProfile with {
            HistoricalNewsDiagnosticPolicyId = HistoricalNewsDiagnosticPolicy.AdmissionPolicyId }, SelectionMode = StrategySelectionMode.Backtest };
        request = WithTreatment(request, treatment);
        return request with { HistoricalNewsPresence = new(request.Candidate.Identity.Symbol, request.AsOfUtc,
            HistoricalNewsDiagnosticPolicy.PolicyId, HistoricalNewsDiagnosticPolicy.PolicySha256,
            new string('a', 64), true, null, ImmutableArray<HistoricalNewsPresenceRevision>.Empty) };
    }

    // Rehash the treatment as a declared immutable strategy rule instead of editing an identity after admission.
    private static StrategyDecisionRequest WithTreatment(StrategyDecisionRequest request, string treatment) =>
        WithRules(request, request.Strategy.EntryRules with { HistoricalNewsDiagnostic = treatment });

    // Reuse the established exact artifact fixture binding for every controlled rule/profile change.
    private static StrategyDecisionRequest WithRules(StrategyDecisionRequest request, EntryRules rules) =>
        StrategyDecisionKernelTests.RebindStrategy(request, request.Strategy with { EntryRules = rules });

    // The assumed next-session instant follows update+24h; actual receipt deliberately occurs months later.
    private static HistoricalNewsPresenceRevision Revision(StrategyDecisionRequest request) => new(
        "alpaca", "article-1", "revision-1", new string('b', 64), request.AsOfUtc.AddHours(-120),
        request.AsOfUtc.AddHours(-80), request.AsOfUtc.AddMonths(4), request.AsOfUtc.AddHours(-48),
        [request.Candidate.Identity.Symbol], [new string('c', 64), new string('d', 64)]);
}
