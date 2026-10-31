using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsIssuerMappingPipelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T13:00:00Z");
    private static readonly CompanyNewsPinnedModel Model = new("finbert-http", "ProsusAI/finbert", new string('a', 40),
        new string('b', 64), "Synthetic model; exact training data unproved.", null);

    /// <summary>Source observations provide issuer names even when unchanged normalized earnings facts exclude those symbols.</summary>
    [Fact]
    public void ReaderUsesFrozenRawIssuerRowsWithoutWideningNormalizedFacts()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.Earnings("issuers", [new { ticker = "ABC", company = "Acme Inc" }, new { ticker = "DEF", company = "Delta Corp" }]);
        var (path, hash, historical) = FreezeIssuerManifest(fixture);
        Assert.DoesNotContain(historical.Facts, item => item.Kind == HistoricalFactKind.EarningsCurrentVintage);
        var result = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, path, hash);
        Assert.Equal(2, result.Mappings.Count);
        var mapping = result.Mappings["ABC"];
        Assert.Equal("Acme Inc", mapping.OfficialName);
        Assert.Equal(HistoricalDiagnosticNewsTests.Archive.Capture, mapping.ObservedAtUtc);
        Assert.Equal("finviz", mapping.SourceProvider);
        var observation = Assert.Single(historical.Observations.Where(item => item.Resource == "/api/calendar/earnings"));
        Assert.Equal(observation.PayloadSha256, mapping.SourceContentSha256);
        Assert.Equal(observation.ObservationId + ":items:0", mapping.SourceIdentity);
        Assert.Equal(3, result.Dependencies.Length);
        Assert.Empty(result.UnavailableSymbols);
    }

    /// <summary>A conflicting name, cross-symbol alias or malformed row withholds affected names without aborting valid rows.</summary>
    [Fact]
    public void ReaderWithholdsConflictingCollidingAndMalformedNames()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.Earnings("issuers", [new { ticker = "ABC", company = "Acme Inc" }, new { ticker = "ABC", company = "Different Corp" },
            new { ticker = "BBB", company = "Shared Inc" }, new { ticker = "CCC", company = "Shared Corp" },
            new { ticker = "BAD", company = " Bad Inc " }, new { ticker = "GOOD", company = "Valid Inc" }]);
        var (path, hash, _) = FreezeIssuerManifest(fixture);
        var result = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, path, hash);
        Assert.Equal("GOOD", Assert.Single(result.Mappings).Key);
        Assert.Equal(new[] { "ABC", "BAD", "BBB", "CCC" }, result.UnavailableSymbols.ToArray());
        Assert.Contains(result.Exclusions, item => item.Symbol == "BAD" && item.Reason == "official_name_or_mapping_metadata_unsupported");
    }

    /// <summary>Actual receipt metadata cannot be replaced by an internally consistent changed source manifest.</summary>
    [Fact]
    public void ReaderRejectsChangedSourceObservationClock()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.Earnings("issuers", [new { ticker = "ABC", company = "Acme Inc" }]);
        var (_, _, historical) = FreezeIssuerManifest(fixture);
        var forged = historical with { Observations = historical.Observations.Select(item => item.Resource == "/api/calendar/earnings"
            ? item with { CapturedAtUtc = item.CapturedAtUtc.AddSeconds(-1) } : item).ToImmutableArray() };
        var path = HistoricalEvidenceManifestStore.WriteExclusive(Path.Combine(fixture.Root, "forged-issuer-manifest"), forged);
        Assert.Throws<InvalidDataException>(() => CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, path, NewsAssessmentWorkflow.FileHash(path)));
    }

    /// <summary>Mapped v2 and literal v1 fallback rows share one frozen source package; forged self-consistent aliases fail source rederivation.</summary>
    [Fact]
    public async Task MappedPackagePreservesLiteralFallbackAndRejectsInventedName()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.News("company", [Story("mapped", "ABC", "Acme raises full-year guidance"), Story("literal", "DEF", "DEF raises full-year guidance")]);
        fixture.Earnings("issuers", [new { ticker = "ABC", company = "Acme Inc" }]);
        var (mappingPath, mappingHash, _) = FreezeIssuerManifest(fixture);
        var provider = fixture.Provider();
        var diagnostic = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), provider.Manifest);
        var selection = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, diagnostic.Path, diagnostic.Sha256);
        var mappings = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, mappingPath, mappingHash,
            selection.Sources.SelectMany(source => source.Symbols));
        var manifest = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture",
            new("diagnostic", Path.GetRelativePath(fixture.Root, diagnostic.Path), diagnostic.Sha256), selection.Sources,
            selection.Dependencies, Model, (extraction, _) => Task.FromResult(new CompanyNewsModelEvidence(Model,
                extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, Now)), new FixedClock(), issuerMappings: mappings);
        Assert.Equal(CompanyNewsAssessmentManifest.MappedSchema, manifest.SchemaVersion);
        var mapped = Assert.Single(manifest.Rows.Where(row => row.Symbol == "ABC"));
        Assert.Equal(CompanyNewsAssessmentState.Positive, mapped.Assessment.State);
        Assert.Equal(CompanyNewsAssessmentClassifier.MappedExtractionVersion, mapped.Assessment.ExtractionVersion);
        Assert.Equal("Acme Inc", mapped.Assessment.Extraction.IssuerMapping!.OfficialName);
        var literal = Assert.Single(manifest.Rows.Where(row => row.Symbol == "DEF"));
        Assert.Equal(CompanyNewsAssessmentState.Positive, literal.Assessment.State);
        Assert.Null(literal.Assessment.Extraction.IssuerMapping);
        Assert.Equal(CompanyNewsAssessmentClassifier.ExtractionVersion, literal.Assessment.ExtractionVersion);
        Assert.All(manifest.Rows, row => Assert.True(manifest.Policy.AllowsExtraction(row.Assessment.Extraction)));
        var publication = CompanyNewsAssessmentManifestStore.PublishExclusive(Path.Combine(fixture.Root, "assessments"), manifest);
        Assert.Equal(2, CompanyNewsAssessmentManifestStore.ReadPublished(publication).Rows.Length);
        var invented = mapped.Assessment.Extraction.IssuerMapping with { OfficialName = "Invented Inc" };
        var extraction = CompanyNewsAssessmentClassifier.Extract(mapped.Source.Revision, mapped.Symbol, Now, invented);
        var forged = manifest with { Rows = manifest.Rows.Select(row => row.Symbol == "ABC" ? row with {
            Assessment = CompanyNewsAssessmentClassifier.Complete(extraction, null, Model, Now) } : row).ToImmutableArray() };
        CompanyNewsAssessmentManifestStore.Validate(forged);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(forged, CompanyNewsAssessmentManifestStore.JsonOptions);
        var hash = NewsAssessmentWorkflow.Hash(bytes);
        var forgedPath = Path.Combine(fixture.Root, "assessments", hash, "company-assessments.json");
        CompanyNewsAssessmentManifestStore.WriteExclusive(forgedPath, bytes);
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.ReadPublished(new(hash, forgedPath, hash)));
    }

    /// <summary>An alias equal to another selected issuer's ticker cannot attribute its assertion to two companies, even without a name mapping for that ticker.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AliasMatchingAnotherIssuerTickerIsWithheldForMappedAndLiteralFallbackSymbols(bool otherIssuerHasMapping)
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        fixture.News("company", [new { id = "collision", symbols = new[] { "AAA", "BBB" }, headline = "BBB raises full-year guidance",
            created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z" }]);
        fixture.Earnings("issuers", otherIssuerHasMapping
            ? [new { ticker = "AAA", company = "BBB Inc" }, new { ticker = "BBB", company = "Beta Ltd" }]
            : [new { ticker = "AAA", company = "BBB Inc" }]);
        var (mappingPath, mappingHash, _) = FreezeIssuerManifest(fixture);
        var provider = fixture.Provider();
        var diagnostic = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), provider.Manifest);
        var selection = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, diagnostic.Path, diagnostic.Sha256);
        var mappings = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, mappingPath, mappingHash,
            selection.Sources.SelectMany(source => source.Symbols));
        Assert.False(mappings.Mappings.ContainsKey("AAA"));
        Assert.Equal(otherIssuerHasMapping, mappings.Mappings.ContainsKey("BBB"));
        Assert.Contains("AAA", mappings.UnavailableSymbols);
        var calls = 0;
        var manifest = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture",
            new("diagnostic", Path.GetRelativePath(fixture.Root, diagnostic.Path), diagnostic.Sha256), selection.Sources,
            selection.Dependencies, Model, (extraction, _) => { calls++; return Task.FromResult(new CompanyNewsModelEvidence(Model,
                extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, Now)); }, new FixedClock(), issuerMappings: mappings);
        Assert.Equal(1, calls);
        var misattributed = Assert.Single(manifest.Rows.Where(row => row.Symbol == "AAA"));
        Assert.Null(misattributed.Assessment.Extraction.IssuerMapping);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, misattributed.Assessment.State);
        Assert.Equal(CompanyNewsAssessmentState.Positive, Assert.Single(manifest.Rows.Where(row => row.Symbol == "BBB")).Assessment.State);
        var publication = CompanyNewsAssessmentManifestStore.PublishExclusive(Path.Combine(fixture.Root, "assessments"), manifest);
        Assert.Equal(2, CompanyNewsAssessmentManifestStore.ReadPublished(publication).Rows.Length);
        if (!otherIssuerHasMapping)
        {
            // A map set computed without the selected fallback ticker must fail before it can trigger any model request.
            var incomplete = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, mappingPath, mappingHash);
            Assert.True(incomplete.Mappings.ContainsKey("AAA"));
            var before = calls;
            await Assert.ThrowsAsync<InvalidDataException>(() => CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture",
                new("diagnostic", Path.GetRelativePath(fixture.Root, diagnostic.Path), diagnostic.Sha256), selection.Sources,
                selection.Dependencies, Model, (_, _) => { calls++; throw new InvalidOperationException("Inference must not start"); },
                new FixedClock(), issuerMappings: incomplete));
            Assert.Equal(before, calls);
        }
    }

    /// <summary>Writes only a disposable synthetic manifest using the existing historical publication mechanism.</summary>
    private static (string Path, string Sha256, HistoricalEvidenceManifest Manifest) FreezeIssuerManifest(HistoricalDiagnosticNewsTests.Archive fixture)
    {
        var manifest = HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(fixture.Root, HistoricalDiagnosticNewsTests.Archive.Capture.AddDays(1));
        var path = HistoricalEvidenceManifestStore.WriteExclusive(Path.Combine(fixture.Root, "issuer-manifest"), manifest);
        return (path, NewsAssessmentWorkflow.FileHash(path), manifest);
    }

    /// <summary>Provides original archived assertion fields without labels, later prices or provider calls.</summary>
    private static object Story(string id, string symbol, string headline) => new { id, symbols = new[] { symbol }, headline,
        created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z" };

    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Retains a real-shaped retrospective October measurement clock in deterministic source tests.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
