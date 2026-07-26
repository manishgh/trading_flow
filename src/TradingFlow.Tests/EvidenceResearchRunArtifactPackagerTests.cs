using System.Collections.Concurrent;
using System.Text;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceResearchRunArtifactPackagerTests
{
    [Fact]
    public async Task PackageAsync_IsByteIdenticalAcrossReorderedInputs()
    {
        var store = new InMemoryImmutableArtifactStore();
        var packager = new EvidenceResearchRunArtifactPackager(store);

        var first = await packager.PackageAsync(Request(
            datasets: [Dataset("dataset-b", 'b'), Dataset("dataset-a", 'a')],
            config: """{"z":1.0,"a":{"y":2,"x":3}}""",
            universe: """{"symbols":{"MSFT":"security-2","AAPL":"security-1"},"as_of":"2025-01-01"}""",
            outputs:
            [
                Output("holdout", """{"return":0.1250,"trades":40}"""),
                Output("development", """{"trades":100,"return":0.20}""")
            ]));
        var second = await packager.PackageAsync(Request(
            datasets: [Dataset("dataset-a", 'a'), Dataset("dataset-b", 'b')],
            config: """{"a":{"x":3.0,"y":2.0},"z":1}""",
            universe: """{"as_of":"2025-01-01","symbols":{"AAPL":"security-1","MSFT":"security-2"}}""",
            outputs:
            [
                Output("development", """{"return":0.2,"trades":100}"""),
                Output("holdout", """{"trades":40,"return":0.125}""")
            ]));

        Assert.Equal(first.Artifacts.AllArtifacts, second.Artifacts.AllArtifacts);
        Assert.Equal(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(first.ManifestDraft),
            EvidenceCanonicalJson.SerializeToUtf8Bytes(second.ManifestDraft));
        Assert.Equal(10, store.ArtifactCount);
        Assert.All(store.Payloads, payload =>
            Assert.Equal(
                payload,
                CanonicalizeThroughJsonDocument(payload)));
    }

    [Fact]
    public async Task PackageAsync_ReadyRunBindsImmutableHoldoutToPackagedInputs()
    {
        var result = await new EvidenceResearchRunArtifactPackager(
                new InMemoryImmutableArtifactStore())
            .PackageAsync(Request());

        var holdout = Assert.IsType<EvidenceHoldoutIdentity>(result.ManifestDraft.Holdout);
        Assert.True(result.ManifestDraft.EvidenceReady);
        Assert.Empty(result.ManifestDraft.ReadinessFailures);
        Assert.Equal(result.Artifacts.UniverseLedger, holdout.UniverseLedgerArtifact);
        Assert.Equal(result.Artifacts.PartitionDefinition, holdout.PartitionDefinitionArtifact);
        Assert.Equal(result.ManifestDraft.InputDatasets, holdout.Datasets);
        Assert.Equal(result.ManifestDraft.StudyPartitions.Holdout.StartUtc, holdout.StartUtc);
        Assert.Equal(result.ManifestDraft.StudyPartitions.Holdout.EndUtc, holdout.EndUtc);
    }

    [Fact]
    public async Task PackageAsync_NonReadyRunRetainsPreparedHoldoutAndCanonicalFailures()
    {
        var result = await new EvidenceResearchRunArtifactPackager(
                new InMemoryImmutableArtifactStore())
            .PackageAsync(Request(
                evidenceReady: false,
                readinessFailures:
                [
                    "benchmark coverage missing",
                    "borrow model missing",
                    "benchmark coverage missing"
                ]));

        Assert.False(result.ManifestDraft.EvidenceReady);
        var holdout = Assert.IsType<EvidenceHoldoutIdentity>(result.ManifestDraft.Holdout);
        Assert.Equal(result.Artifacts.UniverseLedger, holdout.UniverseLedgerArtifact);
        Assert.Equal(result.Artifacts.PartitionDefinition, holdout.PartitionDefinitionArtifact);
        Assert.Equal(result.ManifestDraft.InputDatasets, holdout.Datasets);
        Assert.Equal(result.ManifestDraft.StudyPartitions.Holdout.StartUtc, holdout.StartUtc);
        Assert.Equal(result.ManifestDraft.StudyPartitions.Holdout.EndUtc, holdout.EndUtc);
        Assert.Equal(
            ["benchmark coverage missing", "borrow model missing"],
            result.ManifestDraft.ReadinessFailures);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PackageAsync_InvalidReadinessFailsBeforeWriting(
        bool evidenceReady,
        bool includeFailure)
    {
        var store = new InMemoryImmutableArtifactStore();
        var request = Request(
            evidenceReady: evidenceReady,
            readinessFailures: includeFailure ? ["not ready"] : []);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new EvidenceResearchRunArtifactPackager(store).PackageAsync(request));

        Assert.Equal(0, store.ArtifactCount);
    }

    [Fact]
    public async Task PackageAsync_StoreCollisionFailsClosed()
    {
        var store = new InMemoryImmutableArtifactStore
        {
            ThrowCollisionOnWrite = 2
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EvidenceResearchRunArtifactPackager(store).PackageAsync(Request()));

        Assert.Equal(1, store.ArtifactCount);
    }

    [Fact]
    public async Task PackageAsync_PostWriteTamperFailsClosed()
    {
        var store = new InMemoryImmutableArtifactStore
        {
            ReportTamperOnVerification = true
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EvidenceResearchRunArtifactPackager(store).PackageAsync(Request()));

        Assert.Contains("verification failed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageAsync_HonorsMidPackageCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new InMemoryImmutableArtifactStore
        {
            CancellationSource = cancellation,
            CancelAfterWrite = 2
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EvidenceResearchRunArtifactPackager(store)
                .PackageAsync(Request(), cancellation.Token));

        Assert.Equal(2, store.ArtifactCount);
    }

    [Fact]
    public async Task PackageAsync_RejectsDuplicateJsonPropertiesBeforeWriting()
    {
        var store = new InMemoryImmutableArtifactStore();

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            new EvidenceResearchRunArtifactPackager(store).PackageAsync(
                Request(config: """{"period":20,"period":50}""")));

        Assert.Equal(0, store.ArtifactCount);
    }

    private static EvidenceResearchRunPackageRequest Request(
        IReadOnlyList<EvidenceDatasetReference>? datasets = null,
        string config = """{"lookback":126,"rebalance":"weekly"}""",
        string universe = """{"as_of":"2025-01-01","symbols":{"AAPL":"security-1"}}""",
        IReadOnlyList<EvidenceResearchNamedOutput>? outputs = null,
        bool evidenceReady = true,
        IReadOnlyList<string>? readinessFailures = null) =>
        new(
            "run-001",
            "cross-sectional-momentum-v1",
            Utc(2026, 7, 25),
            "git:0123456789abcdef",
            datasets ?? [Dataset("dataset-a", 'a')],
            "universe-ledger-001",
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    Utc(2020, 1, 1),
                    Utc(2022, 1, 1)),
                new EvidenceStudyWindow(
                    "validation",
                    Utc(2022, 1, 1),
                    Utc(2024, 1, 1)),
                new EvidenceStudyWindow(
                    "holdout",
                    Utc(2024, 1, 1),
                    Utc(2026, 1, 1))),
            Json(config),
            Json(universe),
            Json("""{"development":["2020-01-01","2022-01-01"],"validation":["2022-01-01","2024-01-01"],"holdout":["2024-01-01","2026-01-01"]}"""),
            new EvidenceResearchAssumptionPayloads(
                Json("""{"commission_bps":1,"fees_bps":0.2}"""),
                Json("""{"source":"sip_nbbo","half_spread":true}"""),
                Json("""{"model":"volume_participation","max_bps":10}"""),
                Json("""{"annual_borrow_bps":300,"availability_required":true}"""),
                Json("""{"ticker":"SPY","return":"total_return"}""")),
            outputs ??
            [
                Output("development", """{"return":0.2,"trades":100}"""),
                Output("holdout", """{"return":0.125,"trades":40}""")
            ],
            evidenceReady,
            readinessFailures ?? (evidenceReady ? [] : ["not ready"]));

    private static EvidenceResearchNamedOutput Output(string name, string json) =>
        new(name, Json(json));

    private static EvidenceResearchJsonPayload Json(string value) =>
        new(Encoding.UTF8.GetBytes(value));

    private static EvidenceDatasetReference Dataset(string id, char hashCharacter)
    {
        var hash = new string(hashCharacter, 64);
        return new EvidenceDatasetReference(
            id,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(hash, 100, "application/json"),
                new EvidenceObjectNamespace("normalized/datasets")));
    }

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static byte[] CanonicalizeThroughJsonDocument(byte[] value)
    {
        using var document = System.Text.Json.JsonDocument.Parse(value);
        return Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
    }

    private sealed class InMemoryImmutableArtifactStore : IImmutableArtifactStore
    {
        private readonly ConcurrentDictionary<
            (string Namespace, string Sha256),
            byte[]> artifacts = new();
        private int writes;

        public int? ThrowCollisionOnWrite { get; init; }

        public bool ReportTamperOnVerification { get; init; }

        public CancellationTokenSource? CancellationSource { get; init; }

        public int? CancelAfterWrite { get; init; }

        public int ArtifactCount => artifacts.Count;

        public IReadOnlyList<byte[]> Payloads =>
            artifacts.Values.Select(value => value.ToArray()).ToArray();

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = Interlocked.Increment(ref writes);
            if (ThrowCollisionOnWrite == write)
            {
                throw new InvalidDataException("Synthetic immutable-store collision.");
            }

            var key = (
                request.Artifact.ObjectNamespace.Value,
                request.Artifact.Content.Sha256);
            var bytes = content.ToArray();
            var existing = artifacts.GetOrAdd(key, bytes);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("Immutable-store collision.");
            }

            if (CancelAfterWrite == write)
            {
                CancellationSource!.Cancel();
            }

            return Task.FromResult(new ImmutableArtifactReceipt(
                request.Artifact,
                $"{key.Item1}/{key.Item2}",
                !ReferenceEquals(existing, bytes)));
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = artifacts[(
                artifact.ObjectNamespace.Value,
                artifact.Content.Sha256)];
            return Task.FromResult<Stream>(
                new MemoryStream(value, writable: false));
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReportTamperOnVerification)
            {
                return Task.FromResult(new ImmutableArtifactVerification(
                    false,
                    artifact,
                    artifact.Content.ByteLength,
                    new string('f', 64),
                    "Synthetic tamper."));
            }

            var bytes = artifacts[(
                artifact.ObjectNamespace.Value,
                artifact.Content.Sha256)];
            return Task.FromResult(new ImmutableArtifactVerification(
                true,
                artifact,
                bytes.LongLength,
                artifact.Content.Sha256,
                null));
        }
    }
}
