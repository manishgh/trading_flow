using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class CompanyNewsAssessmentClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 0, 0, TimeSpan.Zero);
    private static readonly CompanyNewsPinnedModel Pinned = new("finbert", "ProsusAI/finbert", "frozen-1",
        new string('a', 64), "training provenance explicitly unknown; diagnostic only", null);

    /// <summary>Builds a synthetic archived revision without provider calls or operational data.</summary>
    private static CompanyNewsRevisionInput Revision(string headline, string? summary = null, string? body = null) =>
        new("synthetic", "article-1", "revision-1", headline, summary, body);

    /// <summary>Returns deterministic model predictions without claiming measured model accuracy.</summary>
    private static CompanyNewsModelEvidence Model(CompanyNewsExtraction extraction, CompanyNewsPolarity polarity) =>
        new(Pinned, extraction.ModelInputSha256, polarity, .8m, Now.AddSeconds(1));

    /// <summary>Exercises the pure extraction and exact pinned-model agreement path.</summary>
    private static CompanyNewsAssessment Assess(string headline, CompanyNewsPolarity polarity,
        string? summary = null, string? body = null)
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision(headline, summary, body), "ABC", Now);
        return CompanyNewsAssessmentClassifier.Complete(extraction, Model(extraction, polarity), Pinned, Now.AddSeconds(2));
    }

    /// <summary>Only supported literal-subject event assertions can agree with a model's top label.</summary>
    [Theory]
    [InlineData("ABC raises full-year guidance", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("ABC raises guidance to $3.25 per share.", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("ABC raises guidance by 10.5%", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("ABC cuts profit forecast", CompanyNewsPolarity.Negative, CompanyNewsAssessmentState.Negative)]
    [InlineData("ABC earnings beat estimates", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("ABC earnings missed expectations", CompanyNewsPolarity.Negative, CompanyNewsAssessmentState.Negative)]
    [InlineData("ABC upgraded to buy", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("ABC downgraded to sell", CompanyNewsPolarity.Negative, CompanyNewsAssessmentState.Negative)]
    [InlineData("ABC reaffirms guidance", CompanyNewsPolarity.Neutral, CompanyNewsAssessmentState.Neutral)]
    public void SupportedAssertionsRequireMatchingTopPolarity(string headline, CompanyNewsPolarity polarity,
        CompanyNewsAssessmentState expected)
    {
        var result = Assess(headline, polarity);
        Assert.Equal(expected, result.State);
        Assert.Equal("ABC", result.IssuerSymbol);
        Assert.Equal(EconomicNewsScope.Company, result.Scope);
        Assert.Null(result.Materiality);
        Assert.True(result.ResearchOnly);
        Assert.False(result.AccuracyMeasured);
        Assert.False(result.TradingActivationAllowed);
    }

    /// <summary>Company names, provider association and unsupported meaningful events cannot invent issuer direction.</summary>
    [Theory]
    [InlineData("Acme raises guidance")]
    [InlineData("ABC reports Q3 earnings")]
    [InlineData("ABC announces public offering")]
    [InlineData("Broker upgrades ABC to buy")]
    [InlineData("ABC upgrades XYZ to buy")]
    [InlineData("XYZ cuts guidance; ABC shares rise")]
    [InlineData("ABC raises guidance while XYZ cuts outlook")]
    [InlineData("ABC raises guidance after Acme cuts spending")]
    [InlineData("ABC raises guidance. XYZ cuts spending.")]
    [InlineData("ABC raises guidance for XYZ")]
    [InlineData("ABC raises guidance, XYZ cuts spending")]
    [InlineData("ABC raises guidance: XYZ cuts spending")]
    [InlineData("ABC raises guidance — XYZ cuts spending")]
    [InlineData("ABC raises guidance / XYZ cuts spending")]
    [InlineData("ABC raises guidance (XYZ cuts spending)")]
    [InlineData("ABC raises guidance to XYZ")]
    [InlineData("ABC raises guidance to $3.25 per share. XYZ cuts spending.")]
    [InlineData("XYZ says ABC raises guidance")]
    [InlineData("ABC does not raise guidance")]
    [InlineData("ABC might raise guidance")]
    [InlineData("ABC raises guidance and cuts profit forecast")]
    [InlineData("<p>ABC raises guidance</p>")]
    public void UnsupportedNegatedOrMultiCompanyAssertionsRemainUnknown(string headline)
    {
        var result = Assess(headline, CompanyNewsPolarity.Positive);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        Assert.False(result.Extraction.CanAssess);
        Assert.NotEmpty(result.Reasons);
    }

    /// <summary>Broad display-rule matches cannot transfer a second issuer's earnings beat through any separator or prose.</summary>
    [Theory]
    [InlineData("ABC earnings unchanged; XYZ beats estimates")]
    [InlineData("ABC earnings unchanged, XYZ beats estimates")]
    [InlineData("ABC earnings unchanged: XYZ beats estimates")]
    [InlineData("ABC earnings unchanged — XYZ beats estimates")]
    [InlineData("ABC earnings unchanged / XYZ beats estimates")]
    [InlineData("ABC earnings unchanged (XYZ beats estimates)")]
    [InlineData("ABC earnings unchanged XYZ beats estimates")]
    [InlineData("ABC earnings unchanged and XYZ beats estimates")]
    [InlineData("ABC earnings unchanged. XYZ beats estimates")]
    [InlineData("ABC earnings for XYZ beat estimates")]
    [InlineData("ABC earnings beat XYZ estimates")]
    [InlineData("ABC earnings beat estimates for XYZ")]
    [InlineData("ABC earnings unchanged; XYZ missed estimates")]
    public void WildcardEconomicCuesCannotBecomeSubjectBoundAssertions(string text)
    {
        var headline = Assess(text, CompanyNewsPolarity.Positive);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, headline.State);
        Assert.False(headline.Extraction.CanAssess);
        Assert.Contains("headline_contains_unsupported_clause_or_subject_relation", headline.Reasons);
        var supporting = Assess("ABC earnings beat estimates", CompanyNewsPolarity.Positive, body: text);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, supporting.State);
        Assert.False(supporting.Extraction.CanAssess);
        Assert.Contains("supporting_field_contains_unsupported_clause_or_subject_relation", supporting.Reasons);
    }

    /// <summary>The entire context is retained, and contrary or unbound supporting assertions withhold the label.</summary>
    [Theory]
    [InlineData("ABC cuts guidance.")]
    [InlineData("ABC raises guidance. XYZ cuts outlook.")]
    [InlineData("It cuts guidance.")]
    [InlineData("ABC did not raise guidance.")]
    [InlineData("ABC raises guidance. It might reverse the decision.")]
    [InlineData("ABC raises guidance. XYZ cuts spending.")]
    [InlineData("ABC raises guidance for XYZ")]
    [InlineData("ABC raises guidance, XYZ cuts spending")]
    public void SupportingContextCannotBeRemovedToObtainPositiveLabel(string body)
    {
        var result = Assess("ABC raises guidance", CompanyNewsPolarity.Positive, body: body);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        var span = Assert.Single(result.Extraction.EvidenceSpans, item => item.Field == "body");
        Assert.Equal(body, span.Text);
        Assert.Equal(body, span.FullContext);
        Assert.Equal(0, span.Start);
        Assert.Equal(body.Length, span.Length);
        Assert.Contains(body, result.Extraction.ModelInputText);
    }

    /// <summary>Fields valid as summary/body but too long for headline-rule reuse stay intact and withhold assessment.</summary>
    [Theory]
    [InlineData("summary", "ABC raises guidance")]
    [InlineData("body", "ABC raises guidance")]
    [InlineData("summary", "Federal Reserve cuts interest rates")]
    [InlineData("body", "Federal Reserve cuts interest rates")]
    public void OverlongSupportingAssertionIsUnknownWithoutTruncation(string field, string headline)
    {
        var text = headline.PadRight(32_001);
        var revision = Revision(headline, field == "summary" ? text : null, field == "body" ? text : null);
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "ABC", Now);
        var expectedReason = $"supporting_{field}_exceeds_32000_character_assertion_limit";
        Assert.False(extraction.CanAssess);
        Assert.Contains(expectedReason, extraction.Reasons);
        Assert.Equal(revision, extraction.Revision);
        var span = Assert.Single(extraction.EvidenceSpans, item => item.Field == field);
        Assert.Equal(text, span.Text);
        Assert.Equal(text, span.FullContext);
        Assert.Equal(text.Length, span.Length);
        Assert.Contains(text, extraction.ModelInputText);
        Assert.Equal(CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(revision), extraction.RevisionContentSha256);
        var result = CompanyNewsAssessmentClassifier.Complete(extraction, null, Pinned, Now.AddSeconds(2));
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        Assert.Contains(expectedReason, result.Reasons);
    }

    /// <summary>The reused headline-rule limit is inclusive, independently of later model token-length checks.</summary>
    [Theory]
    [InlineData("summary")]
    [InlineData("body")]
    public void SupportingAssertionAtHeadlineRuleLimitDoesNotOverflow(string field)
    {
        var text = "ABC raises guidance".PadRight(32_000);
        var revision = Revision("ABC raises guidance", field == "summary" ? text : null, field == "body" ? text : null);
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "ABC", Now);
        Assert.True(extraction.CanAssess);
        Assert.DoesNotContain($"supporting_{field}_exceeds_32000_character_assertion_limit", extraction.Reasons);
    }

    /// <summary>Textual neutral or negative predictions cannot silently become positive economic assertions.</summary>
    [Theory]
    [InlineData(CompanyNewsPolarity.Negative)]
    [InlineData(CompanyNewsPolarity.Neutral)]
    public void ModelDisagreementIsExplicitlyUnknown(CompanyNewsPolarity polarity)
    {
        var result = Assess("ABC raises guidance", polarity);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        Assert.Contains("model_polarity_disagrees_with_supported_assertion", result.Reasons);
    }

    /// <summary>Missing inference, failure, fallback identity and different model text remain separate auditable failures.</summary>
    [Fact]
    public void MissingFailureFallbackAndHashMismatchNeverBecomeNeutral()
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision("ABC raises guidance"), "ABC", Now);
        var evidence = Model(extraction, CompanyNewsPolarity.Positive);
        var cases = new (CompanyNewsModelEvidence? Evidence, string Reason)[] {
            (null, "required_model_assessment_missing"),
            (evidence with { Succeeded = false, FailureReason = "service unavailable" }, "required_model_assessment_failed"),
            (evidence with { Identity = Pinned with { Provider = "vader" } }, "pinned_model_identity_mismatch_or_fallback"),
            (evidence with { InputContentSha256 = new string('b', 64) }, "model_input_hash_mismatch")
        };
        foreach (var item in cases)
        {
            var result = CompanyNewsAssessmentClassifier.Complete(extraction, item.Evidence, Pinned, Now.AddSeconds(2));
            Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
            Assert.Contains(item.Reason, result.Reasons);
        }
    }

    /// <summary>Macro context is visibly irrelevant for issuer direction while unresolved company identity stays unknown.</summary>
    [Fact]
    public void IrrelevantAndUnknownAreDistinct()
    {
        Assert.Equal(CompanyNewsAssessmentState.Irrelevant,
            Assess("Federal Reserve cuts interest rates", CompanyNewsPolarity.Negative).State);
        Assert.Equal(CompanyNewsAssessmentState.Unknown,
            Assess("Acme raises guidance", CompanyNewsPolarity.Positive).State);
    }

    /// <summary>Retains unsupported provider symbols as unknown rows so one foreign symbol cannot abort an archived pilot.</summary>
    [Theory]
    [InlineData("TSX:PMZ", "TSX:PMZ raises guidance")]
    [InlineData("TSX:PMZ/UN", "TSX:PMZ/UN raises guidance")]
    [InlineData("TSX:PMZ", "Federal Reserve cuts interest rates")]
    [InlineData("TSX:PMZ/UN", "Federal Reserve cuts interest rates")]
    public void UnsupportedProviderSymbolIsPreservedAndCompletedAsUnknown(string symbol, string headline)
    {
        var revision = Revision(headline);
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, symbol, Now);
        Assert.Equal(symbol, extraction.IssuerSymbol);
        Assert.Equal(revision, extraction.Revision);
        Assert.False(extraction.CanAssess);
        Assert.Contains("unsupported_symbol_format", extraction.Reasons);
        var result = CompanyNewsAssessmentClassifier.Complete(extraction, Model(extraction, CompanyNewsPolarity.Positive),
            Pinned, Now.AddSeconds(2));
        Assert.Equal(symbol, result.IssuerSymbol);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        Assert.Contains("unsupported_symbol_format", result.Reasons);
    }

    /// <summary>Rejects malformed caller identities while preserving distinct unsupported provider formats.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData(" ABC")]
    public void MalformedProviderSymbolStillThrows(string? symbol)
    {
        Assert.ThrowsAny<ArgumentException>(() => CompanyNewsAssessmentClassifier.Extract(
            Revision("ABC raises guidance"), symbol!, Now));
    }

    /// <summary>Revision identifiers, exact whitespace and raw retained headline independently affect content identity.</summary>
    [Fact]
    public void ImmutableIdentityRetainsExactOriginalFieldsAndDetectsTampering()
    {
        var revision = Revision("ABC raises guidance", "ABC raises full-year guidance.");
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "ABC", Now);
        Assert.Equal(revision, extraction.Revision);
        Assert.NotEqual(extraction.RevisionContentSha256,
            CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(revision with { RevisionId = "revision-2" }));
        Assert.NotEqual(extraction.RevisionContentSha256,
            CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(revision with { Headline = "ABC  raises guidance" }));
        Assert.Equal(CompanyNewsAssessmentClassifier.ComputeTextSha256(extraction.ModelInputText), extraction.ModelInputSha256);
        var modified = extraction with { ModelInputText = "ABC raises guidance", CanAssess = true };
        Assert.Throws<ArgumentException>(() => CompanyNewsAssessmentClassifier.Complete(modified,
            Model(modified, CompanyNewsPolarity.Positive), Pinned, Now.AddSeconds(2)));
    }

    /// <summary>Retrospective measurements retain actual classification clocks and cannot pretend inference finished earlier.</summary>
    [Fact]
    public void ActualUtcClocksAreRequired()
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision("ABC raises guidance"), "ABC", Now);
        var evidence = Model(extraction, CompanyNewsPolarity.Positive) with { CompletedAtUtc = Now.AddDays(1) };
        var result = CompanyNewsAssessmentClassifier.Complete(extraction, evidence, Pinned, Now.AddSeconds(2));
        Assert.Equal(CompanyNewsAssessmentState.Unknown, result.State);
        Assert.Contains("model_completion_clock_invalid", result.Reasons);
        Assert.Throws<ArgumentException>(() => CompanyNewsAssessmentClassifier.Extract(extraction.Revision, "ABC", default));
    }
}
