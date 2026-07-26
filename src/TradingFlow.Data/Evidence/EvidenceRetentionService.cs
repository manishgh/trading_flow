using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;

namespace TradingFlow.Data.Evidence;

internal enum EvidenceTombstoneState
{
    Prepared = 1,
    Deleted = 2,
    RetryableFailure = 3
}

internal sealed record RetentionCandidate(
    EvidenceArtifactReference Artifact,
    DateTimeOffset PolicyCutoffUtc);

/// <summary>
/// Applies retention to complete owner groups, never individual references. Committed
/// datasets and research runs are indefinite; only old observation groups with no
/// dataset/quarantine/pin/external dependency and aged physical orphans can retire.
/// </summary>
public sealed class EvidenceRetentionService : IEvidenceRetentionService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    private readonly SqliteEvidenceCatalog catalog;
    private readonly IImmutableArtifactLifecycleStore artifactStore;
    private readonly EvidenceOrphanReconciler reconciler;

    public EvidenceRetentionService(
        SqliteEvidenceCatalog catalog,
        IImmutableArtifactLifecycleStore artifactStore)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        reconciler = new EvidenceOrphanReconciler(catalog, artifactStore);
    }

    public async Task<EvidenceRetentionRunResult> RunAsync(
        EvidenceRetentionPolicy policy,
        DateTimeOffset evaluatedAtUtc,
        int maximumObjects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        if (maximumObjects <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));
        }

        var scan = await reconciler.ReconcileAsync(evaluatedAtUtc, cancellationToken);
        if (!scan.IsComplete)
        {
            return new EvidenceRetentionRunResult(
                scan.Scanned,
                0,
                0,
                Math.Max(1, scan.Findings.Count));
        }

        var candidates = await PrepareCandidatesAsync(
            policy,
            evaluatedAtUtc,
            maximumObjects,
            cancellationToken);
        var tombstoned = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var prepared = await PrepareTombstoneAsync(
                    candidate,
                    evaluatedAtUtc,
                    cancellationToken);
                if (!prepared)
                {
                    continue;
                }

                tombstoned++;
                await artifactStore.DeleteIfMatchesAsync(candidate.Artifact, cancellationToken);
                await MarkDeletedAsync(candidate.Artifact, evaluatedAtUtc, cancellationToken);
                deleted++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                await MarkFailureAsync(
                    candidate.Artifact,
                    exception.Message,
                    evaluatedAtUtc,
                    cancellationToken);
            }
        }

        return new EvidenceRetentionRunResult(
            candidates.Count,
            tombstoned,
            deleted,
            failed);
    }

    private async Task<IReadOnlyList<RetentionCandidate>> PrepareCandidatesAsync(
        EvidenceRetentionPolicy policy,
        DateTimeOffset evaluatedAtUtc,
        int maximumObjects,
        CancellationToken cancellationToken)
    {
        var rawCutoff = evaluatedAtUtc.AddMonths(-policy.RawObservationMonths);
        var normalizedCutoff = evaluatedAtUtc.AddMonths(-policy.NormalizedDatasetMonths);
        await using var connection = await catalog.OpenMaintenanceConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);

        var protectedObservationIds = await ReadDatasetAndQuarantineObservationIdsAsync(
            connection,
            transaction,
            cancellationToken);
        var externalReferences = await EvidenceOrphanReconciler.ReadExternalReferencesAsync(
            connection,
            transaction,
            cancellationToken);
        var sourceCandidates = await ReadSourceCandidatesAsync(
            connection,
            transaction,
            rawCutoff,
            protectedObservationIds,
            externalReferences,
            cancellationToken);
        var orphanCandidates = await ReadOrphanCandidatesAsync(
            connection,
            transaction,
            rawCutoff,
            normalizedCutoff,
            evaluatedAtUtc,
            externalReferences,
            cancellationToken);
        var retryCandidates = await ReadRetryCandidatesAsync(
            connection,
            transaction,
            evaluatedAtUtc,
            externalReferences,
            cancellationToken);

        var candidates = retryCandidates
            .Concat(sourceCandidates)
            .Concat(orphanCandidates)
            .GroupBy(
                value => (
                    value.Artifact.ObjectNamespace.Value,
                    value.Artifact.Content.Sha256))
            .Select(group => group.First())
            .OrderBy(value => value.PolicyCutoffUtc)
            .ThenBy(value => value.Artifact.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Artifact.Content.Sha256, StringComparer.Ordinal)
            .Take(maximumObjects)
            .ToArray();
        transaction.Commit();
        return candidates;
    }

    private static async Task<HashSet<string>> ReadDatasetAndQuarantineObservationIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT canonical_json FROM datasets;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var dataset = EvidenceCanonicalJson.Deserialize<EvidenceDatasetManifest>(
                    Encoding.UTF8.GetBytes(reader.GetString(0)));
                foreach (var source in dataset.Partitions.SelectMany(
                             partition => partition.SourceObservations))
                {
                    ids.Add(source.ObservationId);
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT canonical_json FROM quarantines;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var quarantine = EvidenceCanonicalJson.Deserialize<EvidenceQuarantineRecord>(
                    Encoding.UTF8.GetBytes(reader.GetString(0)));
                foreach (var source in quarantine.Lineage.Sources)
                {
                    ids.Add(source.ObservationId);
                }
            }
        }

        return ids;
    }

    private static async Task<IReadOnlyList<RetentionCandidate>> ReadSourceCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset cutoffUtc,
        IReadOnlySet<string> protectedObservationIds,
        IReadOnlySet<(string Namespace, string Sha256)> externalReferences,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(string Namespace, string Sha256), List<SourceOwner>>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT observation_id, artifact_namespace, artifact_sha256,
                       received_at_utc, canonical_json
                FROM source_observations;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var observation = EvidenceCanonicalJson.Deserialize<EvidenceSourceObservation>(
                    Encoding.UTF8.GetBytes(reader.GetString(4)));
                var key = (reader.GetString(1), reader.GetString(2));
                if (!groups.TryGetValue(key, out var owners))
                {
                    owners = [];
                    groups.Add(key, owners);
                }

                owners.Add(new SourceOwner(
                    reader.GetString(0),
                    DateTimeOffset.Parse(
                        reader.GetString(3),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    observation.Artifact));
            }
        }

        var candidates = new List<RetentionCandidate>();
        foreach (var (identity, owners) in groups)
        {
            if (owners.Any(owner =>
                    owner.ReceivedAtUtc >= cutoffUtc ||
                    protectedObservationIds.Contains(owner.ObservationId)) ||
                externalReferences.Contains(identity) ||
                await HasPinAsync(connection, transaction, identity, cancellationToken) ||
                await HasBlockingReferenceAsync(
                    connection,
                    transaction,
                    identity,
                    owners.Select(owner => owner.ObservationId).ToHashSet(StringComparer.Ordinal),
                    cancellationToken))
            {
                continue;
            }

            candidates.Add(new RetentionCandidate(owners[0].Artifact, cutoffUtc));
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<RetentionCandidate>> ReadOrphanCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset rawCutoffUtc,
        DateTimeOffset normalizedCutoffUtc,
        DateTimeOffset scanTimeUtc,
        IReadOnlySet<(string Namespace, string Sha256)> externalReferences,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RetentionCandidate>();
        var rows = new List<(string Namespace, string Sha256, long Length, string Media, string FirstSeen)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT artifact_namespace, artifact_sha256, artifact_byte_length,
                       artifact_media_type, first_seen_at_utc
                FROM orphan_artifacts
                WHERE classification = $valid
                  AND state = $observed
                  AND last_seen_at_utc = $scanTime;
                """;
            command.Parameters.AddWithValue(
                "$valid",
                (int)EvidenceOrphanClassification.UnreferencedValidObject);
            command.Parameters.AddWithValue("$observed", (int)EvidenceOrphanState.Observed);
            command.Parameters.AddWithValue("$scanTime", Utc(scanTimeUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            var objectNamespace = row.Namespace;
            var sha256 = row.Sha256;
            var firstSeen = DateTimeOffset.Parse(
                row.FirstSeen,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            var cutoff = objectNamespace.StartsWith("raw/", StringComparison.Ordinal)
                ? rawCutoffUtc
                : normalizedCutoffUtc;
            var identity = (objectNamespace, sha256);
            if (firstSeen >= cutoff ||
                externalReferences.Contains(identity) ||
                await HasPinAsync(connection, transaction, identity, cancellationToken))
            {
                continue;
            }

            candidates.Add(new RetentionCandidate(
                new EvidenceArtifactReference(
                    new EvidenceContentAddress(
                        sha256,
                        row.Length,
                        row.Media),
                    new EvidenceObjectNamespace(objectNamespace)),
                cutoff));
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<RetentionCandidate>> ReadRetryCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset evaluatedAtUtc,
        IReadOnlySet<(string Namespace, string Sha256)> externalReferences,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RetentionCandidate>();
        var rows = new List<(string Namespace, string Sha256, long Length, string Media, string Cutoff)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT artifact_namespace, artifact_sha256, artifact_byte_length,
                       artifact_media_type, policy_cutoff_utc
                FROM retention_tombstones
                WHERE state IN ($prepared, $failed)
                  AND (next_retry_at_utc IS NULL OR next_retry_at_utc <= $evaluated);
                """;
            command.Parameters.AddWithValue("$prepared", (int)EvidenceTombstoneState.Prepared);
            command.Parameters.AddWithValue("$failed", (int)EvidenceTombstoneState.RetryableFailure);
            command.Parameters.AddWithValue("$evaluated", Utc(evaluatedAtUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            var identity = (row.Namespace, row.Sha256);
            if (externalReferences.Contains(identity) ||
                await HasPinAsync(connection, transaction, identity, cancellationToken))
            {
                continue;
            }

            candidates.Add(new RetentionCandidate(
                new EvidenceArtifactReference(
                    new EvidenceContentAddress(
                        identity.Item2,
                        row.Length,
                        row.Media),
                    new EvidenceObjectNamespace(identity.Item1)),
                DateTimeOffset.Parse(
                    row.Cutoff,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)));
        }

        return candidates;
    }

    private async Task<bool> PrepareTombstoneAsync(
        RetentionCandidate candidate,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await catalog.OpenMaintenanceConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var identity = (
            candidate.Artifact.ObjectNamespace.Value,
            candidate.Artifact.Content.Sha256);
        if (await HasPinAsync(connection, transaction, identity, cancellationToken) ||
            (await EvidenceOrphanReconciler.ReadExternalReferencesAsync(
                connection,
                transaction,
                cancellationToken)).Contains(identity))
        {
            transaction.Commit();
            return false;
        }

        var existingState = await ScalarLongAsync(
            connection,
            transaction,
            """
            SELECT state FROM retention_tombstones
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            cancellationToken,
            identity);
        if (existingState == (long)EvidenceTombstoneState.Deleted)
        {
            transaction.Commit();
            return false;
        }

        if (existingState is null &&
            !await IsStillEligibleAsync(
                connection,
                transaction,
                candidate,
                cancellationToken))
        {
            transaction.Commit();
            return false;
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO retention_tombstones(
                tombstone_id, artifact_namespace, artifact_sha256,
                artifact_byte_length, artifact_media_type, state,
                policy_cutoff_utc, evaluated_at_utc, created_at_utc,
                updated_at_utc, deleted_at_utc, attempt_count,
                next_retry_at_utc, last_error)
            VALUES(
                $id, $namespace, $sha, $length, $media, $state,
                $cutoff, $evaluated, $evaluated, $evaluated, NULL, 1, NULL, NULL)
            ON CONFLICT(artifact_namespace, artifact_sha256) DO UPDATE SET
                state = excluded.state,
                evaluated_at_utc = excluded.evaluated_at_utc,
                updated_at_utc = excluded.updated_at_utc,
                attempt_count = retention_tombstones.attempt_count + 1,
                next_retry_at_utc = NULL,
                last_error = NULL;
            """,
            cancellationToken,
            ("$id", TombstoneId(candidate.Artifact)),
            ("$namespace", identity.Item1),
            ("$sha", identity.Item2),
            ("$length", candidate.Artifact.Content.ByteLength),
            ("$media", candidate.Artifact.Content.MediaType),
            ("$state", (int)EvidenceTombstoneState.Prepared),
            ("$cutoff", Utc(candidate.PolicyCutoffUtc)),
            ("$evaluated", Utc(evaluatedAtUtc)));
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE catalog_artifacts
            SET status = $status
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;

            DELETE FROM artifact_references
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
              AND owner_kind = $sourceOwner;

            UPDATE orphan_artifacts
            SET state = $orphanState
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            cancellationToken,
            ("$status", (int)EvidenceArtifactLifecycleStatus.Tombstoned),
            ("$namespace", identity.Item1),
            ("$sha", identity.Item2),
            ("$sourceOwner", (int)EvidencePublicationOwnerKind.SourceObservation),
            ("$orphanState", (int)EvidenceOrphanState.Tombstoned));
        transaction.Commit();
        return true;
    }

    private async Task MarkDeletedAsync(
        EvidenceArtifactReference artifact,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        await UpdateTombstoneAsync(
            artifact,
            EvidenceTombstoneState.Deleted,
            deletedAtUtc,
            null,
            null,
            EvidenceArtifactLifecycleStatus.Deleted,
            EvidenceOrphanState.Deleted,
            cancellationToken);
    }

    private async Task MarkFailureAsync(
        EvidenceArtifactReference artifact,
        string error,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        await UpdateTombstoneAsync(
            artifact,
            EvidenceTombstoneState.RetryableFailure,
            failedAtUtc,
            failedAtUtc.Add(RetryDelay),
            error,
            EvidenceArtifactLifecycleStatus.Tombstoned,
            EvidenceOrphanState.Tombstoned,
            cancellationToken);
    }

    private async Task UpdateTombstoneAsync(
        EvidenceArtifactReference artifact,
        EvidenceTombstoneState state,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? nextRetryAtUtc,
        string? error,
        EvidenceArtifactLifecycleStatus artifactStatus,
        EvidenceOrphanState orphanState,
        CancellationToken cancellationToken)
    {
        await using var connection = await catalog.OpenMaintenanceConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE retention_tombstones
            SET state = $state, updated_at_utc = $updated,
                deleted_at_utc = $deleted, next_retry_at_utc = $retry,
                last_error = $error
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;

            UPDATE catalog_artifacts
            SET status = $artifactStatus
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;

            UPDATE orphan_artifacts
            SET state = $orphanState,
                attempt_count = attempt_count + 1,
                last_error = $error
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            cancellationToken,
            ("$state", (int)state),
            ("$updated", Utc(updatedAtUtc)),
            ("$deleted", state == EvidenceTombstoneState.Deleted
                ? Utc(updatedAtUtc)
                : null),
            ("$retry", nextRetryAtUtc is null ? null : Utc(nextRetryAtUtc.Value)),
            ("$error", error),
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256),
            ("$artifactStatus", (int)artifactStatus),
            ("$orphanState", (int)orphanState));
        transaction.Commit();
    }

    private static async Task<bool> HasBlockingReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        (string Namespace, string Sha256) identity,
        IReadOnlySet<string> candidateSourceOwners,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT owner_kind, owner_id
            FROM artifact_references
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """;
        command.Parameters.AddWithValue("$namespace", identity.Namespace);
        command.Parameters.AddWithValue("$sha", identity.Sha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ownerKind = reader.GetInt32(0);
            var ownerId = reader.GetString(1);
            if (ownerKind != (int)EvidencePublicationOwnerKind.SourceObservation ||
                !candidateSourceOwners.Contains(ownerId))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsStillEligibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var identity = (
            candidate.Artifact.ObjectNamespace.Value,
            candidate.Artifact.Content.Sha256);
        var sourceOwners = new List<(string Id, DateTimeOffset ReceivedAtUtc)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT observation_id, received_at_utc
                FROM source_observations
                WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
                """;
            command.Parameters.AddWithValue("$namespace", identity.Item1);
            command.Parameters.AddWithValue("$sha", identity.Item2);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceOwners.Add((
                    reader.GetString(0),
                    DateTimeOffset.Parse(
                        reader.GetString(1),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
            }
        }

        if (sourceOwners.Count > 0)
        {
            if (sourceOwners.Any(owner => owner.ReceivedAtUtc >= candidate.PolicyCutoffUtc))
            {
                return false;
            }

            var protectedIds = await ReadDatasetAndQuarantineObservationIdsAsync(
                connection,
                transaction,
                cancellationToken);
            if (sourceOwners.Any(owner => protectedIds.Contains(owner.Id)) ||
                await HasBlockingReferenceAsync(
                    connection,
                    transaction,
                    identity,
                    sourceOwners.Select(owner => owner.Id).ToHashSet(StringComparer.Ordinal),
                    cancellationToken))
            {
                return false;
            }
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT first_seen_at_utc, classification, state
                FROM orphan_artifacts
                WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
                """;
            command.Parameters.AddWithValue("$namespace", identity.Item1);
            command.Parameters.AddWithValue("$sha", identity.Item2);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            var firstSeen = DateTimeOffset.Parse(
                reader.GetString(0),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            if (firstSeen >= candidate.PolicyCutoffUtc ||
                reader.GetInt32(1) != (int)EvidenceOrphanClassification.UnreferencedValidObject ||
                reader.GetInt32(2) != (int)EvidenceOrphanState.Observed)
            {
                return false;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT owner_kind, owner_id
                FROM artifact_publications
                WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
                """;
            command.Parameters.AddWithValue("$namespace", identity.Item1);
            command.Parameters.AddWithValue("$sha", identity.Item2);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var sourceIds = sourceOwners.Select(owner => owner.Id).ToHashSet(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) != (int)EvidencePublicationOwnerKind.SourceObservation ||
                    !sourceIds.Contains(reader.GetString(1)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<bool> HasPinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        (string Namespace, string Sha256) identity,
        CancellationToken cancellationToken)
    {
        return await ScalarLongAsync(
            connection,
            transaction,
            """
            SELECT EXISTS(
                SELECT 1 FROM retention_pin_artifacts
                WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha
            );
            """,
            cancellationToken,
            identity) != 0;
    }

    private static async Task<long?> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        (string Namespace, string Sha256) identity)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$namespace", identity.Namespace);
        command.Parameters.AddWithValue("$sha", identity.Sha256);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string TombstoneId(EvidenceArtifactReference artifact)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{artifact.ObjectNamespace.Value}:{artifact.Content.Sha256}");
        return $"retention-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32]}";
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }

    private sealed record SourceOwner(
        string ObservationId,
        DateTimeOffset ReceivedAtUtc,
        EvidenceArtifactReference Artifact);
}
