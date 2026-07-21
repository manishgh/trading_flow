namespace TradingFlow.Data.Backups;

public sealed record SqliteRestoreResult(
    string BackupPath,
    string RestoredDatabasePath,
    string Sha256,
    long SizeBytes);

/// <summary>
/// Validates a retained backup and restores it atomically to an empty destination.
/// </summary>
public sealed class SqliteDatabaseRestoreService
{
    public async Task<SqliteRestoreResult> RestoreToEmptyAsync(
        string backupPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (!File.Exists(fullBackupPath))
        {
            throw new FileNotFoundException("The SQLite backup does not exist.", fullBackupPath);
        }

        if (string.Equals(fullBackupPath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup and restore destination must be different files.");
        }

        EnsureDestinationIsEmpty(fullDestinationPath);
        await SqliteBackupFile.ValidateIntegrityAsync(fullBackupPath, cancellationToken);
        var backupHash = await SqliteBackupFile.ComputeSha256Async(fullBackupPath, cancellationToken);
        var backupSize = new FileInfo(fullBackupPath).Length;
        await SqliteBackupFile.ValidateManifestAsync(
            fullBackupPath,
            fullBackupPath + ".manifest.json",
            ResolveOperationalDate(fullBackupPath),
            backupHash,
            backupSize,
            cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
        var partialPath = fullDestinationPath + $".{Guid.NewGuid():N}.partial";
        try
        {
            await SqliteBackupFile.CopyDurablyAsync(fullBackupPath, partialPath, cancellationToken);
            await SqliteBackupFile.ValidateIntegrityAsync(partialPath, cancellationToken);
            File.Move(partialPath, fullDestinationPath, overwrite: false);
        }
        finally
        {
            SqliteBackupFile.TryDelete(partialPath);
        }

        return new SqliteRestoreResult(
            fullBackupPath,
            fullDestinationPath,
            backupHash,
            backupSize);
    }

    private static void EnsureDestinationIsEmpty(string destinationPath)
    {
        var existingFiles = new[] { destinationPath, destinationPath + "-wal", destinationPath + "-shm" }
            .Where(File.Exists)
            .ToArray();
        if (existingFiles.Length > 0)
        {
            throw new IOException(
                "Restore destination must be empty. Existing file(s): " + string.Join(", ", existingFiles));
        }
    }

    private static DateOnly ResolveOperationalDate(string backupPath)
    {
        var manifestPath = backupPath + ".manifest.json";
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Backup manifest is missing: {manifestPath}");
        }

        const string prefix = "tradingflow-";
        const string suffix = ".db";
        var fileName = Path.GetFileName(backupPath);
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || !DateOnly.TryParseExact(
                fileName[prefix.Length..^suffix.Length],
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var operationalDate))
        {
            throw new InvalidDataException(
                $"Backup filename must use tradingflow-yyyy-MM-dd.db: {fileName}");
        }

        return operationalDate;
    }
}
