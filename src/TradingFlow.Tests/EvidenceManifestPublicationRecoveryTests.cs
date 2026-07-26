using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceManifestPublicationRecoveryTests : IAsyncLifetime
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-manifest-recovery-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidenceCollectionPlan plan = null!;
    private EvidenceDatasetManifest dataset = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        catalog = OpenCatalog(EvidenceCatalogOpenMode.BootstrapNew, store);
        plan = EvidenceLifecycleTestData.Plan("manifest-recovery");
        await catalog.RegisterCollectionPlanAsync(plan);
        var rawBytes = Encoding.UTF8.GetBytes("""{"bars":[1]}""");
        var raw = EvidenceLifecycleTestData.Artifact(
            rawBytes,
            "raw/alpaca",
            "application/json");
        var observation = EvidenceLifecycleTestData.Observation(
            plan,
            raw,
            "manifest-observation",
            EvidenceLifecycleTestData.Now);
        await new EvidencePublicationCoordinator(catalog, store)
            .PublishSourceObservationAsync(observation, rawBytes);
        var partition = await EvidenceLifecycleTestData.PutAsync(
            store,
            "partition",
            "partitions/bars",
            "application/x-parquet");
        dataset = EvidenceLifecycleTestData.Dataset(
            plan,
            partition,
            observation.ToReference(),
            EvidenceLifecycleTestData.Now.AddMinutes(1));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatasetManifestPublication_RecoversAccordingToCrashBoundary(
        bool throwBeforePut)
    {
        var crashingCatalog = OpenCatalog(
            EvidenceCatalogOpenMode.OpenExisting,
            new FaultingArtifactStore(store, throwBeforePut));

        await Assert.ThrowsAsync<IOException>(() =>
            crashingCatalog.CommitDatasetAsync(dataset));
        Assert.Null(await catalog.GetDatasetAsync(dataset.DatasetId));

        var recovery = await new EvidencePublicationCoordinator(catalog, store)
            .RecoverPendingPublicationsAsync(10);

        if (throwBeforePut)
        {
            Assert.Equal(1, recovery.WaitingForContent);
            Assert.Null(await catalog.GetDatasetAsync(dataset.DatasetId));
        }
        else
        {
            Assert.Equal(1, recovery.Recovered);
            Assert.Equal(dataset.DatasetId, (await catalog.GetDatasetAsync(dataset.DatasetId))!.DatasetId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResearchManifestPublication_RecoversAccordingToCrashBoundary(
        bool throwBeforePut)
    {
        await catalog.CommitDatasetAsync(dataset);
        var research = await EvidenceLifecycleTestData.ResearchRunAsync(store, dataset);
        var crashingCatalog = OpenCatalog(
            EvidenceCatalogOpenMode.OpenExisting,
            new FaultingArtifactStore(store, throwBeforePut));

        await Assert.ThrowsAsync<IOException>(() =>
            crashingCatalog.RegisterResearchRunAsync(research));
        Assert.Null(await catalog.GetResearchRunAsync(research.ResearchRunId));

        var recovery = await new EvidencePublicationCoordinator(catalog, store)
            .RecoverPendingPublicationsAsync(10);

        if (throwBeforePut)
        {
            Assert.Equal(1, recovery.WaitingForContent);
            Assert.Null(await catalog.GetResearchRunAsync(research.ResearchRunId));
        }
        else
        {
            Assert.Equal(1, recovery.Recovered);
            Assert.Equal(
                research.ResearchRunId,
                (await catalog.GetResearchRunAsync(research.ResearchRunId))!.ResearchRunId);
        }
    }

    private SqliteEvidenceCatalog OpenCatalog(
        EvidenceCatalogOpenMode mode,
        IImmutableArtifactStore artifactStore) =>
        new(
            new EvidenceCatalogOptions(Path.Combine(rootPath, "evidence.db"), mode),
            artifactStore);

    private sealed class FaultingArtifactStore(
        IImmutableArtifactStore inner,
        bool throwBeforePut) : IImmutableArtifactStore
    {
        public async Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            if (throwBeforePut)
            {
                throw new IOException("Injected failure before manifest publication.");
            }

            await inner.PutIfAbsentAsync(request, content, cancellationToken);
            throw new IOException("Injected failure after manifest publication.");
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(artifact, cancellationToken);
    }
}
