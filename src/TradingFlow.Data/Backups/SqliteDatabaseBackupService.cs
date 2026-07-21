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

            var partialPath = finalPath + $".{Guid.NewGuid():N}.partial";
            try
            {
                await Task.Run(() => CreateOnlineBackup(partialPath), cancellationToken);
                await SqliteBackupFile.ValidateIntegrityAsync(partialPath, cancellationToken);
                SqliteBackupFile.FlushToDisk(partialPath);

                var created = true;
                try
                {
                    File.Move(partialPath, finalPath, overwrite: false);
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
                SqliteBackupFile.TryDelete(partialPath);
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
