using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class EvidenceOrphanTests : IAsyncLifetime
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-orphan-tests",
        Guid.NewGuid().ToString("N"));
    private string objectRoot = null!;
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        objectRoot = Path.Combine(rootPath, "objects");
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(objectRoot));
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
    public async Task MalformedCandidate_IsRecordedAndDoesNotAbortLaterValidCandidate()
    {
        await catalog.RegisterCollectionPlanAsync(EvidenceLifecycleTestData.Plan("bootstrap"));
        var valid = await EvidenceLifecycleTestData.PutAsync(
            store,
            "unowned",
            "raw/alpaca",
            "application/json");
        CreateMalformedCandidate();

        var result = await new EvidenceOrphanReconciler(catalog, store)
            .ReconcileAsync(EvidenceLifecycleTestData.Now);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ValidOrphans);
        Assert.Equal(1, result.MalformedCandidates);
        Assert.Contains(result.Findings, entry => !entry.IsValid);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM orphan_artifacts;"));
        Assert.True((await store.VerifyAsync(valid)).IsValid);
    }

    [Fact]
    public async Task CompleteReconciliation_AllowsOnlyAgedValidOrphanDeletion()
    {
        await catalog.RegisterCollectionPlanAsync(EvidenceLifecycleTestData.Plan("bootstrap"));
        var orphan = await EvidenceLifecycleTestData.PutAsync(
            store,
            "old-unowned",
            "raw/alpaca",
            "application/json");
        var reconciler = new EvidenceOrphanReconciler(catalog, store);
        Assert.True((await reconciler.ReconcileAsync(EvidenceLifecycleTestData.Now)).IsComplete);
        await ExecuteAsync(
            """
            UPDATE orphan_artifacts
            SET first_seen_at_utc = '2024-01-01T00:00:00.0000000+00:00';
            """);

        var result = await new EvidenceRetentionService(catalog, store).RunAsync(
            new EvidenceRetentionPolicy(24, 24),
            EvidenceLifecycleTestData.Now,
            10);

        Assert.Equal(1, result.Deleted);
        Assert.False((await store.VerifyAsync(orphan)).IsValid);
    }

    private void CreateMalformedCandidate()
    {
        var hash = new string('a', 64);
        var directory = Path.Combine(
            objectRoot,
            "raw",
            "alpaca",
            "aa",
            hash);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "metadata.json"), "{broken");
        File.WriteAllText(Path.Combine(directory, "content.bin"), "malformed");
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
