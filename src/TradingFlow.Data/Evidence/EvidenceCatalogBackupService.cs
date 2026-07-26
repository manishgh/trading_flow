using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

public sealed record EvidenceCatalogBackupManifest(
    int SchemaVersion,
    string BackupScope,
    bool ArtifactBytesIncluded,
    DateTimeOffset CreatedAtUtc,
    string DatabaseFileName,
    long DatabaseByteLength,
    string DatabaseSha256,
    int ReferencedArtifactCount,
    string ReferencedArtifactInventorySha256);

public sealed record EvidenceCatalogBackupResult(
    string BundlePath,
    string BackupPath,
    string ManifestPath,
    EvidenceCatalogBackupManifest Manifest);

/// <summary>
/// Creates and restores point-in-time, catalog-only evidence backups. A backup is accepted only
/// when the SQLite snapshot, catalog reference graph, and every visible immutable artifact
/// validate together. Immutable artifact bytes are not copied by this service and require
/// independently protected object storage.
/// </summary>
public sealed class EvidenceCatalogBackupService
{
    private const string BundleSuffix = ".catalog-only-backup";
    private const string DatabaseFileName = "catalog.sqlite";
    private const string ManifestFileName = "manifest.json";
    private const string CatalogOnlyScope = "catalog_only";
    private readonly SqliteEvidenceCatalog catalog;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly TimeProvider timeProvider;

    public EvidenceCatalogBackupService(
        SqliteEvidenceCatalog catalog,
        IImmutableArtifactStore artifactStore,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static IReadOnlyList<string> DiscoverCatalogOnlyBundles(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(
                directory,
                $"*{BundleSuffix}",
                SearchOption.TopDirectoryOnly)
            .Where(bundle =>
                File.Exists(Path.Combine(bundle, DatabaseFileName)) &&
                File.Exists(Path.Combine(bundle, ManifestFileName)))
            .OrderBy(bundle => bundle, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<EvidenceCatalogBackupResult> CreateAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var requestedPath = NormalizeDatabasePath(backupPath, nameof(backupPath));
        var bundlePath = requestedPath + BundleSuffix;
        if (Directory.Exists(bundlePath) || File.Exists(bundlePath))
        {
            throw new IOException("Evidence catalog-only backup bundle already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        var partialBundle = bundlePath + $".partial-{Guid.NewGuid():N}";
        Directory.CreateDirectory(partialBundle);
        var partialPath = Path.Combine(partialBundle, DatabaseFileName);
        var partialManifest = Path.Combine(partialBundle, ManifestFileName);
        try
        {
            await using (var source = await catalog.OpenMaintenanceConnectionAsync(cancellationToken))
            await using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = partialPath,
                             Mode = SqliteOpenMode.ReadWriteCreate,
                             Cache = SqliteCacheMode.Private,
                             Pooling = false
                         }.ToString()))
            {
                await snapshot.OpenAsync(cancellationToken);
                source.BackupDatabase(snapshot);
            }

            await FlushFileAsync(partialPath, cancellationToken);
            var inventory = await ValidateSnapshotAsync(
                partialPath,
                artifactStore,
                cancellationToken);
            var databaseLength = new FileInfo(partialPath).Length;
            var databaseHash = await ComputeSha256Async(partialPath, cancellationToken);
            var manifest = new EvidenceCatalogBackupManifest(
                EvidenceCatalogSchema.Version,
                CatalogOnlyScope,
                false,
                timeProvider.GetUtcNow(),
                DatabaseFileName,
                databaseLength,
                databaseHash,
                inventory.Count,
                InventoryHash(inventory));
            await WriteDurablyAsync(
                partialManifest,
                EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest),
                cancellationToken);

            Directory.Move(partialBundle, bundlePath);
            return new EvidenceCatalogBackupResult(
                bundlePath,
                Path.Combine(bundlePath, DatabaseFileName),
                Path.Combine(bundlePath, ManifestFileName),
                manifest);
        }
        catch
        {
            TryDeleteDirectory(partialBundle);
            throw;
        }
    }

