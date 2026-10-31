using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsAssessmentStreamingIoTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T13:00:00Z");
    private static readonly CompanyNewsPinnedModel Model = new("synthetic", "ProsusAI/finbert", new string('a', 40),
        new string('b', 64), "Synthetic training provenance explicitly unknown. café 雪", null);

    /// <summary>Confirms full packet bytes, content address, source graph and deserialized text remain identical to the former serializer.</summary>
    [Fact]
    public async Task StreamedPacketPreservesBytesHashAndCompleteVerification()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var manifest = await BuildManifest(fixture);
        var previousBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CompanyNewsAssessmentManifestStore.JsonOptions);
        var publication = await CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(Path.Combine(fixture.Root, "assessments"), manifest);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(publication.Path));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(previousBytes)), publication.Sha256);
        Assert.Equal(publication, await CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(Path.Combine(fixture.Root, "assessments"), manifest));
        var parsed = CompanyNewsAssessmentManifestStore.ReadPublished(publication);
        Assert.Equal(previousBytes, JsonSerializer.SerializeToUtf8Bytes(parsed, CompanyNewsAssessmentManifestStore.JsonOptions));
        Assert.Equal(manifest.Rows[0].Source.Revision.Body, parsed.Rows[0].Source.Revision.Body);
        var declarations = CompanyNewsAssessmentDependencyReader.ReadDeclarations(fixture.Root, publication.Path, publication.Sha256);
        Assert.Equal(manifest.Dependencies.Length + 1, declarations.Length);
        Assert.Contains(new CompanyNewsAssessmentDependency(Path.GetRelativePath(fixture.Root, publication.Path), publication.Sha256), declarations);
        foreach (var dependency in manifest.Dependencies) Assert.Contains(dependency, declarations);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "assessments"), "*.stage", SearchOption.AllDirectories));
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, manifest.Dependencies[0].Path), "changed synthetic source");
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.ReadPublished(publication));
    }

    /// <summary>Conflicting content-address destinations are preserved; a cancelled publication exposes no packet or staging artifact.</summary>
    [Fact]
    public async Task PacketConflictAndCancellationNeverReplaceCommittedContent()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var manifest = await BuildManifest(fixture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CompanyNewsAssessmentManifestStore.JsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var output = Path.Combine(fixture.Root, "assessments");
        var path = Path.Combine(output, hash, "company-assessments.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "committed conflicting fixture");
        await Assert.ThrowsAsync<IOException>(() => CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(output, manifest));
        Assert.Equal("committed conflicting fixture", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledOutput = Path.Combine(fixture.Root, "cancelled-publication");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(cancelledOutput, manifest, cancellation.Token));
        Assert.Empty(CompanyNewsAssessmentManifestStore.FindPublished(cancelledOutput));
        Assert.False(Directory.Exists(cancelledOutput));
    }

    /// <summary>Internally consistent rows cannot hide a raw file replayed from the original selection outside the leased declarations.</summary>
    [Fact]
    public async Task AssessmentRejectsOmittedOriginalSelectionDependency()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var manifest = await BuildManifest(fixture);
        var omitted = Assert.Single(manifest.Dependencies.Where(item => item.Path.Replace('\\', '/') == "datasets/streaming-company.json"));
        var forged = manifest with { Dependencies = manifest.Dependencies.Remove(omitted) };
        CompanyNewsAssessmentManifestStore.Validate(forged);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(forged, CompanyNewsAssessmentManifestStore.JsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.Combine(fixture.Root, "forged-assessments", hash, "company-assessments.json");
        CompanyNewsAssessmentManifestStore.WriteExclusive(path, bytes);
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.ReadVerified(fixture.Root, path, hash));
        await Assert.ThrowsAsync<InvalidDataException>(() => CompanyNewsAssessmentManifestStore.PublishExclusiveAsync(
            Path.Combine(fixture.Root, "new-assessments"), forged));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "new-assessments")));
    }

    /// <summary>Partial producer output and late cancellation remove only exact owned staging, while identical retries preserve final bytes.</summary>
    [Fact]
    public async Task StreamedArtifactFailureCancellationAndRetriesAreAtomic()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var output = Path.Combine(fixture.Root, "reports");
        var path = Path.Combine(output, "report.json");
        await Assert.ThrowsAsync<IOException>(() => CompanyNewsAssessmentManifestStore.WriteStreamExclusiveAsync(path,
            async (stream, token) => { await stream.WriteAsync("partial"u8.ToArray(), token); throw new IOException("injected failure"); }));
        Assert.Empty(Directory.GetFiles(output));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompanyNewsAssessmentManifestStore.WriteStreamExclusiveAsync(path,
            async (stream, token) => { await stream.WriteAsync("complete"u8.ToArray(), token); cancellation.Cancel(); }, cancellation.Token));
        Assert.Empty(Directory.GetFiles(output));
        CompanyNewsAssessmentManifestStore.WriteExclusive(path, "committed"u8.ToArray());
        CompanyNewsAssessmentManifestStore.WriteExclusive(path, "committed"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => CompanyNewsAssessmentManifestStore.WriteStreamExclusiveAsync(path,
            (stream, token) => stream.WriteAsync("changed"u8.ToArray(), token).AsTask()));
        Assert.Equal("committed", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(output));
    }

    /// <summary>Declaration discovery ignores row-shaped content without granting verification and still checks hash, bounds and conflicting declarations.</summary>
    [Fact]
    public void DeclarationReaderSkipsLargeRowsButRejectsChangedHashAndEscapingGraph()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var dependency = new CompanyNewsAssessmentDependency("query-semantics.txt", new string('a', 64));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Rows = new[] { new { FakeRow = new string('雪', 170_000) } }, Dependencies = new[] { dependency } });
        var path = Path.Combine(fixture.Root, "declarations-only.json");
        File.WriteAllBytes(path, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(2, CompanyNewsAssessmentDependencyReader.ReadDeclarations(fixture.Root, path, hash).Length);
        Assert.Throws<JsonException>(() => CompanyNewsAssessmentManifestStore.ReadVerified(fixture.Root, path, hash));
        Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentDependencyReader.ReadDeclarations(fixture.Root, path, new string('b', 64)));
        foreach (var dependencies in new[] { new[] { dependency with { Path = "../escape.json" } },
            new[] { dependency, dependency with { Sha256 = new string('b', 64) } } })
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { Dependencies = dependencies, Rows = Array.Empty<object>() });
            File.WriteAllBytes(path, bytes);
            hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.Throws<InvalidDataException>(() => CompanyNewsAssessmentDependencyReader.ReadDeclarations(fixture.Root, path, hash));
        }
    }

    /// <summary>Builds one real synthetic archive lineage with long Unicode original text and deterministic local model evidence.</summary>
    internal static async Task<CompanyNewsAssessmentManifest> BuildManifest(HistoricalDiagnosticNewsTests.Archive fixture)
    {
        fixture.News("streaming-company", [new { id = "story-α", created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z",
            symbols = new[] { "ABC" }, headline = "ABC raises guidance", summary = (string?)null,
            content = String.Concat(Enumerable.Repeat("ABC raises guidance. café <tag> \\ 雪 🧪\n", 1200)) }]);
        fixture.Provider();
        var publication = Assert.Single(HistoricalDiagnosticNewsManifestStore.FindPublished(Path.Combine(fixture.Root, "published")));
        var selected = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, publication.Path, publication.Sha256);
        return await CompanyNewsAssessmentBuilder.BuildAsync(fixture.Root, "synthetic-streaming-parity",
            new("diagnostic", Path.GetRelativePath(fixture.Root, publication.Path), publication.Sha256), selected.Sources,
            selected.Dependencies, Model, (extraction, _) => Task.FromResult(new CompanyNewsModelEvidence(Model,
                extraction.ModelInputSha256, CompanyNewsPolarity.Positive, .9m, Now)), new FixedClock());
    }

    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Uses a fixed observed clock after synthetic receipt capture.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
