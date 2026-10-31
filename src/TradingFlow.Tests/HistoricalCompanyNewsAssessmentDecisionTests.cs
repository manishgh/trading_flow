using System.Collections.Immutable;
using TradingFlow.Backtesting;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class HistoricalCompanyNewsAssessmentDecisionTests
{
    private static readonly CompanyNewsPinnedModel Model = new("finbert", "ProsusAI/finbert",
        new string('1', 40), new string('2', 64), "Training corpus/cutoff unproved; retrospective research only", null);

    // Empty, neutral and confirmed irrelevant stories are complete; positive-required still needs supported company direction.
    [Theory]
    [InlineData("empty")]
    [InlineData("neutral")]
    [InlineData("irrelevant")]
    public void CompleteNonPositiveEvidence_PassesCoverageAndVetoButFailsPositiveRequirement(string scenario)
    {
        var request = Request();
        if (scenario != "empty") request = WithStories(request, Story(request, scenario, "one"));
        var technical = new StrategyDecisionKernel().Evaluate(StrategyDecisionKernelTests.CreateRequest());
        var coverage = new StrategyDecisionKernel().Evaluate(request);
        var veto = new StrategyDecisionKernel().Evaluate(WithTreatment(request, HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto));
        var positive = new StrategyDecisionKernel().Evaluate(WithTreatment(request, HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired));
        Assert.True(coverage.IsTriggered, coverage.NoEntryReason);
        Assert.True(veto.IsTriggered, veto.NoEntryReason);
        Assert.Equal(technical.OrderPlan, coverage.OrderPlan);
        Assert.Equal(coverage.OrderPlan, veto.OrderPlan);
        Assert.Equal("historical_positive_company_news_missing", positive.NoEntryReason);
    }

    // A supported adverse story wins regardless of story order or the number of supported positives.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownMixedStories_AreCompleteButNegativeCannotBeCancelled(bool reverse)
    {
        var request = Request();
        var stories = new[] { Story(request, "positive", "positive"), Story(request, "negative", "negative") };
        request = WithStories(request, reverse ? stories.Reverse().ToArray() : stories);
        Assert.True(new StrategyDecisionKernel().Evaluate(request).IsTriggered);
        foreach (var treatment in new[] { HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto, HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired })
        {
            var result = new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment));
            Assert.Equal("historical_negative_company_news_veto", result.NoEntryReason);
            Assert.DoesNotContain(result.Rules, rule => rule.Code == "historical_positive_company_news_required");
        }
    }

    // Missing evidence and uncertain assessments share the identical admission mask before direction in all three arms.
    [Theory]
    [InlineData("missing_context")]
    [InlineData("missing_policy")]
    [InlineData("missing_assessment")]
    [InlineData("uncertain_direction")]
    [InlineData("failed_model")]
    [InlineData("fallback_model")]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_revision")]
    [InlineData("edited_label")]
    [InlineData("assessment_before_receipt")]
    [InlineData("duplicate_article")]
    public void IncompleteAssessment_RejectsAtOriginalTriggerInEveryNewsArm(string defect)
    {
        var request = Request();
        var revision = Story(request, "positive", "one");
        var assessment = revision.Assessment!;
        request = WithStories(request, revision);
        request = defect switch {
            "missing_context" => request with { HistoricalNewsPresence = null },
            "missing_policy" => request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { AssessmentPolicy = null } },
            "missing_assessment" => WithStories(request, revision with { Assessment = null }),
            "uncertain_direction" => WithStories(request, Story(request, "unknown", "one")),
            "failed_model" => WithStories(request, revision with { Assessment = assessment with {
                ModelEvidence = assessment.ModelEvidence! with { Succeeded = false, FailureReason = "inference_failure" } } }),
            "fallback_model" => WithStories(request, revision with { Assessment = assessment with {
                ModelEvidence = assessment.ModelEvidence! with { Identity = Model with { Model = "VADER" } } } }),
            "wrong_issuer" => WithStories(request, revision with { Assessment = assessment with {
                Extraction = assessment.Extraction with { IssuerSymbol = "OTHER" } } }),
            "wrong_revision" => WithStories(request, revision with { RevisionId = "another-revision" }),
            "edited_label" => WithStories(request, revision with { Assessment = assessment with { State = CompanyNewsAssessmentState.Negative } }),
            "assessment_before_receipt" => WithStories(request, revision with { ActualReceivedAtUtc = assessment.AssessedAtUtc.AddTicks(1) }),
            _ => WithStories(request, revision, revision)
        };
        foreach (var treatment in Treatments())
        {
            var result = new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment));
            Assert.Equal(StrategyCandidateState.Rejected, result.State);
            Assert.Equal("historical_company_news_assessment_unknown", result.NoEntryReason);
            Assert.Single(result.Rules, rule => rule.Code.StartsWith("historical_", StringComparison.Ordinal));
        }
    }

    // Retrospective assessment and receipt clocks stay in October while availability stays on its distinct historical assumption.
    [Fact]
    public void ActualOctoberClassification_IsNotRelabeledAsHistoricalReceiptOrStrictCatalyst()
    {
        var request = Request();
        var revision = Story(request, "positive", "one");
        request = WithTreatment(WithStories(request, revision), HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired);
        var result = new StrategyDecisionKernel().Evaluate(request);
        Assert.True(result.IsTriggered, result.NoEntryReason);
        Assert.True(revision.Assessment!.AssessedAtUtc > request.AsOfUtc);
        Assert.True(revision.ActualReceivedAtUtc > request.AsOfUtc);
        Assert.True(revision.AssumedAvailableAtUtc < request.AsOfUtc);
        Assert.Empty(request.Catalysts);
        foreach (var mode in new[] { StrategySelectionMode.RunPaperExperiment, StrategySelectionMode.RunPaperShadow,
            StrategySelectionMode.RunLive, StrategySelectionMode.DiagnosticReplay })
            Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(request with { SelectionMode = mode }));
    }

    // A failed original opportunity remains terminal when later evidence becomes complete; later stories cannot postpone entry.
    [Fact]
    public async Task UnknownOriginalTrigger_IsNotReenteredWhenAssessmentArrivesLater()
    {
        var request = Request();
        using var journal = new BacktestCandidateJournal();
        var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
        var run = BacktestCandidateJournal.CreateRun(new { Test = "company-news-original-trigger" }, "company-news-original-trigger", request.AsOfUtc);
        var rejected = await orchestrator.EvaluateAsync(run, request with { HistoricalNewsPresence = null });
        var later = request.AsOfUtc.AddMinutes(1);
        var replay = await orchestrator.EvaluateAsync(run, request with { AsOfUtc = later,
            HistoricalNewsPresence = request.HistoricalNewsPresence! with { DecisionAtUtc = later } });
        Assert.Equal(StrategyCandidateState.Rejected, rejected.Decision!.State);
        Assert.True(replay.WasAlreadyTerminal);
        Assert.Null(replay.Decision);
    }

    // Invalid model provenance cannot pass an empty-news package, and original publication age cannot be reset by assessment.
    [Theory]
    [InlineData("fallback_policy")]
    [InlineData("missing_training_provenance")]
    [InlineData("unlabeled_retrospective_model")]
    [InlineData("stale_original_publication")]
    public void FrozenPolicyAndPublicationAge_CannotBeWeakened(string defect)
    {
        var request = Request();
        var policy = request.HistoricalNewsPresence!.AssessmentPolicy!;
        request = defect switch {
            "fallback_policy" => request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { AssessmentPolicy = policy with { ModelIdentity = "VADER" } } },
            "missing_training_provenance" => request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { AssessmentPolicy = policy with { TrainingDataProvenance = "" } } },
            "unlabeled_retrospective_model" => request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { AssessmentPolicy = policy with { HistoricalUseLimitation = "historically_observed" } } },
            _ => WithStories(request, Story(request, "positive", "one") with { PublishedAtUtc = request.AsOfUtc.AddHours(-120).AddTicks(-1) })
        };
        Assert.All(Treatments(), treatment => Assert.Equal("historical_company_news_assessment_unknown",
            new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment)).NoEntryReason));
    }

    // A technical failure remains the primary rejection and does not request direction-dependent evidence.
    [Fact]
    public void AssessmentCannotRescueATechnicalFailure()
    {
        var request = Request();
        request = StrategyDecisionKernelTests.RebindStrategy(request, request.Strategy with {
            EntryRules = request.Strategy.EntryRules with { MinVolumeSpike = 99m } });
        var result = new StrategyDecisionKernel().Evaluate(request with { HistoricalNewsPresence = null });
        Assert.Equal(StrategyCandidateState.Rejected, result.State);
        Assert.DoesNotContain(result.Rules, rule => rule.Code.StartsWith("historical_company_", StringComparison.Ordinal));
    }

    // The versioned name package accepts either its exact mapped rows or unchanged literal fallbacks using the same order plan.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MappedPackage_AcceptsVerifiedNameOrOriginalLiteralFallback(bool mapped)
    {
        var request = WithMappedPolicy(Request());
        var story = mapped ? NamedStory(request, true) : Story(request, "positive", "literal-fallback");
        var assessment = story.Assessment!;
        request = WithStories(request, story);
        Assert.Equal(CompanyNewsAssessmentState.Positive, assessment.State);
        Assert.Equal(mapped, assessment.Extraction.IssuerMapping is not null);
        Assert.Null(HistoricalCompanyNewsAssessmentEvaluation.AssessmentUnknownReason(request));
        var originalOrder = new StrategyDecisionKernel().Evaluate(StrategyDecisionKernelTests.CreateRequest()).OrderPlan;
        foreach (var treatment in Treatments())
        {
            var result = new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment));
            Assert.True(result.IsTriggered, result.NoEntryReason);
            Assert.Equal(originalOrder, result.OrderPlan);
        }
    }

    // Missing or conflicting source mappings stay on the literal method; a company-name-only story cannot acquire ticker certainty.
    [Fact]
    public void MappedPackage_NameWithoutVerifiedMappingRemainsUnknownInEveryArm()
    {
        var request = WithMappedPolicy(Request());
        var story = NamedStory(request, false);
        var assessment = story.Assessment!;
        request = WithStories(request, story);
        Assert.Equal(CompanyNewsAssessmentClassifier.ExtractionVersion, assessment.ExtractionVersion);
        Assert.Equal(CompanyNewsAssessmentClassifier.MappingVersion, assessment.MappingVersion);
        Assert.Null(assessment.Extraction.IssuerMapping);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, assessment.State);
        Assert.Equal("company_news_relevance_direction_or_model_uncertain",
            HistoricalCompanyNewsAssessmentEvaluation.AssessmentUnknownReason(request));
        Assert.All(Treatments(), treatment => Assert.Equal("historical_company_news_assessment_unknown",
            new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment)).NoEntryReason));
    }

    // Exact version pairs and their mapping presence are one shared package rule; a v1 package cannot consume later mapped semantics.
    [Theory]
    [InlineData("old_package_new_row")]
    [InlineData("literal_row_mapped_version")]
    [InlineData("mapped_row_literal_version")]
    [InlineData("mapped_row_missing_mapping")]
    [InlineData("literal_row_unexpected_mapping")]
    public void PackageAndRowMethodMismatch_RejectsBeforeDirection(string defect)
    {
        var request = WithMappedPolicy(Request());
        var literal = Story(request, "positive", "literal-fallback");
        var mapped = NamedStory(request, true);
        var literalAssessment = literal.Assessment!;
        var mappedAssessment = mapped.Assessment!;
        var story = defect switch {
            "literal_row_mapped_version" => literal with { Assessment = literalAssessment with {
                Extraction = literalAssessment.Extraction with { MappingVersion = CompanyNewsAssessmentClassifier.MappedMappingVersion } } },
            "mapped_row_literal_version" => mapped with { Assessment = mappedAssessment with {
                Extraction = mappedAssessment.Extraction with { MappingVersion = CompanyNewsAssessmentClassifier.MappingVersion } } },
            "mapped_row_missing_mapping" => mapped with { Assessment = mappedAssessment with {
                Extraction = mappedAssessment.Extraction with { IssuerMapping = null } } },
            "literal_row_unexpected_mapping" => literal with { Assessment = literalAssessment with {
                Extraction = literalAssessment.Extraction with { IssuerMapping = mappedAssessment.Extraction.IssuerMapping } } },
            _ => mapped
        };
        if (defect == "old_package_new_row") request = Request();
        request = WithStories(request, story);
        Assert.Equal("company_news_assessment_method_mismatch",
            HistoricalCompanyNewsAssessmentEvaluation.AssessmentUnknownReason(request));
        Assert.All(Treatments(), treatment => Assert.Equal("historical_company_news_assessment_unknown",
            new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment)).NoEntryReason));
    }

    // Package provenance itself must declare one exact supported pair, including when its query contains no news.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedPackageVersionPair_IsUnknownEvenWithoutStories(bool mappedMapping)
    {
        var request = Request();
        var context = request.HistoricalNewsPresence!;
        request = request with { HistoricalNewsPresence = context with { AssessmentPolicy = context.AssessmentPolicy! with {
            MappingVersion = mappedMapping ? CompanyNewsAssessmentClassifier.MappedMappingVersion : CompanyNewsAssessmentClassifier.MappingVersion,
            ExtractionVersion = mappedMapping ? CompanyNewsAssessmentClassifier.ExtractionVersion : CompanyNewsAssessmentClassifier.MappedExtractionVersion } } };
        Assert.Equal("company_news_assessment_policy_incomplete",
            HistoricalCompanyNewsAssessmentEvaluation.AssessmentUnknownReason(request));
        Assert.All(Treatments(), treatment => Assert.Equal("historical_company_news_assessment_unknown",
            new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment)).NoEntryReason));
    }

    // Introducing a supported mapped positive cannot cancel a literal negative retained under the same v2 package.
    [Fact]
    public void MappedPositiveAndLiteralNegative_PreserveCommonCoverageAndNegativeVeto()
    {
        var request = WithMappedPolicy(Request());
        request = WithStories(request, NamedStory(request, true), Story(request, "negative", "literal-negative"));
        Assert.True(new StrategyDecisionKernel().Evaluate(request).IsTriggered);
        foreach (var treatment in new[] { HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto, HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired })
            Assert.Equal("historical_negative_company_news_veto",
                new StrategyDecisionKernel().Evaluate(WithTreatment(request, treatment)).NoEntryReason);
    }

    // Freeze one Research fixture using the same request and candidate identity consumed by ordinary backtests.
    private static StrategyDecisionRequest Request()
    {
        var original = StrategyDecisionKernelTests.CreateRequest();
        var request = original with { AdmissionProfile = original.AdmissionProfile with {
            HistoricalNewsDiagnosticPolicyId = HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId }, SelectionMode = StrategySelectionMode.Backtest };
        request = WithTreatment(request, HistoricalCompanyNewsDiagnosticPolicy.CoverageOnly);
        var policy = new HistoricalCompanyNewsAssessmentPolicy(HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId,
            Model.Model, Model.ArtifactSha256, Model.TrainingDataProvenance, CompanyNewsAssessmentClassifier.MappingVersion,
            CompanyNewsAssessmentClassifier.ExtractionVersion, EconomicNewsRuleClassifier.Version,
            HistoricalCompanyNewsDiagnosticPolicy.AssessmentMethodVersion, HistoricalCompanyNewsDiagnosticPolicy.HistoricalUseLimitation,
            Model.Provider, Model.Version, Model.TrainingDataCutoffUtc);
        return request with { HistoricalNewsPresence = new(request.Candidate.Identity.Symbol, request.AsOfUtc,
            HistoricalNewsDiagnosticPolicy.PolicyId, HistoricalNewsDiagnosticPolicy.PolicySha256, new string('a', 64),
            true, null, ImmutableArray<HistoricalNewsPresenceRevision>.Empty, policy) };
    }

    // Bind an exact revision and supported literal issuer assertion; actual classification always occurs in October.
    private static HistoricalNewsPresenceRevision Story(StrategyDecisionRequest request, string direction, string article)
    {
        var received = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        var headline = direction switch {
            "positive" => "RGTI raises full-year guidance",
            "negative" => "RGTI cuts full-year guidance",
            "neutral" => "RGTI reaffirms full-year guidance",
            "irrelevant" => "Federal Reserve raises interest rates",
            _ => "RGTI announces strategic plans"
        };
        var extraction = CompanyNewsAssessmentClassifier.Extract(new("alpaca", article, "revision-1", headline),
            request.Candidate.Identity.Symbol, received.AddSeconds(1));
        var polarity = direction switch { "positive" => CompanyNewsPolarity.Positive, "negative" => CompanyNewsPolarity.Negative,
            _ => CompanyNewsPolarity.Neutral };
        var model = new CompanyNewsModelEvidence(Model, extraction.ModelInputSha256, polarity, .9m, received.AddSeconds(2));
        var assessment = CompanyNewsAssessmentClassifier.Complete(extraction, model, Model, received.AddSeconds(3));
        return new("alpaca", article, "revision-1", new string('b', 64), request.AsOfUtc.AddHours(-120),
            request.AsOfUtc.AddHours(-80), received, request.AsOfUtc.AddHours(-48),
            [request.Candidate.Identity.Symbol], [new string('c', 64)], assessment);
    }

    // Freeze only the approved package pair; model, retrospective clocks, admission and all trading rules are unchanged.
    private static StrategyDecisionRequest WithMappedPolicy(StrategyDecisionRequest request)
    {
        var context = request.HistoricalNewsPresence!;
        return request with { HistoricalNewsPresence = context with { AssessmentPolicy = context.AssessmentPolicy! with {
            MappingVersion = CompanyNewsAssessmentClassifier.MappedMappingVersion,
            ExtractionVersion = CompanyNewsAssessmentClassifier.MappedExtractionVersion } } };
    }

    // Bind a deterministic official-name source while retaining original text and real October assessment clocks.
    private static HistoricalNewsPresenceRevision NamedStory(StrategyDecisionRequest request, bool mapped)
    {
        var story = Story(request, "positive", "named-company");
        var at = story.ActualReceivedAtUtc;
        var input = story.Assessment!.Revision with { Headline = "Rigetti Computing raises full-year guidance" };
        var mapping = new CompanyNewsIssuerMapping(request.Candidate.Identity.Symbol, "Rigetti Computing Inc.", "finviz",
            "synthetic-earnings-receipt:items:0", new string('d', 64), at.AddDays(-1));
        var extraction = mapped
            ? CompanyNewsAssessmentClassifier.Extract(input, request.Candidate.Identity.Symbol, at.AddSeconds(1), mapping)
            : CompanyNewsAssessmentClassifier.Extract(input, request.Candidate.Identity.Symbol, at.AddSeconds(1));
        var model = new CompanyNewsModelEvidence(Model, extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, at.AddSeconds(2));
        return story with { Assessment = CompanyNewsAssessmentClassifier.Complete(extraction, model, Model, at.AddSeconds(3)) };
    }

    // Replace the selected story set without changing any query, policy, trigger or actual clock.
    private static StrategyDecisionRequest WithStories(StrategyDecisionRequest request, params HistoricalNewsPresenceRevision[] stories) =>
        request with { HistoricalNewsPresence = request.HistoricalNewsPresence! with { EligibleRevisions = stories.ToImmutableArray() } };

    // Rehash every declared treatment through the canonical artifact fixture rather than changing admitted rules in place.
    private static StrategyDecisionRequest WithTreatment(StrategyDecisionRequest request, string treatment) =>
        StrategyDecisionKernelTests.RebindStrategy(request, request.Strategy with {
            EntryRules = request.Strategy.EntryRules with { HistoricalNewsDiagnostic = treatment } });

    // Return the frozen three news arms for identical completeness-mask assertions.
    private static string[] Treatments() => [HistoricalCompanyNewsDiagnosticPolicy.CoverageOnly,
        HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto, HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired];
}