    public async Task RestoreToEmptyAsync(
        string backupPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var backup = NormalizeDatabasePath(backupPath, nameof(backupPath));
        var destination = NormalizeDatabasePath(destinationPath, nameof(destinationPath));
        if (!File.Exists(backup))
        {
            throw new FileNotFoundException("Evidence catalog backup does not exist.", backup);
        }
        if (File.Exists(destination) ||
            File.Exists(destination + "-wal") ||
            File.Exists(destination + "-shm"))
        {
            throw new IOException("Evidence restore destination must be empty.");
        }
        if (backup.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup and restore destination must differ.");
        }

        var manifestPath = Path.Combine(
            Path.GetDirectoryName(backup)!,
            ManifestFileName);
        var manifest = await ReadAndValidateManifestAsync(
            backup,
            manifestPath,
            cancellationToken);
        var sourceInventory = await ValidateSnapshotAsync(
            backup,
            artifactStore,
            cancellationToken);
        if (sourceInventory.Count != manifest.ReferencedArtifactCount ||
            !InventoryHash(sourceInventory).Equals(
                manifest.ReferencedArtifactInventorySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Evidence backup artifact inventory does not match its sidecar.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partialPath = destination + $".restore-{Guid.NewGuid():N}.partial";
        try
        {
            await CopyDurablyAsync(backup, partialPath, cancellationToken);
            var restoredInventory = await ValidateSnapshotAsync(
                partialPath,
                artifactStore,
                cancellationToken);
            if (!InventoryHash(restoredInventory).Equals(
                    manifest.ReferencedArtifactInventorySha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Restored evidence catalog artifact inventory changed during restore.");
            }

            File.Move(partialPath, destination);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    internal static async Task<IReadOnlyList<EvidenceArtifactReference>> ValidateSnapshotAsync(
        string databasePath,
        IImmutableArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteEvidenceCatalog.ValidateCatalogIntegrityAsync(connection, cancellationToken);
        await ValidateReferenceGraphAsync(connection, cancellationToken);
        var artifacts = await ReadVisibleArtifactInventoryAsync(connection, cancellationToken);
        foreach (var artifact in artifacts)
        {
            var verification = await artifactStore.VerifyAsync(artifact, cancellationToken);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    $"Backup references missing or corrupt artifact " +
                    $"{artifact.ObjectNamespace}/{artifact.Content.Sha256}: " +
                    $"{verification.FailureReason}");
            }
        }

        return artifacts;
    }

    private static async Task ValidateReferenceGraphAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var invalidOwnerReferences = await ScalarLongAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM artifact_references AS reference
            WHERE
                (reference.owner_kind = 1 AND NOT EXISTS(
                    SELECT 1 FROM source_observations AS source
                    WHERE source.observation_id = reference.owner_id))
                OR (reference.owner_kind = 2 AND NOT EXISTS(
                    SELECT 1 FROM datasets AS dataset
                    WHERE dataset.dataset_id = reference.owner_id))
                OR (reference.owner_kind = 3 AND NOT EXISTS(
                    SELECT 1 FROM research_runs AS run
                    WHERE run.research_run_id = reference.owner_id))
                OR NOT EXISTS(
                    SELECT 1 FROM catalog_artifacts AS artifact
                    WHERE artifact.artifact_namespace = reference.artifact_namespace
                      AND artifact.artifact_sha256 = reference.artifact_sha256
                      AND artifact.status = 1);
            """,
            cancellationToken);
        if (invalidOwnerReferences != 0)
        {
            throw new InvalidDataException(
                $"Evidence catalog contains {invalidOwnerReferences} invalid owner/artifact reference(s).");
        }

        var missingDirectReferences = await ScalarLongAsync(
            connection,
            """
            SELECT
                (SELECT COUNT(*) FROM source_observations AS source
                 WHERE NOT EXISTS(
                     SELECT 1 FROM retention_tombstones AS tombstone
                     WHERE tombstone.artifact_namespace = source.artifact_namespace
                       AND tombstone.artifact_sha256 = source.artifact_sha256)
                   AND NOT EXISTS(
                     SELECT 1 FROM artifact_references AS reference
                     WHERE reference.owner_kind = 1
                       AND reference.owner_id = source.observation_id
                       AND reference.artifact_namespace = source.artifact_namespace
                       AND reference.artifact_sha256 = source.artifact_sha256))
              + (SELECT COUNT(*) FROM datasets AS dataset
                 WHERE NOT EXISTS(
                     SELECT 1 FROM artifact_references AS reference
                     WHERE reference.owner_kind = 2
                       AND reference.owner_id = dataset.dataset_id
                       AND reference.artifact_namespace = dataset.manifest_namespace
                       AND reference.artifact_sha256 = dataset.manifest_sha256))
              + (SELECT COUNT(*) FROM research_runs AS run
                 WHERE NOT EXISTS(
                     SELECT 1 FROM artifact_references AS reference
                     WHERE reference.owner_kind = 3
                       AND reference.owner_id = run.research_run_id
                       AND reference.artifact_namespace = run.manifest_namespace
                       AND reference.artifact_sha256 = run.manifest_sha256));
            """,
            cancellationToken);
        if (missingDirectReferences != 0)
        {
            throw new InvalidDataException(
                $"Evidence catalog contains {missingDirectReferences} visible owner(s) without direct artifact references.");
        }
    }

    private static async Task<IReadOnlyList<EvidenceArtifactReference>> ReadVisibleArtifactInventoryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var artifacts = new HashSet<EvidenceArtifactReference>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT artifact_namespace, artifact_sha256,
                       artifact_byte_length, artifact_media_type
                FROM catalog_artifacts
                WHERE status = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                artifacts.Add(Reference(reader));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT canonical_json FROM retention_pins;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pin = EvidenceCanonicalJson.Deserialize<EvidenceRetentionPin>(
                    Encoding.UTF8.GetBytes(reader.GetString(0)));
                artifacts.UnionWith(pin.ReferencedArtifacts);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT canonical_json FROM external_reference_subjects;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var subject = EvidenceCanonicalJson.Deserialize<EvidenceReferenceSubjectManifest>(
                    Encoding.UTF8.GetBytes(reader.GetString(0)));
                artifacts.UnionWith(subject.ReferencedArtifacts);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT artifact_namespace, artifact_sha256,
                       artifact_byte_length, artifact_media_type
                FROM artifact_publications
                WHERE state = 2;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                artifacts.Add(Reference(reader));
            }
        }

        return artifacts
            .OrderBy(value => value.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Content.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static EvidenceArtifactReference Reference(SqliteDataReader reader) =>
        new(
            new EvidenceContentAddress(
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3)),
            new EvidenceObjectNamespace(reader.GetString(0)));

    private static async Task<EvidenceCatalogBackupManifest> ReadAndValidateManifestAsync(
        string backupPath,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("Evidence backup sidecar is missing.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceCatalogBackupManifest>(bytes);
        if (manifest.SchemaVersion != EvidenceCatalogSchema.Version ||
            !manifest.BackupScope.Equals(CatalogOnlyScope, StringComparison.Ordinal) ||
            manifest.ArtifactBytesIncluded ||
            !manifest.DatabaseFileName.Equals(Path.GetFileName(backupPath), StringComparison.Ordinal) ||
            manifest.DatabaseByteLength != new FileInfo(backupPath).Length ||
            !manifest.DatabaseSha256.Equals(
                await ComputeSha256Async(backupPath, cancellationToken),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Evidence backup sidecar does not match the database file.");
        }

        return manifest;
    }

    private static string InventoryHash(
        IReadOnlyList<EvidenceArtifactReference> artifacts) =>
        EvidenceCanonicalJson.ComputeSha256(
            artifacts.Select(artifact => new
            {
                Namespace = artifact.ObjectNamespace.Value,
                artifact.Content.Sha256,
                artifact.Content.ByteLength,
                artifact.Content.MediaType
            }).ToArray());

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static string NormalizeDatabasePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.GetFullPath(path);
        if (String.IsNullOrWhiteSpace(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException("Database path requires a parent directory.", parameterName);
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static async Task WriteDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CopyDurablyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task FlushFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1,
            FileOptions.Asynchronous);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide the original backup/restore failure.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not hide the original backup failure.
        }
    }
}
