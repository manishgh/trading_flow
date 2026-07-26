using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceRetentionTests : IAsyncLifetime
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-retention-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task OldUnreferencedObservation_IsTombstonedDeletedAndNoLongerVisible()
    {
        var artifact = await RegisterObservationAsync(
            "old-observation",
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        var service = new EvidenceRetentionService(catalog, store);

        var result = await service.RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        Assert.Equal(1, result.Tombstoned);
        Assert.Equal(1, result.Deleted);
        Assert.Null(await catalog.GetSourceObservationAsync("old-observation"));
        Assert.False((await store.VerifyAsync(artifact)).IsValid);
        Assert.Equal(2L, await ScalarAsync(
            """
            SELECT state FROM retention_tombstones
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            artifact));
    }

    [Fact]
    public async Task DatasetLineageAndPin_ProtectEntireObservationClosure()
    {
        var plan = EvidenceLifecycleTestData.Plan("protected-retention");
        await catalog.RegisterCollectionPlanAsync(plan);
        var rawBytes = Encoding.UTF8.GetBytes("""{"bars":[1]}""");
        var raw = EvidenceLifecycleTestData.Artifact(rawBytes, "raw/alpaca", "application/json");
        var observation = EvidenceLifecycleTestData.Observation(
            plan,
            raw,
            "protected-observation",
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        await new EvidencePublicationCoordinator(catalog, store)
            .PublishSourceObservationAsync(observation, rawBytes);
        var partition = await EvidenceLifecycleTestData.PutAsync(
            store,
            "partition",
            "partitions/bars",
            "application/x-parquet");
        var dataset = EvidenceLifecycleTestData.Dataset(
            plan,
            partition,
            observation.ToReference(),
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        await catalog.CommitDatasetAsync(dataset);
        await catalog.PinAsync(
            new EvidencePinSubject(EvidencePinSubjectKind.Dataset, dataset.DatasetId),
            "audit hold");

        var result = await new EvidenceRetentionService(catalog, store).RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        Assert.Equal(0, result.Deleted);
        Assert.NotNull(await catalog.GetSourceObservationAsync(observation.ObservationId));
        Assert.True((await store.VerifyAsync(raw)).IsValid);
    }

    [Fact]
    public async Task IncompleteOrCorruptScan_FailsClosedWithoutTombstoning()
    {
        var artifact = await RegisterObservationAsync(
            "scan-protected",
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        var incompleteStore = new IncompleteLifecycleStore(store);

        var result = await new EvidenceRetentionService(catalog, incompleteStore).RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        Assert.Equal(0, result.Tombstoned);
        Assert.Equal(0, result.Deleted);
        Assert.True(result.Failed > 0);
        Assert.NotNull(await catalog.GetSourceObservationAsync("scan-protected"));
        Assert.True((await store.VerifyAsync(artifact)).IsValid);
    }

    [Fact]
    public async Task TombstonedAddress_CannotBeReattachedToANewCatalogOwner()
    {
        var artifact = await RegisterObservationAsync(
            "retired-observation",
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        await new EvidenceRetentionService(catalog, store).RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.RegisterAsync(new EvidenceReferenceSubjectManifest(
                new EvidencePinSubject(EvidencePinSubjectKind.AuditCase, "late-audit"),
                EvidenceLifecycleTestData.Now,
                [artifact])));
    }

    [Fact]
    public async Task CatalogVisibleExternalOwner_ProtectsOtherwiseOrphanedArtifact()
    {
        await catalog.RegisterCollectionPlanAsync(EvidenceLifecycleTestData.Plan("external-bootstrap"));
        var artifact = await EvidenceLifecycleTestData.PutAsync(
            store,
            "external-audit",
            "audit/evidence",
            "application/json");
        await catalog.RegisterAsync(new EvidenceReferenceSubjectManifest(
            new EvidencePinSubject(EvidencePinSubjectKind.AuditCase, "audit-owner"),
            EvidenceLifecycleTestData.Now.AddMonths(-25),
            [artifact]));

        var result = await new EvidenceRetentionService(catalog, store).RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        Assert.Equal(0, result.Deleted);
        Assert.True((await store.VerifyAsync(artifact)).IsValid);
    }

    [Fact]
    public async Task FailedPhysicalDelete_LeavesDurableRetryableTombstoneAndLaterRecovers()
    {
        var artifact = await RegisterObservationAsync(
            "retry-observation",
            EvidenceLifecycleTestData.Now.AddMonths(-25));
        var failOnce = new FailOnceDeleteStore(store);
        var service = new EvidenceRetentionService(catalog, failOnce);

        var failed = await service.RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);
        var recovered = await service.RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now.AddHours(2),
            10);

        Assert.Equal(1, failed.Failed);
        Assert.True(failed.Tombstoned > 0);
        Assert.Equal(1, recovered.Deleted);
        Assert.False((await store.VerifyAsync(artifact)).IsValid);
        Assert.Equal(2L, await ScalarAsync(
            """
            SELECT state FROM retention_tombstones
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            artifact));
    }

    private async Task<EvidenceArtifactReference> RegisterObservationAsync(
        string observationId,
        DateTimeOffset receivedAtUtc)
    {
        var plan = EvidenceLifecycleTestData.Plan(observationId);
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes($"{{\"id\":\"{observationId}\"}}");
        var artifact = EvidenceLifecycleTestData.Artifact(
            bytes,
            "raw/alpaca",
            "application/json");
        var observation = EvidenceLifecycleTestData.Observation(
            plan,
            artifact,
            observationId,
            receivedAtUtc);
        await new EvidencePublicationCoordinator(catalog, store)
            .PublishSourceObservationAsync(observation, bytes);
        return artifact;
    }

    private async Task<long> ScalarAsync(
        string sql,
        EvidenceArtifactReference artifact)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$namespace", artifact.ObjectNamespace.Value);
        command.Parameters.AddWithValue("$sha", artifact.Content.Sha256);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class IncompleteLifecycleStore(
        IImmutableArtifactLifecycleStore inner) : IImmutableArtifactLifecycleStore
    {
        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            inner.PutIfAbsentAsync(request, content, cancellationToken);

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactDeleteResult> DeleteIfMatchesAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.DeleteIfMatchesAsync(artifact, cancellationToken);

        public async IAsyncEnumerable<ImmutableArtifactScanEntry> ScanAsync(
            EvidenceObjectNamespace? objectNamespace = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ImmutableArtifactScanEntry(
                String.Empty,
                null,
                null,
                null,
                null,
                null,
                [
                    new ImmutableArtifactScanFinding(
                        ImmutableArtifactScanIssue.RootUnavailable,
                        "Injected incomplete scan.")
                ]);
        }
    }

    private sealed class FailOnceDeleteStore(
        IImmutableArtifactLifecycleStore inner) : IImmutableArtifactLifecycleStore
    {
        private int failuresRemaining = 1;

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            inner.PutIfAbsentAsync(request, content, cancellationToken);

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactDeleteResult> DeleteIfMatchesAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failuresRemaining, 0) == 1)
            {
                throw new IOException("Injected physical deletion failure.");
            }

            return inner.DeleteIfMatchesAsync(artifact, cancellationToken);
        }

        public IAsyncEnumerable<ImmutableArtifactScanEntry> ScanAsync(
            EvidenceObjectNamespace? objectNamespace = null,
            CancellationToken cancellationToken = default) =>
            inner.ScanAsync(objectNamespace, cancellationToken);
    }
}
