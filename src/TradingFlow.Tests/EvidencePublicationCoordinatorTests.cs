using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidencePublicationCoordinatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-publication-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidencePublicationCoordinator coordinator = null!;

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
        coordinator = new EvidencePublicationCoordinator(catalog, store);
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
    public async Task Publish_JournalsBytesInventoryAndObservation()
    {
        var plan = Plan();
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes("""{"bars":[]}""");
        var observation = Observation(plan, bytes, "observation-1", Now);

        var result = await coordinator.PublishSourceObservationAsync(observation, bytes);
        var restored = await catalog.GetSourceObservationAsync(observation.ObservationId);
        var counts = await ReadCountsAsync();

        Assert.False(result.AlreadyCommitted);
        Assert.Equal(observation.ReceivedAtUtc, restored!.ReceivedAtUtc);
        Assert.Equal((1, 1, 1), counts);
    }

    [Fact]
    public async Task CrashAfterObjectWrite_RecoversExactOriginalReceipt()
    {
        var plan = Plan();
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes("""{"bars":[{"t":"2026-07-25T12:00:00Z"}]}""");
        var observation = Observation(plan, bytes, "observation-crash", Now.AddSeconds(7));
        var crashing = new EvidencePublicationCoordinator(
            catalog,
            new FaultingArtifactStore(store, throwBeforePut: false));

        await Assert.ThrowsAsync<IOException>(() =>
            crashing.PublishSourceObservationAsync(observation, bytes));
        Assert.Null(await catalog.GetSourceObservationAsync(observation.ObservationId));

        var recovery = await coordinator.RecoverPendingPublicationsAsync(10);
        var restored = await catalog.GetSourceObservationAsync(observation.ObservationId);

        Assert.Equal(1, recovery.Recovered);
        Assert.Equal(observation.ReceivedAtUtc, restored!.ReceivedAtUtc);
        Assert.Equal(observation.Artifact, restored.Artifact);
    }

    [Fact]
    public async Task CrashBeforeObjectWrite_RemainsInvisibleAndAwaitingRequestRetry()
    {
        var plan = Plan();
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes("""{"bars":[1]}""");
        var observation = Observation(plan, bytes, "observation-no-object", Now);
        var crashing = new EvidencePublicationCoordinator(
            catalog,
            new FaultingArtifactStore(store, throwBeforePut: true));

        await Assert.ThrowsAsync<IOException>(() =>
            crashing.PublishSourceObservationAsync(observation, bytes));
        var recovery = await coordinator.RecoverPendingPublicationsAsync(10);

        Assert.Equal(1, recovery.WaitingForContent);
        Assert.Null(await catalog.GetSourceObservationAsync(observation.ObservationId));
    }

    [Fact]
    public async Task IdenticalPayloadRetries_ShareContentAndRetainDistinctReceipts()
    {
        var plan = Plan();
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes("""{"bars":[1,2,3]}""");
        var first = Observation(plan, bytes, "observation-first", Now);
        var second = Observation(plan, bytes, "observation-second", Now.AddSeconds(1));

        await coordinator.PublishSourceObservationAsync(first, bytes);
        await coordinator.PublishSourceObservationAsync(second, bytes);
        var counts = await ReadCountsAsync();

        Assert.Equal(first.Artifact, second.Artifact);
        Assert.Equal((2, 1, 2), counts);
        Assert.NotEqual(
            (await catalog.GetSourceObservationAsync(first.ObservationId))!.ReceivedAtUtc,
            (await catalog.GetSourceObservationAsync(second.ObservationId))!.ReceivedAtUtc);
    }

    private async Task<(long Publications, long Artifacts, long References)> ReadCountsAsync()
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM artifact_publications WHERE state = 3),
                (SELECT COUNT(*) FROM catalog_artifacts),
                (SELECT COUNT(*) FROM artifact_references);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static EvidenceCollectionPlan Plan() =>
        new(
            Now,
            "publication-test",
            Hash("config"),
            "commit-1",
            "collection-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "bars",
                    "alpaca",
                    "/v2/stocks/bars",
                    ["AAPL"],
                    Now.AddDays(-1),
                    Now.AddMinutes(1),
                    "sip",
                    "all",
                    "USD",
                    new DateOnly(2026, 7, 25))
            ]);

    private static EvidenceSourceObservation Observation(
        EvidenceCollectionPlan plan,
        byte[] bytes,
        string observationId,
        DateTimeOffset receivedAtUtc) =>
        new(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            "bars",
            observationId,
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            200,
            ["AAPL"],
            Now.AddDays(-1),
            Now.AddMinutes(1),
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            receivedAtUtc,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    bytes.Length,
                    "application/json"),
                new EvidenceObjectNamespace("raw/alpaca")),
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
                throw new IOException("Injected failure before immutable publication.");
            }

            await inner.PutIfAbsentAsync(request, content, cancellationToken);
            throw new IOException("Injected failure after immutable publication.");
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
