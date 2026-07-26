using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class EvidenceBackupTests : IAsyncLifetime
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-evidence-backup-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidenceSourceObservation registeredObservation = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        var plan = EvidenceLifecycleTestData.Plan("backup");
        await catalog.RegisterCollectionPlanAsync(plan);
        var bytes = Encoding.UTF8.GetBytes("""{"bars":[1,2]}""");
        var artifact = EvidenceLifecycleTestData.Artifact(
            bytes,
            "raw/alpaca",
            "application/json");
        registeredObservation = EvidenceLifecycleTestData.Observation(
            plan,
            artifact,
            "backup-observation",
            EvidenceLifecycleTestData.Now);
        await new EvidencePublicationCoordinator(catalog, store)
            .PublishSourceObservationAsync(registeredObservation, bytes);
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
    public async Task OnlineBackupAndRestore_ValidateSidecarCatalogAndArtifacts()
    {
        var service = new EvidenceCatalogBackupService(
            catalog,
            store,
            new FixedTimeProvider(EvidenceLifecycleTestData.Now));
        var backupPath = Path.Combine(rootPath, "backups", "evidence.db");
        var backup = await service.CreateAsync(backupPath);
        var restoredPath = Path.Combine(rootPath, "restore", "evidence.db");

        await service.RestoreToEmptyAsync(backup.BackupPath, restoredPath);
        var restoredCatalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(restoredPath, EvidenceCatalogOpenMode.OpenExisting),
            store);
        var restored = await restoredCatalog.GetSourceObservationAsync(
            registeredObservation.ObservationId);

        Assert.Equal(registeredObservation.Artifact, restored!.Artifact);
        Assert.Equal(1, backup.Manifest.ReferencedArtifactCount);
        Assert.Equal("catalog_only", backup.Manifest.BackupScope);
        Assert.False(backup.Manifest.ArtifactBytesIncluded);
        Assert.True(Directory.Exists(backup.BundlePath));
        Assert.True(File.Exists(backup.ManifestPath));
    }

    [Fact]
    public async Task Restore_TamperedBackupFailsBeforePublishingDestination()
    {
        var service = new EvidenceCatalogBackupService(catalog, store);
        var backup = await service.CreateAsync(
            Path.Combine(rootPath, "backups", "evidence.db"));
        await File.AppendAllTextAsync(backup.BackupPath, "tampered");
        var destination = Path.Combine(rootPath, "restore", "evidence.db");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreToEmptyAsync(backup.BackupPath, destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.Exists(Path.GetDirectoryName(destination)!)
            ? Directory.EnumerateFiles(
                Path.GetDirectoryName(destination)!,
                "*.partial",
                SearchOption.TopDirectoryOnly)
            : Array.Empty<string>());
    }

    [Fact]
    public async Task Backup_MissingReferencedArtifactFailsClosedWithoutFinalFiles()
    {
        await store.DeleteIfMatchesAsync(registeredObservation.Artifact);
        var service = new EvidenceCatalogBackupService(catalog, store);
        var backupPath = Path.Combine(rootPath, "backups", "missing.db");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateAsync(backupPath));

        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + ".manifest.json"));
    }

    [Fact]
    public async Task Backup_InvalidOwnerReferenceGraphIsRejected()
    {
        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(rootPath, "evidence.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM artifact_references
                WHERE owner_kind = 1 AND owner_id = $owner;
                """;
            command.Parameters.AddWithValue("$owner", registeredObservation.ObservationId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var backupPath = Path.Combine(rootPath, "backups", "invalid-reference.db");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EvidenceCatalogBackupService(catalog, store).CreateAsync(backupPath));
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public async Task Restore_RefusesNonEmptyDestination()
    {
        var service = new EvidenceCatalogBackupService(catalog, store);
        var backup = await service.CreateAsync(
            Path.Combine(rootPath, "backups", "evidence.db"));
        var destination = Path.Combine(rootPath, "restore", "evidence.db");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "existing");

        await Assert.ThrowsAsync<IOException>(() =>
            service.RestoreToEmptyAsync(backup.BackupPath, destination));
        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Backup_DiscoveryPublishesOnlyCompleteAtomicBundles()
    {
        var backupDirectory = Path.Combine(rootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        var strandedPartial = Path.Combine(
            backupDirectory,
            "crashed.db.catalog-only-backup.partial-deadbeef");
        Directory.CreateDirectory(strandedPartial);
        await File.WriteAllTextAsync(
            Path.Combine(strandedPartial, "catalog.sqlite"),
            "partial");

        var backup = await new EvidenceCatalogBackupService(catalog, store)
            .CreateAsync(Path.Combine(backupDirectory, "complete.db"));
        var discovered = EvidenceCatalogBackupService.DiscoverCatalogOnlyBundles(
            backupDirectory);

        Assert.Equal([backup.BundlePath], discovered);
        Assert.DoesNotContain(strandedPartial, discovered);
    }

    [Fact]
    public async Task CatalogOnlyBackup_RestoreFailsClosedWhenArtifactStorageWasLost()
    {
        var service = new EvidenceCatalogBackupService(catalog, store);
        var backup = await service.CreateAsync(
            Path.Combine(rootPath, "backups", "catalog-only.db"));
        await store.DeleteIfMatchesAsync(registeredObservation.Artifact);
        var destination = Path.Combine(rootPath, "restore", "evidence.db");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreToEmptyAsync(backup.BackupPath, destination));

        Assert.False(File.Exists(destination));
        Assert.True(Directory.Exists(backup.BundlePath));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
