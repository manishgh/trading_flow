using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Backups;

public sealed record SqliteBackupResult(
    DateOnly OperationalDate,
    string BackupPath,
    string ManifestPath,
    string Sha256,
    long SizeBytes,
    bool Created);

public sealed record SqliteBackupManifest(
    int SchemaVersion,
    DateOnly OperationalDate,
    DateTimeOffset CreatedAtUtc,
    string DatabaseFileName,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Creates immutable, integrity-checked online backups of the active SQLite journal.
/// </summary>
public sealed class SqliteDatabaseBackupService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string databasePath;
    private readonly string backupRoot;
    private readonly IArtifactWriter artifactWriter;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim backupGate = new(1, 1);

    public SqliteDatabaseBackupService(
        string databasePath,
        string backupRoot,
        IArtifactWriter artifactWriter,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        this.databasePath = Path.GetFullPath(databasePath);
        this.backupRoot = Path.GetFullPath(backupRoot);
        this.artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates and verifies SQLite locally, then publishes only closed immutable bytes to the configured backup destination.</summary>
    public async Task<SqliteBackupResult> CreateDailyBackupAsync(
        DateOnly operationalDate,
        CancellationToken cancellationToken = default)
    {
        await backupGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException("The SQLite journal does not exist.", databasePath);
            }

            var finalPath = ResolveBackupPath(operationalDate);
            var manifestPath = finalPath + ".manifest.json";
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(finalPath))
            {
                return await ValidateExistingOrRecoverManifestAsync(
                    operationalDate,
                    finalPath,
                    manifestPath,
                    cancellationToken);
            }

            // SQLite writes only beside the local operational database. A separate backup volume receives closed bytes.
            var stagingRoot = Path.Combine(Path.GetDirectoryName(databasePath)!, ".backup-staging");
            Directory.CreateDirectory(stagingRoot);
            var partialPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.db.partial");
            try
            {
                await Task.Run(() => CreateOnlineBackup(partialPath), cancellationToken);
                await SqliteBackupFile.ValidateIntegrityAsync(partialPath, cancellationToken);
                RequireStandaloneBackup(partialPath);
                SqliteBackupFile.FlushToDisk(partialPath);

                var created = true;
                try
                {
                    await artifactWriter.WriteStreamExclusiveAsync(finalPath, async (destination, token) =>
                    {
                        // The online backup connection is closed and the local copy has already passed integrity checks.
                        await using var source = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 16 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await source.CopyToAsync(destination, token);
                    }, cancellationToken);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    created = false;
                }

                return created
                    ? await ValidateAndWriteManifestAsync(
                        operationalDate,
                        finalPath,
                        manifestPath,
                        created: true,
                        cancellationToken)
                    : await ValidateExistingOrRecoverManifestAsync(
                        operationalDate,
                        finalPath,
                        manifestPath,
                        cancellationToken);
            }
            finally
            {
                DeleteStagedBackup(partialPath);
            }
        }
        finally
        {
            backupGate.Release();
        }
    }

    public string ResolveBackupPath(DateOnly operationalDate) =>
        Path.Combine(
            backupRoot,
            operationalDate.Year.ToString("0000", CultureInfo.InvariantCulture),
            operationalDate.Month.ToString("00", CultureInfo.InvariantCulture),
            $"tradingflow-{operationalDate:yyyy-MM-dd}.db");

    /// <summary>Copies the online database locally and converts the copied WAL header into a standalone DELETE-journal snapshot before closing it.</summary>
    private void CreateOnlineBackup(string destinationPath)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        // SQLite's backup API copies page-one WAL settings too. Only this private
        // destination changes mode; the operational source remains in WAL mode.
        using var mode = destination.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!String.Equals(Convert.ToString(mode.ExecuteScalar(), CultureInfo.InvariantCulture), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local backup could not be converted into a standalone database file.");
    }

    /// <summary>Refuses publication when the closed local snapshot still depends on an auxiliary SQLite journal.</summary>
    private static void RequireStandaloneBackup(string path)
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix)))
            throw new InvalidDataException("The closed backup still has a SQLite journal; standalone bytes cannot be published.");
    }

    /// <summary>Deletes only this attempt's random staging filename and its exact SQLite companions after all connections close.</summary>
    private static void DeleteStagedBackup(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal", String.Empty })
            SqliteBackupFile.TryDelete(path + suffix);
    }

    private async Task<SqliteBackupResult> ValidateAndWriteManifestAsync(
        DateOnly operationalDate,
        string backupPath,
        string manifestPath,
        bool created,
        CancellationToken cancellationToken)
    {
        await SqliteBackupFile.ValidateIntegrityAsync(backupPath, cancellationToken);
        var hash = await SqliteBackupFile.ComputeSha256Async(backupPath, cancellationToken);
        var size = new FileInfo(backupPath).Length;
        var manifest = new SqliteBackupManifest(
            SchemaVersion: 1,
            OperationalDate: operationalDate,
            CreatedAtUtc: timeProvider.GetUtcNow(),
            DatabaseFileName: Path.GetFileName(backupPath),
            SizeBytes: size,
            Sha256: hash);
        await artifactWriter.WriteTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            cancellationToken);
        return new SqliteBackupResult(
            operationalDate,
            backupPath,
            manifestPath,
            hash,
            size,
            created);
    }

    private async Task<SqliteBackupResult> ValidateExistingOrRecoverManifestAsync(
        DateOnly operationalDate,
        string backupPath,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await SqliteBackupFile.ValidateIntegrityAsync(backupPath, cancellationToken);
        var hash = await SqliteBackupFile.ComputeSha256Async(backupPath, cancellationToken);
        var size = new FileInfo(backupPath).Length;
        if (File.Exists(manifestPath))
        {
            await SqliteBackupFile.ValidateManifestAsync(
                backupPath,
                manifestPath,
                operationalDate,
                hash,
                size,
                cancellationToken);
            return new SqliteBackupResult(
                operationalDate,
                backupPath,
                manifestPath,
                hash,
                size,
                Created: false);
        }

        // A process can stop after the database rename but before the sidecar publish.
        // Reconstruct only the missing sidecar; an existing sidecar is never rewritten.
        return await ValidateAndWriteManifestAsync(
            operationalDate,
            backupPath,
            manifestPath,
            created: false,
            cancellationToken);
    }
}
