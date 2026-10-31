using System.Text.Json;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsMappedAssessmentClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 17, 0, 0, TimeSpan.Zero);
    private static readonly CompanyNewsPinnedModel Pinned = new("finbert", "ProsusAI/finbert", "frozen",
        new string('a', 64), "synthetic diagnostic fixture; accuracy not measured", null);

    /// <summary>Constructs source-bound synthetic mappings without looking up a live provider or a ticker roster.</summary>
    private static CompanyNewsIssuerMapping Mapping(string symbol = "DTM", string name = "DT Midstream Inc") =>
        new(symbol, name, "finviz", "fixture-observation:items:0", new string('b', 64), Now.AddHours(-1));

    /// <summary>Builds an exact synthetic revision retaining headline, optional summary and optional body.</summary>
    private static CompanyNewsRevisionInput Revision(string headline, string? summary = null, string? body = null) =>
        new("synthetic", "article", "revision", headline, summary, body);

    /// <summary>Completes the extracted input using deterministic pinned predictions rather than actual inference.</summary>
    private static CompanyNewsAssessment Complete(CompanyNewsExtraction extraction, CompanyNewsPolarity label) =>
        CompanyNewsAssessmentClassifier.Complete(extraction,
            new(Pinned, extraction.ModelInputSha256, label, .8m, Now.AddSeconds(1)), Pinned, Now.AddSeconds(2));

    /// <summary>Exact source names, legal trims and ordinary analyst-first headlines produce auditable canonical assertions.</summary>
    [Theory]
    [InlineData("DT Midstream Inc raises guidance", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("DT Midstream raises guidance", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("DT Midstream earnings missed estimates", CompanyNewsPolarity.Negative, CompanyNewsAssessmentState.Negative)]
    [InlineData("Barclays Upgrades DT Midstream to Outperform, Announces $145 Price Target", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    [InlineData("Wells Fargo Downgrades DT Midstream to Underperform, Announces $120.50 Price Target", CompanyNewsPolarity.Negative, CompanyNewsAssessmentState.Negative)]
    [InlineData("Morgan Stanley Upgrades DTM to Overweight", CompanyNewsPolarity.Positive, CompanyNewsAssessmentState.Positive)]
    public void MappedSubjectsUseExplicitCanonicalRuleInputsAndRetainOriginals(string headline, CompanyNewsPolarity label,
        CompanyNewsAssessmentState expected)
    {
        var revision = Revision(headline);
        var mapping = Mapping();
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "DTM", Now, mapping);
        Assert.True(extraction.CanAssess);
        Assert.Equal(CompanyNewsAssessmentClassifier.MappedExtractionVersion, extraction.ExtractionVersion);
        Assert.Equal(CompanyNewsAssessmentClassifier.MappedMappingVersion, extraction.MappingVersion);
        Assert.Equal(mapping, extraction.IssuerMapping);
        Assert.Equal(revision, extraction.Revision);
        Assert.Equal(headline, extraction.ModelInputText);
        Assert.Equal(CompanyNewsAssessmentClassifier.ComputeTextSha256(headline), extraction.ModelInputSha256);
        Assert.NotNull(extraction.RuleInputBinding);
        Assert.StartsWith("DTM ", extraction.RuleInputBinding.Headline);
        var span = Assert.Single(extraction.RuleInputBinding.AssertionSpans);
        Assert.Equal(headline, span.Text);
        Assert.Equal(headline, span.FullContext);
        Assert.Equal(0, span.Start);
        Assert.Equal(headline.Length, span.Length);
        var result = Complete(extraction, label);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Materiality);
        Assert.False(result.AccuracyMeasured);
    }

    /// <summary>Only one trailing legal suffix may be removed; no brand prefix or unreviewed company alias is invented.</summary>
    [Theory]
    [InlineData("DT Midstream Inc", "DT Midstream", "DT")]
    [InlineData("T-Mobile US Inc", "T-Mobile US", "T-Mobile")]
    [InlineData("Navitas Semiconductor Corp", "Navitas Semiconductor", "Navitas")]
    [InlineData("Acme Inc Corp", "Acme Inc", "Acme")]
    public void AliasesAreLimitedToOfficialNameAndOneLegalSuffix(string official, string allowed, string forbidden)
    {
        var aliases = CompanyNewsAssessmentClassifier.GetIssuerAliases(Mapping(name: official));
        Assert.Contains(official, aliases);
        Assert.Contains(allowed, aliases);
        Assert.DoesNotContain(forbidden, aliases);
    }

    /// <summary>Cross-company clauses, uncertain actor text and unsupported brand shortening remain unknown despite positive tone.</summary>
    [Theory]
    [InlineData("Barclays Upgrades XYZ to Outperform, Announces $145 Price Target")]
    [InlineData("DT Midstream Upgrades XYZ to Outperform")]
    [InlineData("Barclays Upgrades DT Midstream and XYZ to Outperform")]
    [InlineData("Barclays Upgrades DT Midstream to Outperform, Downgrades XYZ to Sell")]
    [InlineData("Barclays Upgrades DT Midstream to Outperform; XYZ earnings beat estimates")]
    [InlineData("Barclays Upgrades DT Midstream to Outperform. XYZ cuts spending")]
    [InlineData("Not Barclays Upgrades DT Midstream to Outperform")]
    [InlineData("Barclays Might Upgrades DT Midstream to Outperform")]
    [InlineData("ABC Cuts Guidance UBS Upgrades DT Midstream to Outperform")]
    [InlineData("XYZ Wins Contract UBS Upgrades DTM to Outperform")]
    [InlineData("XYZ Acquires ABC UBS Upgrades DTM to Outperform")]
    [InlineData("XYZ Loses Contract UBS Upgrades DTM to Outperform")]
    [InlineData("DT Midstream earnings unchanged; XYZ beats estimates")]
    [InlineData("DT Midstream earnings beat estimates, revenue misses expectations")]
    [InlineData("DT raises guidance")]
    public void UnsupportedRelationsCannotBorrowAnIssuerOrAnalystDirection(string headline)
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision(headline), "DTM", Now, Mapping());
        Assert.False(extraction.CanAssess);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(extraction, CompanyNewsPolarity.Positive).State);
        Assert.Equal(headline, extraction.ModelInputText);
    }

    /// <summary>Reported or refuted actor prose cannot lose its qualifiers in either headline or surrounding supporting text.</summary>
    [Theory]
    [InlineData("Report Falsely Says UBS Upgrades DT Midstream to Outperform")]
    [InlineData("Report Incorrectly Says UBS Upgrades DT Midstream to Outperform")]
    [InlineData("UBS Incorrectly Upgrades DT Midstream to Outperform")]
    [InlineData("Denied Report Says UBS Upgrades DT Midstream to Outperform")]
    [InlineData("Refuted Claim Says UBS Upgrades DT Midstream to Outperform")]
    [InlineData("UBS Allegedly Upgrades DT Midstream to Outperform")]
    [InlineData("Report Says UBS Upgrades DT Midstream to Outperform")]
    [InlineData("Unverified Claim UBS Upgrades DT Midstream to Outperform")]
    public void NarrativeQualifiersCannotBeCanonicalizedIntoAnAssertedUpgrade(string text)
    {
        var headline = CompanyNewsAssessmentClassifier.Extract(Revision(text), "DTM", Now, Mapping());
        Assert.False(headline.CanAssess);
        Assert.Contains("headline_reported_or_refuted_assertion_requires_review", headline.Reasons);
        Assert.Equal(text, headline.RuleInputBinding!.Headline);
        Assert.Equal(text, headline.ModelInputText);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(headline, CompanyNewsPolarity.Positive).State);
        foreach (var field in new[] { "summary", "body" })
        {
            var revision = Revision("UBS Upgrades DT Midstream to Outperform",
                field == "summary" ? text : null, field == "body" ? text : null);
            var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "DTM", Now, Mapping());
            Assert.False(extraction.CanAssess);
            Assert.Contains($"{field}_reported_or_refuted_assertion_requires_review", extraction.Reasons);
            Assert.Contains(text, extraction.ModelInputText);
            Assert.Equal(text, field == "summary" ? extraction.RuleInputBinding!.Summary : extraction.RuleInputBinding!.Body);
            Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(extraction, CompanyNewsPolarity.Positive).State);
        }
    }

    /// <summary>Event clauses are rejected as analyst actors in supporting fields using the same shared rule-cue veto.</summary>
    [Theory]
    [InlineData("XYZ Wins Contract UBS Upgrades DTM to Outperform")]
    [InlineData("XYZ Acquires ABC UBS Upgrades DTM to Outperform")]
    [InlineData("XYZ Loses Contract UBS Upgrades DTM to Outperform")]
    public void SupportingActorCannotContainAnotherCompanyEvent(string text)
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(
            Revision("UBS Upgrades DT Midstream to Outperform", body: text), "DTM", Now, Mapping());
        Assert.False(extraction.CanAssess);
        Assert.Equal(text, extraction.RuleInputBinding!.Body);
        Assert.Contains(text, extraction.ModelInputText);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(extraction, CompanyNewsPolarity.Positive).State);
    }

    /// <summary>Mapped supporting fields remain complete and contradictory issuer assertions continue withholding entry.</summary>
    [Fact]
    public void MappedSupportingContextPreservesContradictoryStatements()
    {
        const string headline = "DT Midstream raises guidance";
        const string body = "DT Midstream cuts guidance";
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision(headline, body: body), "DTM", Now, Mapping());
        Assert.False(extraction.CanAssess);
        Assert.Equal(headline + "\n\n" + body, extraction.ModelInputText);
        Assert.Equal(body, extraction.Revision.Body);
        Assert.Equal("DTM cuts guidance", extraction.RuleInputBinding!.Body);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(extraction, CompanyNewsPolarity.Positive).State);
    }

    /// <summary>Mixed earnings clauses and unsupported brand aliases cannot become positive through mapping or model tone.</summary>
    [Fact]
    public void MixedMobileIssuerHeadlineRemainsUnknown()
    {
        const string headline = "T-Mobile US earnings beat estimates, revenue missed expectations";
        var extraction = CompanyNewsAssessmentClassifier.Extract(Revision(headline), "TMUS", Now,
            Mapping("TMUS", "T-Mobile US Inc"));
        Assert.False(extraction.CanAssess);
        Assert.Equal(headline, extraction.ModelInputText);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(extraction, CompanyNewsPolarity.Positive).State);
    }

    /// <summary>Old v1 payloads omit new optional fields and are replayed with their original literal-ticker grammar.</summary>
    [Fact]
    public void PriorV1PayloadDoesNotSilentlyAcquireMappedSemantics()
    {
        var old = CompanyNewsAssessmentClassifier.Extract(Revision("DTM raises guidance"), "DTM", Now);
        var json = JsonSerializer.Serialize(old);
        Assert.DoesNotContain("IssuerMapping", json);
        Assert.DoesNotContain("RuleInputBinding", json);
        var restored = JsonSerializer.Deserialize<CompanyNewsExtraction>(json)!;
        Assert.Equal(CompanyNewsAssessmentState.Positive, Complete(restored, CompanyNewsPolarity.Positive).State);
        var unmappedName = CompanyNewsAssessmentClassifier.Extract(Revision("DT Midstream raises guidance"), "DTM", Now);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, Complete(unmappedName, CompanyNewsPolarity.Positive).State);
        Assert.Throws<ArgumentException>(() => Complete(old with { IssuerMapping = Mapping() }, CompanyNewsPolarity.Positive));
        var mapped = CompanyNewsAssessmentClassifier.Extract(Revision("DT Midstream raises guidance"), "DTM", Now, Mapping());
        Assert.Throws<ArgumentException>(() => Complete(mapped with { RuleInputBinding = null }, CompanyNewsPolarity.Positive));
    }

    /// <summary>Source metadata and mapping capture clocks are required before official-name binding can be reused.</summary>
    [Fact]
    public void InvalidMappingMetadataDoesNotProduceAnAssessment()
    {
        var revision = Revision("DT Midstream raises guidance");
        Assert.Throws<ArgumentException>(() => CompanyNewsAssessmentClassifier.Extract(revision, "DTM", Now,
            Mapping() with { Symbol = "XYZ" }));
        Assert.Throws<ArgumentException>(() => CompanyNewsAssessmentClassifier.Extract(revision, "DTM", Now,
            Mapping() with { ObservedAtUtc = Now.AddDays(1) }));
        Assert.Throws<ArgumentException>(() => CompanyNewsAssessmentClassifier.Extract(revision, "DTM", Now,
            Mapping() with { SourceContentSha256 = "unverified" }));
    }
}
