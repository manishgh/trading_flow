using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

public sealed record EvidenceOrphanReconciliationResult(
    bool IsComplete,
    int Scanned,
    int ValidOrphans,
    int MalformedCandidates,
    int CatalogOwned,
    IReadOnlyList<ImmutableArtifactScanEntry> Findings);

internal enum EvidenceOrphanClassification
{
    UnreferencedValidObject = 1,
    MalformedObject = 2
}

internal enum EvidenceOrphanState
{
    Observed = 1,
    Tombstoned = 2,
    Deleted = 3
}

/// <summary>
/// Reconciles physical immutable objects with catalog ownership. It records every
/// independently identifiable malformed candidate and continues scanning, while any
/// scan finding makes the overall result incomplete so retention cannot delete.
/// </summary>
public sealed class EvidenceOrphanReconciler
{
    private readonly SqliteEvidenceCatalog catalog;
    private readonly IImmutableArtifactMaintenanceScanner scanner;

    public EvidenceOrphanReconciler(
        SqliteEvidenceCatalog catalog,
        IImmutableArtifactMaintenanceScanner scanner)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public async Task<EvidenceOrphanReconciliationResult> ReconcileAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureUtc(observedAtUtc, nameof(observedAtUtc));
        var entries = new List<ImmutableArtifactScanEntry>();
        await foreach (var entry in scanner.ScanAsync(cancellationToken: cancellationToken))
        {
            entries.Add(entry);
        }

        var findings = entries.Where(entry => !entry.IsValid).ToArray();
        var validOrphans = 0;
        var malformed = 0;
        var owned = 0;
        await using var connection = await catalog.OpenMaintenanceConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var externallyReferenced = await ReadExternalReferencesAsync(
            connection,
            transaction,
            cancellationToken);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = ResolveIdentity(entry);
            if (identity is null)
            {
                continue;
            }

            if (entry.IsValid &&
                await ReactivateDeletedTombstoneAsync(
                    connection,
                    transaction,
                    identity.Value.Namespace,
                    identity.Value.Sha256,
                    observedAtUtc,
                    cancellationToken))
            {
                owned++;
                continue;
            }

            if (await IsCatalogOwnedAsync(
                    connection,
                    transaction,
                    identity.Value.Namespace,
                    identity.Value.Sha256,
                    externallyReferenced,
                    cancellationToken))
            {
                owned++;
                await DeleteOrphanRecordAsync(
                    connection,
                    transaction,
                    identity.Value.Namespace,
                    identity.Value.Sha256,
                    cancellationToken);
                continue;
            }

            var artifact = entry.DeclaredArtifact;
            var byteLength = artifact?.Content.ByteLength ?? entry.ActualByteLength ?? 0;
            var mediaType = artifact?.Content.MediaType ?? "application/octet-stream";
            var classification = entry.IsValid
                ? EvidenceOrphanClassification.UnreferencedValidObject
                : EvidenceOrphanClassification.MalformedObject;
            await UpsertOrphanAsync(
                connection,
                transaction,
                identity.Value.Namespace,
                identity.Value.Sha256,
                byteLength,
                mediaType,
                observedAtUtc,
                classification,
                entry.IsValid ? null : FormatFindings(entry),
                cancellationToken);
            if (entry.IsValid)
            {
                validOrphans++;
            }
            else
            {
                malformed++;
            }
        }

        transaction.Commit();
        return new EvidenceOrphanReconciliationResult(
            findings.Length == 0,
            entries.Count,
            validOrphans,
            malformed,
            owned,
            findings);
    }

    internal static async Task<HashSet<(string Namespace, string Sha256)>> ReadExternalReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var references = new HashSet<(string Namespace, string Sha256)>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_json FROM external_reference_subjects;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceReferenceSubjectManifest>(
                Encoding.UTF8.GetBytes(reader.GetString(0)));
            foreach (var artifact in manifest.ReferencedArtifacts)
            {
                references.Add((
                    artifact.ObjectNamespace.Value,
                    artifact.Content.Sha256));
            }
        }

        return references;
    }

    private static (string Namespace, string Sha256)? ResolveIdentity(
        ImmutableArtifactScanEntry entry)
    {
        if (entry.DeclaredArtifact is not null)
        {
            return (
                entry.DeclaredArtifact.ObjectNamespace.Value,
                entry.DeclaredArtifact.Content.Sha256);
        }

        if (!String.IsNullOrWhiteSpace(entry.PhysicalNamespace) &&
            IsSha256(entry.PhysicalSha256))
        {
            return (entry.PhysicalNamespace, entry.PhysicalSha256!);
        }

        return null;
    }

    private static async Task<bool> IsCatalogOwnedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectNamespace,
        string sha256,
        IReadOnlySet<(string Namespace, string Sha256)> externalReferences,
        CancellationToken cancellationToken)
    {
        if (externalReferences.Contains((objectNamespace, sha256)))
        {
            return true;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                EXISTS(
                    SELECT 1 FROM catalog_artifacts
                    WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
                )
                OR EXISTS(
                    SELECT 1 FROM artifact_publications
                    WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
                )
                OR EXISTS(
                    SELECT 1 FROM retention_pin_artifacts
                    WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
                )
                OR EXISTS(
                    SELECT 1 FROM retention_tombstones
                    WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
                );
            """;
        command.Parameters.AddWithValue("$namespace", objectNamespace);
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> ReactivateDeletedTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectNamespace,
        string sha256,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE retention_tombstones
            SET state = $prepared,
                updated_at_utc = $observed,
                deleted_at_utc = NULL,
                next_retry_at_utc = NULL,
                last_error = 'Exact retired content reappeared and was scheduled for deletion.'
            WHERE artifact_namespace = $namespace
              AND artifact_sha256 = $sha
              AND state = $deleted;
            """;
        command.Parameters.AddWithValue("$prepared", (int)EvidenceTombstoneState.Prepared);
        command.Parameters.AddWithValue("$observed", Utc(observedAtUtc));
        command.Parameters.AddWithValue("$namespace", objectNamespace);
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$deleted", (int)EvidenceTombstoneState.Deleted);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task UpsertOrphanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectNamespace,
        string sha256,
        long byteLength,
        string mediaType,
        DateTimeOffset observedAtUtc,
        EvidenceOrphanClassification classification,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO orphan_artifacts(
                artifact_namespace, artifact_sha256, artifact_byte_length,
                artifact_media_type, first_seen_at_utc, last_seen_at_utc,
                classification, state, attempt_count, last_error)
            VALUES(
                $namespace, $sha, $length, $media, $observed, $observed,
                $classification, $state, 0, $error)
            ON CONFLICT(artifact_namespace, artifact_sha256) DO UPDATE SET
                last_seen_at_utc = excluded.last_seen_at_utc,
                classification = excluded.classification,
                last_error = excluded.last_error;
            """;
        command.Parameters.AddWithValue("$namespace", objectNamespace);
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$length", byteLength);
        command.Parameters.AddWithValue("$media", mediaType);
        command.Parameters.AddWithValue("$observed", Utc(observedAtUtc));
        command.Parameters.AddWithValue("$classification", (int)classification);
        command.Parameters.AddWithValue("$state", (int)EvidenceOrphanState.Observed);
        command.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteOrphanRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string objectNamespace,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM orphan_artifacts
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """;
        command.Parameters.AddWithValue("$namespace", objectNamespace);
        command.Parameters.AddWithValue("$sha", sha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatFindings(ImmutableArtifactScanEntry entry) =>
        String.Join(
            " | ",
            entry.Findings.Select(finding => $"{finding.Code}: {finding.Detail}"));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Utc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}
