using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TradingFlow.Cli;
using TradingFlow.Alpaca;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsEvidencePipelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T13:00:00Z");
    private static readonly CompanyNewsPinnedModel Model = new("finbert-http", "ProsusAI/finbert", new string('a', 40),
        new string('b', 64), "Synthetic fixture; training provenance explicitly unknown.", null);

    /// <summary>Creates an explicit ticker subject with an exact full-context inference input.</summary>
    private static CompanyNewsExtraction Extraction() => CompanyNewsAssessmentClassifier.Extract(
        new("alpaca", "story", "revision", "ABC raises full-year guidance"), "ABC", Now);

    /// <summary>Invalid destinations must fail before a model request or publication, including the second diagnostic destination.</summary>
    [Theory]
    [InlineData("missing-output")]
    [InlineData("missing-diagnostic-output")]
    [InlineData("outside-output")]
    [InlineData("outside-diagnostic-output")]
    public async Task CliRejectsInvalidOutputBeforeInferenceOrCreatingFiles(string defect)
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        using var outsideFixture = new HistoricalDiagnosticNewsTests.Archive();
        var (selection, _, _) = FixtureSelection(fixture);
        var modelPath = Path.Combine(fixture.Root, "model-policy.json");
        await File.WriteAllBytesAsync(modelPath, JsonSerializer.SerializeToUtf8Bytes(Model, CompanyNewsAssessmentManifestStore.JsonOptions));
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var outsideBefore = Directory.GetFiles(outsideFixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        // This local listener never accepts a request; a pending connection would prove invalid inputs reached inference.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var args = new List<string> { "--research-root", fixture.Root, "--diagnostic-manifest",
                Path.Combine(fixture.Root, selection.ResearchRootRelativePath), "--manifest-sha256", selection.Sha256,
                "--model-policy", modelPath, "--endpoint", $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}" };
            if (defect != "missing-output")
                args.AddRange(["--output", Path.Combine(defect == "outside-output" ? outsideFixture.Root : fixture.Root, "assessments")]);
            if (defect != "missing-diagnostic-output")
                args.AddRange(["--diagnostic-output", Path.Combine(defect == "outside-diagnostic-output" ? outsideFixture.Root : fixture.Root, "diagnostics")]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var output = new StringWriter();
            if (defect.StartsWith("missing-", StringComparison.Ordinal))
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(() => CompanyNewsAssessmentCommand.RunAsync(args.ToArray(), output, cancellation.Token));
                Assert.Contains(defect == "missing-output" ? "--output" : "--diagnostic-output", error.Message);
            }
            else
                await Assert.ThrowsAsync<InvalidDataException>(() => CompanyNewsAssessmentCommand.RunAsync(args.ToArray(), output, cancellation.Token));
            Assert.False(listener.Pending());
            Assert.Equal("", output.ToString());
            Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(outsideBefore, Directory.GetFiles(outsideFixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "assessments")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "diagnostics")));
            Assert.False(Directory.Exists(Path.Combine(outsideFixture.Root, "assessments")));
            Assert.False(Directory.Exists(Path.Combine(outsideFixture.Root, "diagnostics")));
        }
        finally { listener.Stop(); }
    }

    /// <summary>Accepts only the pinned loaded model and preserves actual model completion time.</summary>
    [Fact]
    public async Task StrictTransportBindsExactInputAndModelWithoutFallback()
    {
        var handler = new ResponseHandler(new { label = "positive", confidence = .9m, score = .9m,
            model = Model.Model, model_revision = Model.Version, model_loaded = true,
            input_sha256 = Extraction().ModelInputSha256, full_text_processed = true, input_token_count = 8,
            model_max_tokens = 512, model_artifact_sha256 = Model.ArtifactSha256 });
        using var client = new HttpClient(handler);
        var result = await new FinbertCompanyNewsAssessor(client, new Uri("http://localhost:8088"), Model, new FixedClock()).AssessAsync(Extraction());
        Assert.Equal(Model, result.Identity);
        Assert.Equal(Extraction().ModelInputSha256, result.InputContentSha256);
        Assert.Equal(Now, result.CompletedAtUtc);
        Assert.Contains("ABC raises full-year guidance", handler.Body);
        Assert.Contains("\"require_full_text\":true", handler.Body);
    }

    /// <summary>A reachable sidecar with the wrong revision or malformed probability fails instead of returning VADER.</summary>
    [Theory]
    [InlineData("cccccccccccccccccccccccccccccccccccccccc", .9)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", .7)]
    public async Task StrictTransportRejectsWrongRevisionOrScore(string revision, double score)
    {
        using var client = new HttpClient(new ResponseHandler(new { label = "positive", confidence = .9m, score,
            model = Model.Model, model_revision = revision, model_loaded = true,
            input_sha256 = Extraction().ModelInputSha256, full_text_processed = true, input_token_count = 8,
            model_max_tokens = 512, model_artifact_sha256 = Model.ArtifactSha256 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FinbertCompanyNewsAssessor(client,
            new Uri("http://localhost:8088"), Model, new FixedClock()).AssessAsync(Extraction()));
    }

    /// <summary>Unproved complete input or wrong loaded weights cannot borrow the configured model identity.</summary>
    [Theory]
    [InlineData(false, false, false, 8)]
    [InlineData(true, true, false, 8)]
    [InlineData(true, false, true, 8)]
    [InlineData(true, false, false, 513)]
    public async Task StrictTransportRejectsMissingFullInputProofAndWrongWeights(bool fullText, bool wrongInput, bool wrongWeights, int tokens)
    {
        using var client = new HttpClient(new ResponseHandler(new { label = "positive", confidence = .9m, score = .9m,
            model = Model.Model, model_revision = Model.Version, model_loaded = true,
            input_sha256 = wrongInput ? new string('c', 64) : Extraction().ModelInputSha256,
            full_text_processed = fullText, input_token_count = tokens, model_max_tokens = 512,
            model_artifact_sha256 = wrongWeights ? new string('d', 64) : Model.ArtifactSha256 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FinbertCompanyNewsAssessor(client,
            new Uri("http://localhost:8088"), Model, new FixedClock()).AssessAsync(Extraction()));
    }

    /// <summary>Failures remain revision-bound unknown states while actual receipt and diagnostic availability retain distinct clocks.</summary>
    [Fact]
    public async Task BuilderPublishesUnknownInferenceFailureAndRejectsForgedSource()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var (selection, sources, dependencies) = FixtureSelection(fixture);
        var source = Assert.Single(sources);
        var manifest = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture", selection, sources, dependencies, Model,
            (_, _) => throw new HttpRequestException("synthetic inference failure"), new FixedClock());
        var row = Assert.Single(manifest.Rows);
        Assert.Equal(CompanyNewsAssessmentState.Unknown, row.Assessment.State);
        Assert.False(row.Assessment.ModelEvidence!.Succeeded);
        Assert.Contains("synthetic inference failure", row.Assessment.ModelEvidence.FailureReason);
        Assert.Equal(Now, row.Assessment.AssessedAtUtc);
        Assert.Equal(source.AssumedAvailableAtUtc, row.Source.AssumedAvailableAtUtc);
        var publication = CompanyNewsAssessmentManifestStore.PublishExclusive(fixture.Root, manifest);
        Assert.Equal(publication, CompanyNewsAssessmentManifestStore.PublishExclusive(fixture.Root, manifest));
        var loaded = CompanyNewsAssessmentManifestStore.ReadPublished(publication);
        Assert.Equal(row.Assessment.State, Assert.Single(loaded.Rows).Assessment.State);
        var forged = manifest with { Rows = [row with { Source = source with { Revision = source.Revision with { Headline = "ABC earnings beat estimates" } } }] };
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.Validate(forged));
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, dependencies[0].Path), "changed");
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.ReadPublished(publication));
    }

    /// <summary>Even a newly content-addressed packet with internally consistent fake text must fail against original raw archive bytes.</summary>
    [Fact]
    public async Task StandalonePreviewRejectsSelfConsistentFabricatedSourceAndAssessment()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var (selection, sources, dependencies) = FixtureSelection(fixture);
        var manifest = await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture", selection, sources, dependencies, Model,
            (_, _) => throw new HttpRequestException("synthetic unavailable inference"), new FixedClock());
        var row = Assert.Single(manifest.Rows);
        var invented = row.Source with { Revision = row.Source.Revision with { Headline = "ABC cuts profit forecast" } };
        var extraction = CompanyNewsAssessmentClassifier.Extract(invented.Revision, row.Symbol, Now);
        var forged = manifest with { Rows = [row with { Source = invented,
            Assessment = CompanyNewsAssessmentClassifier.Complete(extraction, null, Model, Now) }] };
        CompanyNewsAssessmentManifestStore.Validate(forged);
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.PublishExclusive(fixture.Root, forged));
        // Simulate a different content address without passing the publisher; the standalone read must still rederive source rows.
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged, CompanyNewsAssessmentManifestStore.JsonOptions);
        var hash = NewsAssessmentWorkflow.Hash(bytes);
        var path = Path.Combine(fixture.Root, hash, "company-assessments.json");
        CompanyNewsAssessmentManifestStore.WriteExclusive(path, bytes);
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.ReadPublished(new(hash, path, hash)));
    }

    /// <summary>The bound availability assumption cannot be substituted for the real receipt clock.</summary>
    [Fact]
    public async Task BuilderRejectsBackdatedActualAssessments()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var (selection, sources, dependencies) = FixtureSelection(fixture);
        var source = Assert.Single(sources) with { ActualReceivedAtUtc = Now.AddSeconds(1) };
        await Assert.ThrowsAsync<InvalidDataException>(() => CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-fixture", selection, [source],
            dependencies, Model,
            (extraction, _) => Task.FromResult(new CompanyNewsModelEvidence(Model, extraction.ModelInputSha256,
                CompanyNewsPolarity.Positive, .9m, Now)), new FixedClock()));
    }

    /// <summary>Uses the existing disposable raw-archive fixture so every synthetic file is removed even when an assertion fails.</summary>
    private static (CompanyNewsAssessmentSelection Selection, ImmutableArray<CompanyNewsAssessmentSource> Sources,
        ImmutableArray<CompanyNewsAssessmentDependency> Dependencies) FixtureSelection(HistoricalDiagnosticNewsTests.Archive fixture)
    {
        fixture.News("company", [new { id = "story", created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z",
            symbols = new[] { "ABC" }, headline = "ABC raises full-year guidance" }]);
        var provider = fixture.Provider();
        var publication = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), provider.Manifest);
        var selected = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, publication.Path, publication.Sha256);
        return (new("diagnostic", Path.GetRelativePath(fixture.Root, publication.Path), publication.Sha256), selected.Sources, selected.Dependencies);
    }

    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Provides deterministic actual completion clocks without network time dependencies.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ResponseHandler(object payload) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        /// <summary>Captures inference input and returns deterministic fake sidecar metadata without network access.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
        }
    }
}
