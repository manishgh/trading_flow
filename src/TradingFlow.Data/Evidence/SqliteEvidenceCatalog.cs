using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence;

/// <summary>
/// Dedicated evidence catalog. Canonical immutable domain JSON is the reconstruction source;
/// indexed columns provide identity, lifecycle, and query enforcement.
/// </summary>
public sealed class SqliteEvidenceCatalog :
    IEvidenceCatalog,
    IAtomicEvidenceDatasetCatalog,
    IEvidenceNormalizationRecoveryCatalog,
    IEvidenceReferenceSubjectRegistry
{
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly EvidenceCatalogOpenMode openMode;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool initialized;

    public SqliteEvidenceCatalog(
        EvidenceCatalogOptions options,
        IImmutableArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        databasePath = options.DatabasePath;
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!String.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        openMode = options.OpenMode;
        if (openMode == EvidenceCatalogOpenMode.BootstrapNew)
        {
            if (File.Exists(options.DatabasePath))
            {
                throw new IOException(
                    $"A database already exists at the bootstrap path '{options.DatabasePath}'.");
            }
        }
        else if (!File.Exists(options.DatabasePath))
        {
            throw new FileNotFoundException(
                "The existing evidence catalog database was not found.",
                options.DatabasePath);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task<EvidenceCatalogCommitResult> RegisterCollectionPlanAsync(
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var json = Canonical(plan);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM collection_plans WHERE job_id = $id;",
            cancellationToken,
            ("$id", plan.JobId));
        if (existing is not null)
        {
            EnsureSame(existing, json, "collection plan", plan.JobId);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(plan.JobId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO collection_plans(
                job_id, logical_plan_hash, attempt_hash, canonical_json)
            VALUES($job, $logical, $attempt, $json);
            """,
            cancellationToken,
            ("$job", plan.JobId),
            ("$logical", plan.LogicalPlanHash),
            ("$attempt", plan.CollectionAttemptHash),
            ("$json", json));
        transaction.Commit();
        return new EvidenceCatalogCommitResult(plan.JobId, false);
    }

    public async Task<EvidenceCollectionPlan?> GetCollectionPlanAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Deserialize<EvidenceCollectionPlan>(await ScalarStringAsync(
            connection,
            null,
            "SELECT canonical_json FROM collection_plans WHERE job_id = $id;",
            cancellationToken,
            ("$id", Required(jobId, nameof(jobId)))));
    }

    public async Task SaveCollectionCheckpointAsync(
        EvidenceCollectionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var plan = await RequirePlanAsync(
            connection,
            transaction,
            checkpoint.JobId,
            cancellationToken);
        var plannedRequestIds = plan.Requests
            .Select(request => request.RequestId)
            .ToHashSet(StringComparer.Ordinal);
        if (checkpoint.CompletedRequestIds.Any(requestId => !plannedRequestIds.Contains(requestId)))
        {
            throw new InvalidOperationException(
                "Collection checkpoint contains a request that is not part of its plan.");
        }

        var existing = Deserialize<EvidenceCollectionCheckpoint>(await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM collection_checkpoints WHERE job_id = $id;",
            cancellationToken,
            ("$id", checkpoint.JobId)));
        var toStore = checkpoint;
        if (existing is null)
        {
            if (checkpoint.State != EvidenceCollectionState.Planned ||
                checkpoint.CompletedRequestIds.Count != 0)
            {
                throw new InvalidOperationException(
                    "The first collection checkpoint must be an empty Planned checkpoint.");
            }
        }
        else
        {
            if (existing.State != checkpoint.State)
            {
                EvidenceCollectionTransitions.EnsureAllowed(existing.State, checkpoint.State);
            }
            else if (IsTerminal(existing.State))
            {
                if (!Canonical(existing).Equals(Canonical(checkpoint), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A terminal collection checkpoint can only be replayed exactly.");
                }

                transaction.Commit();
                return;
            }

            if (checkpoint.UpdatedAtUtc < existing.UpdatedAtUtc)
            {
                throw new InvalidOperationException("A collection checkpoint cannot move backward in time.");
            }

            var completed = existing.CompletedRequestIds
                .Concat(checkpoint.CompletedRequestIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            toStore = new EvidenceCollectionCheckpoint(
                checkpoint.JobId,
                checkpoint.State,
                checkpoint.UpdatedAtUtc,
                completed,
                checkpoint.FailureReason);
        }
        if (toStore.State == EvidenceCollectionState.Committed &&
            toStore.CompletedRequestIds.Count != plannedRequestIds.Count)
        {
            throw new InvalidOperationException(
                "A committed collection checkpoint requires every planned request to be complete.");
        }
        foreach (var requestId in toStore.CompletedRequestIds)
        {
            var completion = await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT completion_observation_id FROM request_completions
                WHERE job_id = $job AND request_id = $request;
                """,
                cancellationToken,
                ("$job", toStore.JobId),
                ("$request", requestId));
            if (completion is null)
            {
                throw new InvalidOperationException(
                    $"Collection request '{requestId}' has no immutable terminal page receipt.");
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO collection_checkpoints(job_id, state, updated_at_utc, canonical_json)
            VALUES($job, $state, $updated, $json)
            ON CONFLICT(job_id) DO UPDATE SET
                state = excluded.state,
                updated_at_utc = excluded.updated_at_utc,
                canonical_json = excluded.canonical_json;
            """,
            cancellationToken,
            ("$job", toStore.JobId),
            ("$state", (int)toStore.State),
            ("$updated", Utc(toStore.UpdatedAtUtc)),
            ("$json", Canonical(toStore)));
        transaction.Commit();
    }

    public async Task<EvidenceCollectionCheckpoint?> GetCollectionCheckpointAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Deserialize<EvidenceCollectionCheckpoint>(await ScalarStringAsync(
            connection,
            null,
            "SELECT canonical_json FROM collection_checkpoints WHERE job_id = $id;",
            cancellationToken,
            ("$id", Required(jobId, nameof(jobId)))));
    }

    public async Task RecoverQuarantinedNormalizationAsync(
        string jobId,
        string decidedBy,
        string reason,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default)
    {
        jobId = Required(jobId, nameof(jobId));
        decidedBy = Required(decidedBy, nameof(decidedBy));
        reason = Required(reason, nameof(reason));
        if (decidedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The normalization recovery decision timestamp must be UTC.",
                nameof(decidedAtUtc));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var plan = await RequirePlanAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);
        var checkpoint = Deserialize<EvidenceCollectionCheckpoint>(
            await ScalarStringAsync(
                connection,
                transaction,
                "SELECT canonical_json FROM collection_checkpoints WHERE job_id = $id;",
                cancellationToken,
                ("$id", jobId)))
            ?? throw new InvalidOperationException("Collection checkpoint does not exist.");
        if (checkpoint.State != EvidenceCollectionState.Quarantined)
        {
            throw new InvalidOperationException(
                $"Only a quarantined collection can be recovered; '{jobId}' is {checkpoint.State}.");
        }

        var completed = checkpoint.CompletedRequestIds.ToHashSet(StringComparer.Ordinal);
        if (plan.Requests.Any(request => !completed.Contains(request.RequestId)))
        {
            throw new InvalidOperationException(
                "Normalization recovery requires every collection request to be complete.");
        }

        foreach (var request in plan.Requests)
        {
            var completion = await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT completion_observation_id FROM request_completions
                WHERE job_id = $job AND request_id = $request;
                """,
                cancellationToken,
                ("$job", jobId),
                ("$request", request.RequestId));
            if (completion is null)
            {
                throw new InvalidOperationException(
                    $"Collection request '{request.RequestId}' has no immutable terminal receipt.");
            }
        }

        var quarantineIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT quarantine_id FROM quarantines WHERE status = $status ORDER BY quarantine_id;";
            command.Parameters.AddWithValue("$status", (int)EvidenceQuarantineStatus.Open);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                quarantineIds.Add(reader.GetString(0));
            }
        }

        var matching = new List<EvidenceQuarantineEntry>();
        foreach (var quarantineId in quarantineIds)
        {
            var entry = await ReadQuarantineEntryAsync(
                connection,
                transaction,
                quarantineId,
                cancellationToken);
            if (entry?.Record.LogicalKey.Equals(jobId, StringComparison.Ordinal) == true)
            {
                matching.Add(entry);
            }
        }

        if (matching.Count == 0)
        {
            throw new InvalidOperationException(
                "No open dataset quarantine exists for this collection.");
        }

        foreach (var entry in matching)
        {
            var resolution = new EvidenceQuarantineResolution(
                entry.Record.QuarantineId,
                EvidenceQuarantineStatus.Resolved,
                decidedBy,
                reason,
                decidedAtUtc);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO quarantine_decisions(
                    quarantine_id, decision_ordinal, status, decided_at_utc, canonical_json)
                VALUES($id, $ordinal, $status, $decided, $json);
                """,
                cancellationToken,
                ("$id", entry.Record.QuarantineId),
                ("$ordinal", entry.Decisions.Count + 1),
                ("$status", (int)resolution.Status),
                ("$decided", Utc(resolution.DecidedAtUtc)),
                ("$json", Canonical(resolution)));
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE quarantines SET status = $status WHERE quarantine_id = $id;",
                cancellationToken,
                ("$status", (int)EvidenceQuarantineStatus.Resolved),
                ("$id", entry.Record.QuarantineId));
        }

        var recovered = new EvidenceCollectionCheckpoint(
            jobId,
            EvidenceCollectionState.Normalizing,
            decidedAtUtc,
            checkpoint.CompletedRequestIds);
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE collection_checkpoints
            SET state = $state, updated_at_utc = $updated, canonical_json = $json
            WHERE job_id = $job AND state = $quarantined;
            """,
            cancellationToken,
            ("$state", (int)EvidenceCollectionState.Normalizing),
            ("$updated", Utc(decidedAtUtc)),
            ("$json", Canonical(recovered)),
            ("$job", jobId),
            ("$quarantined", (int)EvidenceCollectionState.Quarantined));
        transaction.Commit();
    }

    public async Task SaveRequestCursorCheckpointAsync(
        EvidenceRequestCursorCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var plan = await RequirePlanAsync(connection, transaction, checkpoint.JobId, cancellationToken);
        if (!plan.Requests.Any(request =>
                request.RequestId.Equals(checkpoint.RequestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Request '{checkpoint.RequestId}' is not part of plan '{checkpoint.JobId}'.");
        }

        var existing = Deserialize<EvidenceRequestCursorCheckpoint>(await ScalarStringAsync(
            connection,
            transaction,
            """
            SELECT canonical_json FROM request_cursor_checkpoints
            WHERE job_id = $job AND request_id = $request;
            """,
            cancellationToken,
            ("$job", checkpoint.JobId),
            ("$request", checkpoint.RequestId)));
        var observation = Deserialize<EvidenceSourceObservation>(await ScalarStringAsync(
            connection,
            transaction,
            """
            SELECT source.canonical_json
            FROM source_observations AS source
            WHERE source.observation_id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM retention_tombstones AS tombstone
                  WHERE tombstone.artifact_namespace = source.artifact_namespace
                    AND tombstone.artifact_sha256 = source.artifact_sha256
              );
            """,
            cancellationToken,
            ("$id", checkpoint.ObservationId)));
        if (observation is null ||
            !observation.CollectionJobId.Equals(checkpoint.JobId, StringComparison.Ordinal) ||
            !observation.IngestionRequestId.Equals(checkpoint.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cursor checkpoint must reference a registered observation for the same plan request.");
        }
        var expectedPageIdentity = $"page-{checkpoint.PageOrdinal}";
        if (!observation.PageIdentity.Equals(expectedPageIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cursor page {checkpoint.PageOrdinal} requires observation page identity " +
                $"'{expectedPageIdentity}'.");
        }

        await RequireValidArtifactAsync(observation.Artifact, cancellationToken);
        if (existing is null)
        {
            if (checkpoint.PageOrdinal != 1 ||
                checkpoint.ConsumedPageTokenHashes.Count != 0)
            {
                throw new InvalidOperationException(
                    "The first cursor checkpoint must record page one with no consumed cursor token.");
            }
        }
        else
        {
            if (checkpoint.UpdatedAtUtc < existing.UpdatedAtUtc ||
                checkpoint.PageOrdinal < existing.PageOrdinal ||
                checkpoint.PageOrdinal > existing.PageOrdinal + 1 ||
                checkpoint.AttemptCount < existing.AttemptCount ||
                !checkpoint.ConsumedPageTokenHashes
                    .Take(existing.ConsumedPageTokenHashes.Count)
                    .SequenceEqual(existing.ConsumedPageTokenHashes))
            {
                throw new InvalidOperationException("Cursor progress cannot regress or lose token history.");
            }

            if (existing.Exhausted && !CursorReplayMatches(existing, checkpoint))
            {
                throw new InvalidOperationException("An exhausted cursor is terminal.");
            }

            if (checkpoint.PageOrdinal == existing.PageOrdinal &&
                !CursorReplayMatches(existing, checkpoint))
            {
                throw new InvalidOperationException(
                    "A cursor replay at the same page ordinal must be exact.");
            }

            if (checkpoint.PageOrdinal > existing.PageOrdinal)
            {
                if (existing.NextPageToken is null)
                {
                    throw new InvalidOperationException(
                        "A cursor cannot advance when the previous page returned no next-page token.");
                }

                var expectedHistory = existing.ConsumedPageTokenHashes
                    .Append(Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(existing.NextPageToken)))
                        .ToLowerInvariant());
                if (!checkpoint.ConsumedPageTokenHashes.SequenceEqual(expectedHistory))
                {
                    throw new InvalidOperationException(
                        "A cursor page must consume exactly the previous page's next-page token.");
                }

                if (checkpoint.AttemptCount <= existing.AttemptCount)
                {
                    throw new InvalidOperationException(
                        "A new cursor page must advance the cumulative request-attempt count.");
                }

                if (checkpoint.ObservationId.Equals(existing.ObservationId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A new cursor page requires its own source observation.");
                }
            }
        }

        var checkpointJson = Canonical(checkpoint);
        var existingPageJson = await ScalarStringAsync(
            connection,
            transaction,
            """
            SELECT canonical_json FROM request_page_ledger
            WHERE job_id = $job AND request_id = $request AND page_ordinal = $page;
            """,
            cancellationToken,
            ("$job", checkpoint.JobId),
            ("$request", checkpoint.RequestId),
            ("$page", checkpoint.PageOrdinal));
        if (existingPageJson is not null)
        {
            EnsureSame(
                existingPageJson,
                checkpointJson,
                "request page",
                $"{checkpoint.JobId}:{checkpoint.RequestId}:{checkpoint.PageOrdinal}");
        }
        else
        {
            var reusedObservation = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT observation_id FROM request_page_ledger WHERE observation_id = $observation;",
                cancellationToken,
                ("$observation", checkpoint.ObservationId));
            if (reusedObservation is not null)
            {
                throw new InvalidOperationException(
                    "A source observation cannot prove more than one request page.");
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO request_page_ledger(
                    job_id, request_id, page_ordinal, observation_id,
                    consumed_page_token_hash, next_page_token_hash, exhausted,
                    canonical_json, created_at_utc)
                VALUES(
                    $job, $request, $page, $observation,
                    $consumed, $next, $exhausted, $json, $created);
                """,
                cancellationToken,
                ("$job", checkpoint.JobId),
                ("$request", checkpoint.RequestId),
                ("$page", checkpoint.PageOrdinal),
                ("$observation", checkpoint.ObservationId),
                ("$consumed", checkpoint.PageOrdinal == 1
                    ? null
                    : checkpoint.ConsumedPageTokenHashes[^1]),
                ("$next", checkpoint.NextPageToken is null
                    ? null
                    : Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.NextPageToken)))
                        .ToLowerInvariant()),
                ("$exhausted", checkpoint.Exhausted ? 1 : 0),
                ("$json", checkpointJson),
                ("$created", Utc(checkpoint.UpdatedAtUtc)));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO request_cursor_checkpoints(
                job_id, request_id, page_ordinal, updated_at_utc, canonical_json)
            VALUES($job, $request, $page, $updated, $json)
            ON CONFLICT(job_id, request_id) DO UPDATE SET
                page_ordinal = excluded.page_ordinal,
                updated_at_utc = excluded.updated_at_utc,
                canonical_json = excluded.canonical_json;
            """,
            cancellationToken,
            ("$job", checkpoint.JobId),
            ("$request", checkpoint.RequestId),
            ("$page", checkpoint.PageOrdinal),
            ("$updated", Utc(checkpoint.UpdatedAtUtc)),
            ("$json", checkpointJson));
        if (checkpoint.Exhausted)
        {
            var existingCompletion = await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT canonical_json FROM request_completions
                WHERE job_id = $job AND request_id = $request;
                """,
                cancellationToken,
                ("$job", checkpoint.JobId),
                ("$request", checkpoint.RequestId));
            if (existingCompletion is not null)
            {
                EnsureSame(
                    existingCompletion,
                    checkpointJson,
                    "request completion",
                    $"{checkpoint.JobId}:{checkpoint.RequestId}");
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO request_completions(
                        job_id, request_id, terminal_page_ordinal,
                        completion_observation_id, canonical_json, completed_at_utc)
                    VALUES($job, $request, $page, $observation, $json, $completed);
                    """,
                    cancellationToken,
                    ("$job", checkpoint.JobId),
                    ("$request", checkpoint.RequestId),
                    ("$page", checkpoint.PageOrdinal),
                    ("$observation", checkpoint.ObservationId),
                    ("$json", checkpointJson),
                    ("$completed", Utc(checkpoint.UpdatedAtUtc)));
            }
        }
        transaction.Commit();
    }

    private static bool CursorReplayMatches(
        EvidenceRequestCursorCheckpoint existing,
        EvidenceRequestCursorCheckpoint candidate) =>
        existing.JobId.Equals(candidate.JobId, StringComparison.Ordinal) &&
        existing.RequestId.Equals(candidate.RequestId, StringComparison.Ordinal) &&
        existing.PageOrdinal == candidate.PageOrdinal &&
        String.Equals(existing.NextPageToken, candidate.NextPageToken, StringComparison.Ordinal) &&
        existing.ConsumedPageTokenHashes.SequenceEqual(candidate.ConsumedPageTokenHashes) &&
        existing.ObservationId.Equals(candidate.ObservationId, StringComparison.Ordinal) &&
        existing.AttemptCount == candidate.AttemptCount &&
        existing.Exhausted == candidate.Exhausted &&
        existing.UpdatedAtUtc == candidate.UpdatedAtUtc;

    private static bool IsTerminal(EvidenceCollectionState state) =>
        state is EvidenceCollectionState.Committed
            or EvidenceCollectionState.Quarantined
            or EvidenceCollectionState.Cancelled;

    public async Task<EvidenceRequestCursorCheckpoint?> GetRequestCursorCheckpointAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Deserialize<EvidenceRequestCursorCheckpoint>(await ScalarStringAsync(
            connection,
            null,
            """
            SELECT canonical_json FROM request_cursor_checkpoints
            WHERE job_id = $job AND request_id = $request;
            """,
            cancellationToken,
            ("$job", Required(jobId, nameof(jobId))),
            ("$request", Required(requestId, nameof(requestId)))));
    }

    public async Task<EvidenceCatalogCommitResult> RegisterSourceObservationAsync(
        EvidenceSourceObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await RequireArtifactAcceptsNewReferenceAsync(
            connection,
            transaction,
            observation.Artifact,
            cancellationToken);
        await RequireValidArtifactAsync(observation.Artifact, cancellationToken);
        var publication = await EnsurePublicationAsync(
            connection,
            transaction,
            EvidencePublicationOwnerKind.SourceObservation,
            observation.ObservationId,
            observation.Artifact,
            Canonical(observation),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var plan = await RequirePlanAsync(
            connection,
            transaction,
            observation.CollectionJobId,
            cancellationToken);
        if (!plan.LogicalPlanHash.Equals(observation.CollectionPlanHash, StringComparison.Ordinal) ||
            !plan.CollectionAttemptHash.Equals(observation.CollectionAttemptHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Observation collection identity does not match its plan.");
        }

        var request = plan.Requests.SingleOrDefault(candidate =>
            candidate.RequestId.Equals(observation.IngestionRequestId, StringComparison.Ordinal));
        if (request is null ||
            !request.Provider.Equals(observation.Provider, StringComparison.Ordinal) ||
            !request.Endpoint.Equals(observation.Endpoint, StringComparison.Ordinal) ||
            !request.DataFeed.Equals(observation.DataFeed, StringComparison.Ordinal) ||
            !request.Adjustment.Equals(observation.Adjustment, StringComparison.Ordinal) ||
            !request.Currency.Equals(observation.Currency, StringComparison.Ordinal) ||
            request.AsOfDate != observation.AsOfDate ||
            !request.Symbols.SequenceEqual(observation.RequestedSymbols) ||
            request.RequestedStartUtc != observation.RequestedStartUtc ||
            request.RequestedEndUtc != observation.RequestedEndUtc)
        {
            throw new InvalidOperationException("Observation request evidence does not match its plan request.");
        }

        var json = Canonical(observation);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM source_observations WHERE observation_id = $id;",
            cancellationToken,
            ("$id", observation.ObservationId));
        if (existing is not null)
        {
            EnsureSame(existing, json, "source observation", observation.ObservationId);
            await CompleteSourcePublicationAsync(
                connection,
                transaction,
                publication,
                observation,
                cancellationToken);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(observation.ObservationId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO source_observations(
                observation_id, job_id, request_id, artifact_namespace,
                artifact_sha256, received_at_utc, canonical_json)
            VALUES($id, $job, $request, $namespace, $sha, $received, $json);
            """,
            cancellationToken,
            ("$id", observation.ObservationId),
            ("$job", observation.CollectionJobId),
            ("$request", observation.IngestionRequestId),
            ("$namespace", observation.Artifact.ObjectNamespace.Value),
            ("$sha", observation.Artifact.Content.Sha256),
            ("$received", Utc(observation.ReceivedAtUtc)),
            ("$json", json));
        await CompleteSourcePublicationAsync(
            connection,
            transaction,
            publication,
            observation,
            cancellationToken);
        transaction.Commit();
        return new EvidenceCatalogCommitResult(observation.ObservationId, false);
    }

    internal async Task<EvidencePublicationRecord> PrepareSourceObservationPublicationAsync(
        EvidenceSourceObservation observation,
        DateTimeOffset preparedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        EnsureUtc(preparedAtUtc, nameof(preparedAtUtc));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await RequireArtifactAcceptsNewReferenceAsync(
            connection,
            transaction,
            observation.Artifact,
            cancellationToken);
        await RequirePlanAsync(
            connection,
            transaction,
            observation.CollectionJobId,
            cancellationToken);
        var publication = await EnsurePublicationAsync(
            connection,
            transaction,
            EvidencePublicationOwnerKind.SourceObservation,
            observation.ObservationId,
            observation.Artifact,
            Canonical(observation),
            preparedAtUtc,
            cancellationToken,
            EvidencePublicationState.Prepared);
        transaction.Commit();
        return publication;
    }

    private async Task<EvidencePublicationRecord> PrepareManifestPublicationAsync(
        EvidencePublicationOwnerKind ownerKind,
        string ownerId,
        EvidenceArtifactReference artifact,
        string canonicalOwner,
        DateTimeOffset preparedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUtc(preparedAtUtc, nameof(preparedAtUtc));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var publication = await EnsurePublicationAsync(
            connection,
            transaction,
            ownerKind,
            ownerId,
            artifact,
            canonicalOwner,
            preparedAtUtc,
            cancellationToken,
            EvidencePublicationState.Prepared);
        transaction.Commit();
        return publication;
    }

    internal async Task MarkPublicationObjectPublishedAsync(
        string publicationId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var publication = await RequirePublicationAsync(
            connection,
            transaction,
            publicationId,
            cancellationToken);
        if (publication.State is EvidencePublicationState.CatalogCommitted or
            EvidencePublicationState.ObjectPublished)
        {
            transaction.Commit();
            return;
        }

        if (publication.State is EvidencePublicationState.Quarantined or
            EvidencePublicationState.Superseded)
        {
            throw new InvalidOperationException(
                $"Publication '{publicationId}' is terminal in state {publication.State}.");
        }

        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publicationId,
            EvidencePublicationState.ObjectPublished,
            updatedAtUtc,
            publication.AttemptCount,
            null,
            cancellationToken);
        transaction.Commit();
    }

    internal async Task MarkPublicationFailureAsync(
        string publicationId,
        string error,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        error = Required(error, nameof(error));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var publication = await RequirePublicationAsync(
            connection,
            transaction,
            publicationId,
            cancellationToken);
        if (publication.State == EvidencePublicationState.CatalogCommitted)
        {
            transaction.Commit();
            return;
        }

        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publicationId,
            EvidencePublicationState.RetryableFailure,
            updatedAtUtc,
            publication.AttemptCount + 1,
            error,
            cancellationToken);
        transaction.Commit();
    }

    internal async Task MarkPublicationQuarantinedAsync(
        string publicationId,
        string error,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        error = Required(error, nameof(error));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var publication = await RequirePublicationAsync(
            connection,
            transaction,
            publicationId,
            cancellationToken);
        if (publication.State == EvidencePublicationState.CatalogCommitted)
        {
            throw new InvalidOperationException(
                "A committed publication cannot be quarantined.");
        }

        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publicationId,
            EvidencePublicationState.Quarantined,
            updatedAtUtc,
            publication.AttemptCount + 1,
            error,
            cancellationToken);
        transaction.Commit();
    }

    internal async Task<IReadOnlyList<EvidencePublicationRecord>> FindPendingPublicationsAsync(
        int maximumPublications,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT publication_id, owner_kind, owner_id, state,
                   artifact_namespace, artifact_sha256, artifact_byte_length,
                   artifact_media_type, canonical_owner_json, created_at_utc,
                   updated_at_utc, attempt_count, last_error
            FROM artifact_publications
            WHERE state IN ($prepared, $published, $failed)
            ORDER BY created_at_utc, publication_id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$prepared", (int)EvidencePublicationState.Prepared);
        command.Parameters.AddWithValue("$published", (int)EvidencePublicationState.ObjectPublished);
        command.Parameters.AddWithValue("$failed", (int)EvidencePublicationState.RetryableFailure);
        command.Parameters.AddWithValue("$maximum", maximumPublications);
        var publications = new List<EvidencePublicationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            publications.Add(ReadPublication(reader));
        }

        return publications;
    }

    public async Task<EvidenceSourceObservation?> GetSourceObservationAsync(
        string observationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Deserialize<EvidenceSourceObservation>(await ScalarStringAsync(
            connection,
            null,
            """
            SELECT source.canonical_json
            FROM source_observations AS source
            WHERE source.observation_id = $id
              AND NOT EXISTS (
                  SELECT 1
                  FROM retention_tombstones AS tombstone
                  WHERE tombstone.artifact_namespace = source.artifact_namespace
                    AND tombstone.artifact_sha256 = source.artifact_sha256
              );
            """,
            cancellationToken,
            ("$id", Required(observationId, nameof(observationId)))));
    }

    public async Task<IReadOnlyList<EvidenceSourceObservation>> FindSourceObservationsAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        jobId = Required(jobId, nameof(jobId));
        requestId = Required(requestId, nameof(requestId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source.canonical_json
            FROM source_observations AS source
            WHERE source.job_id = $job
              AND source.request_id = $request
              AND NOT EXISTS (
                  SELECT 1
                  FROM retention_tombstones AS tombstone
                  WHERE tombstone.artifact_namespace = source.artifact_namespace
                    AND tombstone.artifact_sha256 = source.artifact_sha256
              )
            ORDER BY source.received_at_utc, source.observation_id;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$request", requestId);

        var observations = new List<EvidenceSourceObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            observations.Add(
                Deserialize<EvidenceSourceObservation>(reader.GetString(0)) ??
                throw new InvalidDataException(
                    "A source observation catalog row has no canonical payload."));
        }

        return observations
            .Select(observation => new
            {
                Observation = observation,
                PageOrdinal = RequiredPositiveObservationAttribute(
                    observation,
                    "page_ordinal"),
                Attempt = RequiredPositiveObservationAttribute(observation, "attempt")
            })
            .OrderBy(value => value.PageOrdinal)
            .ThenBy(value => value.Observation.ReceivedAtUtc)
            .ThenBy(value => value.Attempt)
            .ThenBy(value => value.Observation.ObservationId, StringComparer.Ordinal)
            .Select(value => value.Observation)
            .ToArray();
    }

    public async Task<EvidenceCatalogCommitResult> CommitDatasetAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var partition in manifest.Partitions)
        {
            await RequireValidArtifactAsync(partition.Artifact, cancellationToken);
            if (partition.QuarantineLedger is not null)
            {
                await RequireValidArtifactAsync(partition.QuarantineLedger, cancellationToken);
            }
        }
        await ValidateDatasetPrerequisitesAsync(manifest, cancellationToken);

        var manifestBytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        var manifestArtifact = Artifact(
            manifestBytes,
            EvidenceObjectNamespaces.DatasetManifests,
            "application/vnd.tradingflow.evidence-dataset+json");
        var publication = await PrepareManifestPublicationAsync(
            EvidencePublicationOwnerKind.Dataset,
            manifest.DatasetId,
            manifestArtifact,
            Canonical(manifest),
            DateTimeOffset.UtcNow,
            cancellationToken);
        try
        {
            await artifactStore.PutIfAbsentAsync(
                new(manifestArtifact),
                manifestBytes,
                cancellationToken);
            await MarkPublicationObjectPublishedAsync(
                publication.PublicationId,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkPublicationFailureAsync(
                publication.PublicationId,
                exception.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
            throw;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await RequireDatasetLineageAsync(connection, transaction, manifest, cancellationToken);
        var existingLogical = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT dataset_id, manifest_namespace, manifest_sha256, canonical_json
            FROM datasets WHERE logical_dataset_key = $logical;
            """,
            cancellationToken,
            ("$logical", manifest.LogicalDatasetKey));
        if (existingLogical is not null)
        {
            await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
                existingLogical[1],
                existingLogical[2],
                existingLogical[3],
                "application/vnd.tradingflow.evidence-dataset+json",
                cancellationToken);
            await UpdatePublicationStateAsync(
                connection,
                transaction,
                publication.PublicationId,
                existingLogical[0].Equals(manifest.DatasetId, StringComparison.Ordinal)
                    ? EvidencePublicationState.CatalogCommitted
                    : EvidencePublicationState.Superseded,
                DateTimeOffset.UtcNow,
                publication.AttemptCount,
                existingLogical[0].Equals(manifest.DatasetId, StringComparison.Ordinal)
                    ? null
                    : $"Logical dataset key already belongs to '{existingLogical[0]}'.",
                cancellationToken);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(
                existingLogical[0],
                true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO datasets(
                dataset_id, logical_dataset_key, kind, data_feed, created_at_utc,
                manifest_namespace, manifest_sha256, canonical_json)
            VALUES($id, $logical, $kind, $feed, $created, $namespace, $sha, $json);
            """,
            cancellationToken,
            ("$id", manifest.DatasetId),
            ("$logical", manifest.LogicalDatasetKey),
            ("$kind", (int)manifest.Kind),
            ("$feed", manifest.DataFeed),
            ("$created", Utc(manifest.CreatedAtUtc)),
            ("$namespace", manifestArtifact.ObjectNamespace.Value),
            ("$sha", manifestArtifact.Content.Sha256),
            ("$json", Canonical(manifest)));
        await RegisterArtifactReferenceAsync(
            connection,
            transaction,
            EvidencePublicationOwnerKind.Dataset,
            manifest.DatasetId,
            "manifest",
            manifestArtifact,
            EvidenceArtifactKind.DatasetManifest,
            manifest.CreatedAtUtc,
            cancellationToken);
        foreach (var partition in manifest.Partitions)
        {
            await RegisterArtifactReferenceAsync(
                connection,
                transaction,
                EvidencePublicationOwnerKind.Dataset,
                manifest.DatasetId,
                $"partition:{partition.PartitionId}",
                partition.Artifact,
                EvidenceArtifactKind.NormalizedPartition,
                manifest.CreatedAtUtc,
                cancellationToken);
            if (partition.QuarantineLedger is not null)
            {
                await RegisterArtifactReferenceAsync(
                    connection,
                    transaction,
                    EvidencePublicationOwnerKind.Dataset,
                    manifest.DatasetId,
                    $"quarantine:{partition.PartitionId}",
                    partition.QuarantineLedger,
                    EvidenceArtifactKind.Diagnostic,
                    manifest.CreatedAtUtc,
                    cancellationToken);
            }
        }
        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publication.PublicationId,
            EvidencePublicationState.CatalogCommitted,
            DateTimeOffset.UtcNow,
            publication.AttemptCount,
            null,
            cancellationToken);
        transaction.Commit();
        return new EvidenceCatalogCommitResult(manifest.DatasetId, false);
    }

    public async Task<IReadOnlyList<EvidenceCatalogCommitResult>>
        CommitDatasetsAtomicallyAsync(
            IReadOnlyList<EvidenceDatasetManifest> manifests,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var batch = manifests
            .Select(manifest => manifest ??
                throw new ArgumentException(
                    "Dataset batches cannot contain null manifests.",
                    nameof(manifests)))
            .ToArray();
        if (batch.Length == 0)
        {
            throw new ArgumentException(
                "An atomic dataset batch must contain at least one manifest.",
                nameof(manifests));
        }

        if (batch.Select(manifest => manifest.DatasetId)
                .Distinct(StringComparer.Ordinal)
                .Count() != batch.Length ||
            batch.Select(manifest => manifest.LogicalDatasetKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != batch.Length)
        {
            throw new ArgumentException(
                "An atomic dataset batch cannot repeat a dataset or logical dataset key.",
                nameof(manifests));
        }

        var prepared = new List<AtomicDatasetCommit>(batch.Length);
        foreach (var manifest in batch)
        {
            foreach (var partition in manifest.Partitions)
            {
                await RequireValidArtifactAsync(partition.Artifact, cancellationToken);
                if (partition.QuarantineLedger is not null)
                {
                    await RequireValidArtifactAsync(
                        partition.QuarantineLedger,
                        cancellationToken);
                }
            }

            await ValidateDatasetPrerequisitesAsync(manifest, cancellationToken);
            var manifestBytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
            var manifestArtifact = Artifact(
                manifestBytes,
                EvidenceObjectNamespaces.DatasetManifests,
                "application/vnd.tradingflow.evidence-dataset+json");
            await artifactStore.PutIfAbsentAsync(
                new(manifestArtifact),
                manifestBytes,
                cancellationToken);
            await RequireValidArtifactAsync(manifestArtifact, cancellationToken);
            prepared.Add(new AtomicDatasetCommit(manifest, manifestArtifact));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var results = new List<EvidenceCatalogCommitResult>(prepared.Count);
        foreach (var item in prepared)
        {
            var manifest = item.Manifest;
            await RequireDatasetLineageAsync(
                connection,
                transaction,
                manifest,
                cancellationToken);
            var existingLogical = await ReadRowAsync(
                connection,
                transaction,
                """
                SELECT dataset_id, manifest_namespace, manifest_sha256, canonical_json
                FROM datasets WHERE logical_dataset_key = $logical;
                """,
                cancellationToken,
                ("$logical", manifest.LogicalDatasetKey));
            if (existingLogical is not null)
            {
                await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
                    existingLogical[1],
                    existingLogical[2],
                    existingLogical[3],
                    "application/vnd.tradingflow.evidence-dataset+json",
                    cancellationToken);
                results.Add(new EvidenceCatalogCommitResult(existingLogical[0], true));
                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO datasets(
                    dataset_id, logical_dataset_key, kind, data_feed, created_at_utc,
                    manifest_namespace, manifest_sha256, canonical_json)
                VALUES($id, $logical, $kind, $feed, $created, $namespace, $sha, $json);
                """,
                cancellationToken,
                ("$id", manifest.DatasetId),
                ("$logical", manifest.LogicalDatasetKey),
                ("$kind", (int)manifest.Kind),
                ("$feed", manifest.DataFeed),
                ("$created", Utc(manifest.CreatedAtUtc)),
                ("$namespace", item.ManifestArtifact.ObjectNamespace.Value),
                ("$sha", item.ManifestArtifact.Content.Sha256),
                ("$json", Canonical(manifest)));
            await RegisterArtifactReferenceAsync(
                connection,
                transaction,
                EvidencePublicationOwnerKind.Dataset,
                manifest.DatasetId,
                "manifest",
                item.ManifestArtifact,
                EvidenceArtifactKind.DatasetManifest,
                manifest.CreatedAtUtc,
                cancellationToken);
            foreach (var partition in manifest.Partitions)
            {
                await RegisterArtifactReferenceAsync(
                    connection,
                    transaction,
                    EvidencePublicationOwnerKind.Dataset,
                    manifest.DatasetId,
                    $"partition:{partition.PartitionId}",
                    partition.Artifact,
                    EvidenceArtifactKind.NormalizedPartition,
                    manifest.CreatedAtUtc,
                    cancellationToken);
                if (partition.QuarantineLedger is not null)
                {
                    await RegisterArtifactReferenceAsync(
                        connection,
                        transaction,
                        EvidencePublicationOwnerKind.Dataset,
                        manifest.DatasetId,
                        $"quarantine:{partition.PartitionId}",
                        partition.QuarantineLedger,
                        EvidenceArtifactKind.Diagnostic,
                        manifest.CreatedAtUtc,
                        cancellationToken);
                }
            }

            results.Add(new EvidenceCatalogCommitResult(manifest.DatasetId, false));
        }

        transaction.Commit();
        return results;
    }

    private static int RequiredPositiveObservationAttribute(
        EvidenceSourceObservation observation,
        string key)
    {
        if (!observation.RequestAttributes.TryGetValue(key, out var text) ||
            !Int32.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw new InvalidDataException(
                $"Source observation '{observation.ObservationId}' has no valid '{key}' attribute.");
        }

        return value;
    }

    public async Task<EvidenceDatasetManifest?> GetDatasetAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadRowAsync(
            connection,
            null,
            """
            SELECT manifest_namespace, manifest_sha256, canonical_json
            FROM datasets WHERE dataset_id = $id;
            """,
            cancellationToken,
            ("$id", Required(datasetId, nameof(datasetId))));
        return row is null
            ? null
            : await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
                row[0],
                row[1],
                row[2],
                "application/vnd.tradingflow.evidence-dataset+json",
                cancellationToken);
    }

    public async Task<IReadOnlyList<EvidenceDatasetManifest>> FindDatasetsAsync(
        EvidenceDatasetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.CreatedAtOrAfterUtc is { } cutoff)
        {
            EnsureUtc(cutoff, nameof(query.CreatedAtOrAfterUtc));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (query.Kind is { } kind)
        {
            conditions.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", (int)kind);
        }

        if (!String.IsNullOrWhiteSpace(query.DataFeed))
        {
            conditions.Add("data_feed = $feed");
            command.Parameters.AddWithValue("$feed", query.DataFeed.Trim().ToLowerInvariant());
        }

        if (query.CreatedAtOrAfterUtc is { } created)
        {
            conditions.Add("created_at_utc >= $created");
            command.Parameters.AddWithValue("$created", Utc(created));
        }

        command.CommandText =
            "SELECT manifest_namespace, manifest_sha256, canonical_json FROM datasets" +
            (conditions.Count == 0 ? String.Empty : " WHERE " + String.Join(" AND ", conditions)) +
            " ORDER BY created_at_utc, dataset_id;";
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2)]);
        }

        var results = new List<EvidenceDatasetManifest>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
                row[0],
                row[1],
                row[2],
                "application/vnd.tradingflow.evidence-dataset+json",
                cancellationToken));
        }

        return results;
    }

    public async Task<EvidenceCatalogCommitResult> RegisterResearchRunAsync(
        EvidenceResearchRunManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var artifacts = new[]
            {
                manifest.UniverseLedgerArtifact,
                manifest.StudyConfigArtifact,
                manifest.Assumptions.CostModel,
                manifest.Assumptions.SpreadModel,
                manifest.Assumptions.SlippageModel,
                manifest.Assumptions.BorrowModel,
                manifest.Assumptions.BenchmarkDefinition
            }
            .Concat(manifest.Holdout is null
                ? Array.Empty<EvidenceArtifactReference>()
                : [manifest.Holdout.PartitionDefinitionArtifact])
            .Concat(manifest.Outputs);
        foreach (var artifact in artifacts)
        {
            await RequireValidArtifactAsync(artifact, cancellationToken);
        }
        await ValidateResearchRunPrerequisitesAsync(manifest, cancellationToken);

        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        var manifestArtifact = Artifact(
            bytes,
            EvidenceObjectNamespaces.ResearchRunManifests,
            "application/vnd.tradingflow.evidence-research-run+json");
        var publication = await PrepareManifestPublicationAsync(
            EvidencePublicationOwnerKind.ResearchRun,
            manifest.ResearchRunId,
            manifestArtifact,
            Canonical(manifest),
            DateTimeOffset.UtcNow,
            cancellationToken);
        try
        {
            await artifactStore.PutIfAbsentAsync(
                new(manifestArtifact),
                bytes,
                cancellationToken);
            await MarkPublicationObjectPublishedAsync(
                publication.PublicationId,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkPublicationFailureAsync(
                publication.PublicationId,
                exception.Message,
                DateTimeOffset.UtcNow,
                cancellationToken);
            throw;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        foreach (var dataset in manifest.InputDatasets)
        {
            await RequireDatasetReferenceAsync(connection, transaction, dataset, cancellationToken);
        }

        var json = Canonical(manifest);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM research_runs WHERE research_run_id = $id;",
            cancellationToken,
            ("$id", manifest.ResearchRunId));
        if (existing is not null)
        {
            EnsureSame(existing, json, "research run", manifest.ResearchRunId);
            await RequireMatchingHoldoutConsumptionAsync(
                connection,
                transaction,
                manifest,
                cancellationToken);
            await UpdatePublicationStateAsync(
                connection,
                transaction,
                publication.PublicationId,
                EvidencePublicationState.CatalogCommitted,
                DateTimeOffset.UtcNow,
                publication.AttemptCount,
                null,
                cancellationToken);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(manifest.ResearchRunId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO research_runs(
                research_run_id, manifest_namespace, manifest_sha256, canonical_json)
            VALUES($id, $namespace, $sha, $json);
            """,
            cancellationToken,
            ("$id", manifest.ResearchRunId),
            ("$namespace", manifestArtifact.ObjectNamespace.Value),
            ("$sha", manifestArtifact.Content.Sha256),
            ("$json", json));
        if (manifest.Holdout is not null)
        {
            var holdout = manifest.Holdout;
            var existingConsumption = Deserialize<EvidenceHoldoutConsumption>(
                await ScalarStringAsync(
                    connection,
                    transaction,
                    "SELECT canonical_json FROM holdout_consumptions WHERE holdout_id = $id;",
                    cancellationToken,
                    ("$id", holdout.HoldoutId)));
            if (existingConsumption is not null)
            {
                var expected = new EvidenceHoldoutConsumption(
                    holdout.HoldoutId,
                    manifest.ResearchRunId,
                    manifest.CreatedAtUtc);
                if (existingConsumption != expected)
                {
                    throw new InvalidOperationException(
                        $"Holdout '{holdout.HoldoutId}' was already consumed by research run " +
                        $"'{existingConsumption.ResearchRunId}'.");
                }
            }
            else
            {
                var consumption = new EvidenceHoldoutConsumption(
                    holdout.HoldoutId,
                    manifest.ResearchRunId,
                    manifest.CreatedAtUtc);
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO holdout_consumptions(
                        holdout_id, research_run_id, consumed_at_utc, canonical_json)
                    VALUES($id, $run, $consumed, $json);
                    """,
                    cancellationToken,
                    ("$id", consumption.HoldoutId),
                    ("$run", consumption.ResearchRunId),
                    ("$consumed", Utc(consumption.ConsumedAtUtc)),
                    ("$json", Canonical(consumption)));
            }
        }
        await RegisterArtifactReferenceAsync(
            connection,
            transaction,
            EvidencePublicationOwnerKind.ResearchRun,
            manifest.ResearchRunId,
            "manifest",
            manifestArtifact,
            EvidenceArtifactKind.ResearchRunManifest,
            manifest.CreatedAtUtc,
            cancellationToken);
        var ordinal = 0;
        foreach (var artifact in artifacts)
        {
            await RegisterArtifactReferenceAsync(
                connection,
                transaction,
                EvidencePublicationOwnerKind.ResearchRun,
                manifest.ResearchRunId,
                $"artifact:{ordinal++:D4}",
                artifact,
                EvidenceArtifactKind.ResearchInput,
                manifest.CreatedAtUtc,
                cancellationToken);
        }
        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publication.PublicationId,
            EvidencePublicationState.CatalogCommitted,
            DateTimeOffset.UtcNow,
            publication.AttemptCount,
            null,
            cancellationToken);
        transaction.Commit();
        return new EvidenceCatalogCommitResult(manifest.ResearchRunId, false);
    }

    public async Task<EvidenceResearchRunManifest?> GetResearchRunAsync(
        string researchRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadRowAsync(
            connection,
            null,
            """
            SELECT manifest_namespace, manifest_sha256, canonical_json
            FROM research_runs WHERE research_run_id = $id;
            """,
            cancellationToken,
            ("$id", Required(researchRunId, nameof(researchRunId))));
        return row is null
            ? null
            : await ReadAuthoritativeManifestAsync<EvidenceResearchRunManifest>(
                row[0],
                row[1],
                row[2],
                "application/vnd.tradingflow.evidence-research-run+json",
                cancellationToken);
    }

    public async Task<EvidenceCatalogCommitResult> RegisterQuarantineAsync(
        EvidenceQuarantineRecord quarantine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quarantine);
        await RequireValidArtifactAsync(quarantine.DiagnosticArtifact, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        foreach (var source in quarantine.Lineage.Sources)
        {
            var observation = Deserialize<EvidenceSourceObservation>(await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT source.canonical_json
                FROM source_observations AS source
                WHERE source.observation_id = $id
                  AND NOT EXISTS (
                      SELECT 1 FROM retention_tombstones AS tombstone
                      WHERE tombstone.artifact_namespace = source.artifact_namespace
                        AND tombstone.artifact_sha256 = source.artifact_sha256
                  );
                """,
                cancellationToken,
                ("$id", source.ObservationId)));
            if (observation is null || observation.ToReference() != source)
            {
                throw new InvalidOperationException(
                    $"Quarantine lineage observation '{source.ObservationId}' is missing or inconsistent.");
            }

            await RequireValidArtifactAsync(observation.Artifact, cancellationToken);
        }

        var json = Canonical(quarantine);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM quarantines WHERE quarantine_id = $id;",
            cancellationToken,
            ("$id", quarantine.QuarantineId));
        if (existing is not null)
        {
            EnsureSame(existing, json, "quarantine", quarantine.QuarantineId);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(quarantine.QuarantineId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO quarantines(quarantine_id, status, canonical_json)
            VALUES($id, $status, $json);
            """,
            cancellationToken,
            ("$id", quarantine.QuarantineId),
            ("$status", (int)EvidenceQuarantineStatus.Open),
            ("$json", json));
        transaction.Commit();
        return new EvidenceCatalogCommitResult(quarantine.QuarantineId, false);
    }

    public async Task<EvidenceQuarantineEntry?> GetQuarantineAsync(
        string quarantineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadQuarantineEntryAsync(
            connection,
            null,
            Required(quarantineId, nameof(quarantineId)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<EvidenceQuarantineEntry>> FindQuarantinesAsync(
        EvidenceQuarantineStatus status,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        await using var connection = await OpenAsync(cancellationToken);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT quarantine_id FROM quarantines WHERE status = $status ORDER BY quarantine_id;";
            command.Parameters.AddWithValue("$status", (int)status);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var values = new List<EvidenceQuarantineEntry>(ids.Count);
        foreach (var id in ids)
        {
            values.Add((await ReadQuarantineEntryAsync(
                connection,
                null,
                id,
                cancellationToken))!);
        }

        return values;
    }

    private static async Task<EvidenceQuarantineEntry?> ReadQuarantineEntryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string quarantineId,
        CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT status, canonical_json FROM quarantines WHERE quarantine_id = $id;
            """,
            cancellationToken,
            ("$id", quarantineId));
        if (row is null)
        {
            return null;
        }

        var decisions = new List<EvidenceQuarantineResolution>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT canonical_json FROM quarantine_decisions
            WHERE quarantine_id = $id ORDER BY decision_ordinal;
            """;
        command.Parameters.AddWithValue("$id", quarantineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            decisions.Add(EvidenceCanonicalJson.Deserialize<EvidenceQuarantineResolution>(
                Encoding.UTF8.GetBytes(reader.GetString(0))));
        }

        return new EvidenceQuarantineEntry(
            EvidenceCanonicalJson.Deserialize<EvidenceQuarantineRecord>(
                Encoding.UTF8.GetBytes(row[1])),
            (EvidenceQuarantineStatus)Int32.Parse(row[0], CultureInfo.InvariantCulture),
            decisions);
    }

    public async Task ResolveQuarantineAsync(
        EvidenceQuarantineResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var entry = await ReadQuarantineEntryAsync(
            connection,
            transaction,
            resolution.QuarantineId,
            cancellationToken)
            ?? throw new InvalidOperationException("Quarantine does not exist.");
        var lastDecision = entry.Decisions.LastOrDefault();
        if (lastDecision?.DecisionId.Equals(
                resolution.DecisionId,
                StringComparison.Ordinal) == true)
        {
            transaction.Commit();
            return;
        }

        if (entry.Decisions.Any(decision =>
                decision.DecisionId.Equals(resolution.DecisionId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A stale quarantine decision cannot be replayed after later decisions.");
        }

        if (resolution.Status == EvidenceQuarantineStatus.RetryScheduled &&
            !entry.Record.Retryable)
        {
            throw new InvalidOperationException("This quarantine is not eligible for retry.");
        }

        EvidenceQuarantineTransitions.EnsureAllowed(entry.Status, resolution.Status);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO quarantine_decisions(
                quarantine_id, decision_ordinal, status, decided_at_utc, canonical_json)
            VALUES($id, $ordinal, $status, $decided, $json);
            """,
            cancellationToken,
            ("$id", resolution.QuarantineId),
            ("$ordinal", entry.Decisions.Count + 1),
            ("$status", (int)resolution.Status),
            ("$decided", Utc(resolution.DecidedAtUtc)),
            ("$json", Canonical(resolution)));
        var affected = await ExecuteAsync(
            connection,
            transaction,
            "UPDATE quarantines SET status = $status WHERE quarantine_id = $id;",
            cancellationToken,
            ("$status", (int)resolution.Status),
            ("$id", resolution.QuarantineId));
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "Quarantine status update failed.");
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<EvidenceArtifactReference>> ResolveReferenceClosureAsync(
        EvidencePinSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var connection = await OpenAsync(cancellationToken);
        return await ResolveClosureAsync(connection, null, subject, cancellationToken);
    }

    public async Task<EvidenceRetentionPin> PinAsync(
        EvidencePinSubject subject,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var closure = await ResolveClosureAsync(connection, transaction, subject, cancellationToken);
        var pin = new EvidenceRetentionPin(
            $"pin-{Guid.NewGuid():N}",
            subject,
            reason,
            DateTimeOffset.UtcNow,
            closure);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO retention_pins(pin_id, subject_kind, subject_id, canonical_json)
            VALUES($id, $kind, $subject, $json);
            """,
            cancellationToken,
            ("$id", pin.PinId),
            ("$kind", (int)subject.Kind),
            ("$subject", subject.SubjectId),
            ("$json", Canonical(pin)));
        foreach (var artifact in closure)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO retention_pin_artifacts(
                    pin_id, artifact_namespace, artifact_sha256)
                VALUES($pin, $namespace, $sha);
                """,
                cancellationToken,
                ("$pin", pin.PinId),
                ("$namespace", artifact.ObjectNamespace.Value),
                ("$sha", artifact.Content.Sha256));
        }

        transaction.Commit();
        return pin;
    }

    public async Task<EvidenceCatalogCommitResult> ReserveHoldoutAsync(
        EvidenceHoldoutConsumption consumption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumption);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var canonical = Canonical(consumption);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM holdout_consumptions WHERE holdout_id = $id;",
            cancellationToken,
            ("$id", consumption.HoldoutId));
        if (existing is not null)
        {
            EnsureSame(
                existing,
                canonical,
                "holdout consumption",
                consumption.HoldoutId);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(consumption.HoldoutId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO holdout_consumptions(
                holdout_id, research_run_id, consumed_at_utc, canonical_json)
            VALUES($id, $run, $consumed, $json);
            """,
            cancellationToken,
            ("$id", consumption.HoldoutId),
            ("$run", consumption.ResearchRunId),
            ("$consumed", Utc(consumption.ConsumedAtUtc)),
            ("$json", canonical));
        transaction.Commit();
        return new EvidenceCatalogCommitResult(consumption.HoldoutId, false);
    }

    public async Task<EvidenceHoldoutConsumption?> GetHoldoutConsumptionAsync(
        string holdoutId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Deserialize<EvidenceHoldoutConsumption>(await ScalarStringAsync(
            connection,
            null,
            "SELECT canonical_json FROM holdout_consumptions WHERE holdout_id = $id;",
            cancellationToken,
            ("$id", Required(holdoutId, nameof(holdoutId)))));
    }

    public async Task<EvidenceCatalogCommitResult> RegisterAsync(
        EvidenceReferenceSubjectManifest subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        foreach (var artifact in subject.ReferencedArtifacts)
        {
            await RequireArtifactAcceptsNewReferenceAsync(
                connection,
                transaction,
                artifact,
                cancellationToken);
            await RequireValidArtifactAsync(artifact, cancellationToken);
        }

        var json = Canonical(subject);
        var existing = await ScalarStringAsync(
            connection,
            transaction,
            """
            SELECT canonical_json FROM external_reference_subjects
            WHERE subject_kind = $kind AND subject_id = $id;
            """,
            cancellationToken,
            ("$kind", (int)subject.Subject.Kind),
            ("$id", subject.Subject.SubjectId));
        if (existing is not null)
        {
            EnsureSame(existing, json, "external reference subject", subject.Subject.SubjectId);
            transaction.Commit();
            return new EvidenceCatalogCommitResult(subject.Subject.SubjectId, true);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO external_reference_subjects(subject_kind, subject_id, canonical_json)
            VALUES($kind, $id, $json);
            """,
            cancellationToken,
            ("$kind", (int)subject.Subject.Kind),
            ("$id", subject.Subject.SubjectId),
            ("$json", json));
        transaction.Commit();
        return new EvidenceCatalogCommitResult(subject.Subject.SubjectId, false);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            if (openMode == EvidenceCatalogOpenMode.BootstrapNew)
            {
                await BootstrapAsync(cancellationToken);
            }
            else
            {
                await ValidateAndMigrateExistingAsync(cancellationToken);
            }

            await using var connection = await OpenUnconfiguredExistingAsync(cancellationToken);
            await SqliteDurability.ConfigureDatabaseAsync(connection, cancellationToken);
            initialized = true;
        }
        finally
        {
            initialization.Release();
        }
    }

    internal Task<SqliteConnection> OpenMaintenanceConnectionAsync(
        CancellationToken cancellationToken = default) =>
        OpenAsync(cancellationToken);

    internal string DatabasePath => databasePath;

    internal static Task ValidateCatalogIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        RequireIntegrityAsync(connection, cancellationToken);

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = databasePath + $".bootstrap-{Guid.NewGuid():N}.tmp";
        try
        {
            var temporaryConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(temporaryConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await SqliteDurability.ConfigureConnectionAsync(connection, cancellationToken);
                await ExecuteAsync(
                    connection,
                    null,
                    "PRAGMA journal_mode = DELETE;",
                    cancellationToken);
                await using (var transaction = connection.BeginTransaction(
                                 IsolationLevel.Serializable,
                                 deferred: false))
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        EvidenceCatalogSchema.CreateSql,
                        cancellationToken);
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO catalog_metadata(singleton, schema_version)
                        VALUES(1, $schemaVersion);
                        """,
                        cancellationToken,
                        ("$schemaVersion", EvidenceCatalogSchema.Version));
                    transaction.Commit();
                }

                await RequireIntegrityAsync(connection, cancellationToken);
            }

            await FlushFileAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, databasePath);
        }
        finally
        {
            DeleteBootstrapArtifact(temporaryPath);
            DeleteBootstrapArtifact(temporaryPath + "-journal");
            DeleteBootstrapArtifact(temporaryPath + "-wal");
            DeleteBootstrapArtifact(temporaryPath + "-shm");
        }
    }

    private async Task ValidateAndMigrateExistingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenUnconfiguredExistingAsync(cancellationToken);
        var metadataTable = await ScalarStringAsync(
            connection,
            null,
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'catalog_metadata';
            """,
            cancellationToken);
        if (metadataTable is null)
        {
            throw new InvalidDataException(
                "The existing database is not an initialized evidence catalog.");
        }

        var version = await ScalarStringAsync(
            connection,
            null,
            "SELECT CAST(schema_version AS TEXT) FROM catalog_metadata WHERE singleton = 1;",
            cancellationToken);
        if (version is null)
        {
            throw new InvalidDataException(
                "The existing evidence catalog has no schema metadata row.");
        }
        if (!Int32.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var schemaVersion))
        {
            throw new InvalidDataException(
                $"Evidence catalog schema version '{version}' is invalid.");
        }

        if (schemaVersion == 4)
        {
            await RequireSchemaMatchesAsync(
                connection,
                EvidenceCatalogSchema.Version4CreateSql,
                "Evidence catalog version 4 schema structure is incomplete or modified.",
                cancellationToken);
            await MigrateVersion4ToVersion5Async(connection, cancellationToken);
            schemaVersion = 5;
        }

        if (schemaVersion != EvidenceCatalogSchema.Version)
        {
            throw new InvalidOperationException(
                $"Evidence catalog schema {version} is not supported.");
        }

        await RequireIntegrityAsync(connection, cancellationToken);
    }

    private static async Task MigrateVersion4ToVersion5Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            ALTER TABLE holdout_consumptions RENAME TO holdout_consumptions_v4;

            CREATE TABLE holdout_consumptions (
                holdout_id TEXT NOT NULL PRIMARY KEY,
                research_run_id TEXT NOT NULL,
                consumed_at_utc TEXT NOT NULL,
                canonical_json TEXT NOT NULL
            );

            INSERT INTO holdout_consumptions(
                holdout_id, research_run_id, consumed_at_utc, canonical_json)
            SELECT holdout_id, research_run_id, consumed_at_utc, canonical_json
            FROM holdout_consumptions_v4;

            DROP TABLE holdout_consumptions_v4;

            UPDATE catalog_metadata
            SET schema_version = 5
            WHERE singleton = 1;
            """,
            cancellationToken);
        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await OpenCoreAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenCoreAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteDurability.ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private async Task<SqliteConnection> OpenUnconfiguredExistingAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task RequireIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var integrity = await ScalarStringAsync(
            connection,
            null,
            "PRAGMA quick_check;",
            cancellationToken);
        if (!String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Evidence catalog integrity check failed: {integrity ?? "no result"}.");
        }

        await using (var foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"Evidence catalog foreign-key check failed for table '{reader.GetString(0)}'.");
            }
        }

        await RequireSchemaMatchesAsync(
            connection,
            EvidenceCatalogSchema.CreateSql,
            "Evidence catalog schema structure does not match the binding schema version.",
            cancellationToken);
    }

    private static async Task RequireSchemaMatchesAsync(
        SqliteConnection connection,
        string expectedCreateSql,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var actualSchema = await ReadSchemaDefinitionAsync(connection, cancellationToken);
        await using var expectedConnection = new SqliteConnection(
            "Data Source=:memory:;Mode=Memory;Cache=Private;Pooling=False");
        await expectedConnection.OpenAsync(cancellationToken);
        await ExecuteAsync(
            expectedConnection,
            null,
            expectedCreateSql,
            cancellationToken);
        var expectedSchema = await ReadSchemaDefinitionAsync(
            expectedConnection,
            cancellationToken);
        if (!actualSchema.Equals(expectedSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(failureMessage);
        }
    }

    private static async Task<string> ReadSchemaDefinitionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name, tbl_name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(String.Join(
                "\u001f",
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return String.Join("\u001e", rows);
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
            bufferSize: 1,
            FileOptions.Asynchronous);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteBootstrapArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must not replace the bootstrap failure with a secondary exception.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the bootstrap failure with a secondary exception.
        }
    }

    private async Task<EvidenceCollectionPlan> RequirePlanAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobId,
        CancellationToken cancellationToken)
    {
        var plan = Deserialize<EvidenceCollectionPlan>(await ScalarStringAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM collection_plans WHERE job_id = $id;",
            cancellationToken,
            ("$id", jobId)));
        return plan ?? throw new InvalidOperationException(
            $"Evidence collection plan '{jobId}' is not registered.");
    }

    private async Task<EvidencePublicationRecord> EnsurePublicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidencePublicationOwnerKind ownerKind,
        string ownerId,
        EvidenceArtifactReference artifact,
        string canonicalOwner,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken,
        EvidencePublicationState initialState = EvidencePublicationState.ObjectPublished)
    {
        var publicationId = PublicationId(ownerKind, ownerId);
        var existing = await ReadPublicationAsync(
            connection,
            transaction,
            publicationId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.OwnerKind != ownerKind ||
                !existing.OwnerId.Equals(ownerId, StringComparison.Ordinal) ||
                existing.Artifact != artifact ||
                !existing.CanonicalOwnerJson.Equals(canonicalOwner, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Publication '{publicationId}' conflicts with its immutable owner.");
            }

            return existing;
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO artifact_publications(
                publication_id, owner_kind, owner_id, state, artifact_namespace,
                artifact_sha256, artifact_byte_length, artifact_media_type,
                canonical_owner_json, created_at_utc, updated_at_utc,
                attempt_count, last_error)
            VALUES(
                $publication, $ownerKind, $ownerId, $state, $namespace,
                $sha, $length, $media, $ownerJson, $created, $updated, 0, NULL);
            """,
            cancellationToken,
            ("$publication", publicationId),
            ("$ownerKind", (int)ownerKind),
            ("$ownerId", ownerId),
            ("$state", (int)initialState),
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256),
            ("$length", artifact.Content.ByteLength),
            ("$media", artifact.Content.MediaType),
            ("$ownerJson", canonicalOwner),
            ("$created", Utc(createdAtUtc)),
            ("$updated", Utc(createdAtUtc)));
        return new EvidencePublicationRecord(
            publicationId,
            ownerKind,
            ownerId,
            initialState,
            artifact,
            canonicalOwner,
            createdAtUtc,
            createdAtUtc,
            0,
            null);
    }

    private async Task<EvidencePublicationRecord> RequirePublicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string publicationId,
        CancellationToken cancellationToken) =>
        await ReadPublicationAsync(
            connection,
            transaction,
            Required(publicationId, nameof(publicationId)),
            cancellationToken)
        ?? throw new InvalidOperationException($"Publication '{publicationId}' is not registered.");

    private static async Task<EvidencePublicationRecord?> ReadPublicationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string publicationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT publication_id, owner_kind, owner_id, state,
                   artifact_namespace, artifact_sha256, artifact_byte_length,
                   artifact_media_type, canonical_owner_json, created_at_utc,
                   updated_at_utc, attempt_count, last_error
            FROM artifact_publications WHERE publication_id = $id;
            """;
        command.Parameters.AddWithValue("$id", publicationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPublication(reader)
            : null;
    }

    private static EvidencePublicationRecord ReadPublication(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            (EvidencePublicationOwnerKind)reader.GetInt32(1),
            reader.GetString(2),
            (EvidencePublicationState)reader.GetInt32(3),
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7)),
                new EvidenceObjectNamespace(reader.GetString(4))),
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    private static Task<int> UpdatePublicationStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string publicationId,
        EvidencePublicationState state,
        DateTimeOffset updatedAtUtc,
        int attemptCount,
        string? lastError,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE artifact_publications
            SET state = $state, updated_at_utc = $updated,
                attempt_count = $attempts, last_error = $error
            WHERE publication_id = $id;
            """,
            cancellationToken,
            ("$state", (int)state),
            ("$updated", Utc(updatedAtUtc)),
            ("$attempts", attemptCount),
            ("$error", lastError),
            ("$id", publicationId));

    private static string PublicationId(
        EvidencePublicationOwnerKind ownerKind,
        string ownerId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{(int)ownerKind}:{ownerId}");
        return $"publication-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32]}";
    }

    private async Task CompleteSourcePublicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidencePublicationRecord publication,
        EvidenceSourceObservation observation,
        CancellationToken cancellationToken)
    {
        if (publication.Artifact != observation.Artifact ||
            !publication.CanonicalOwnerJson.Equals(Canonical(observation), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Publication '{publication.PublicationId}' does not match the source observation.");
        }

        await RegisterArtifactReferenceAsync(
            connection,
            transaction,
            EvidencePublicationOwnerKind.SourceObservation,
            observation.ObservationId,
            "raw_payload",
            observation.Artifact,
            EvidenceArtifactKind.RawObservation,
            observation.ReceivedAtUtc,
            cancellationToken);
        await UpdatePublicationStateAsync(
            connection,
            transaction,
            publication.PublicationId,
            EvidencePublicationState.CatalogCommitted,
            DateTimeOffset.UtcNow,
            publication.AttemptCount,
            null,
            cancellationToken);
    }

    private static async Task RegisterArtifactReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidencePublicationOwnerKind ownerKind,
        string ownerId,
        string role,
        EvidenceArtifactReference artifact,
        EvidenceArtifactKind artifactKind,
        DateTimeOffset referenceTimeUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO catalog_artifacts(
                artifact_namespace, artifact_sha256, artifact_byte_length,
                artifact_media_type, artifact_kind, status, cataloged_at_utc)
            VALUES($namespace, $sha, $length, $media, $kind, $status, $cataloged)
            ON CONFLICT(artifact_namespace, artifact_sha256) DO NOTHING;
            """,
            cancellationToken,
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256),
            ("$length", artifact.Content.ByteLength),
            ("$media", artifact.Content.MediaType),
            ("$kind", (int)artifactKind),
            ("$status", (int)EvidenceArtifactLifecycleStatus.Active),
            ("$cataloged", Utc(referenceTimeUtc)));
        var row = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT artifact_byte_length, artifact_media_type, status
            FROM catalog_artifacts
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            cancellationToken,
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256));
        if (row is null ||
            !Int64.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var byteLength) ||
            byteLength != artifact.Content.ByteLength ||
            !row[1].Equals(artifact.Content.MediaType, StringComparison.Ordinal) ||
            !Int32.TryParse(row[2], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ||
            status != (int)EvidenceArtifactLifecycleStatus.Active)
        {
            throw new InvalidDataException(
                "Catalog artifact identity conflicts with an existing or retired content address.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO artifact_references(
                owner_kind, owner_id, role, artifact_namespace,
                artifact_sha256, reference_time_utc)
            VALUES($ownerKind, $ownerId, $role, $namespace, $sha, $referenceTime)
            ON CONFLICT DO NOTHING;
            """,
            cancellationToken,
            ("$ownerKind", (int)ownerKind),
            ("$ownerId", ownerId),
            ("$role", role),
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256),
            ("$referenceTime", Utc(referenceTimeUtc)));
    }

    private static async Task RequireArtifactAcceptsNewReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        var tombstone = await ScalarStringAsync(
            connection,
            transaction,
            """
            SELECT tombstone_id
            FROM retention_tombstones
            WHERE artifact_namespace = $namespace AND artifact_sha256 = $sha;
            """,
            cancellationToken,
            ("$namespace", artifact.ObjectNamespace.Value),
            ("$sha", artifact.Content.Sha256));
        if (tombstone is not null)
        {
            throw new InvalidOperationException(
                $"Artifact {artifact.ObjectNamespace}/{artifact.Content.Sha256} is retired.");
        }
    }

    private async Task ValidateDatasetPrerequisitesAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        await RequireDatasetLineageAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        transaction.Commit();
    }

    private async Task ValidateResearchRunPrerequisitesAsync(
        EvidenceResearchRunManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        foreach (var dataset in manifest.InputDatasets)
        {
            await RequireDatasetReferenceAsync(
                connection,
                transaction,
                dataset,
                cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task RequireMatchingHoldoutConsumptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceResearchRunManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!manifest.EvidenceReady)
        {
            return;
        }

        var consumption = Deserialize<EvidenceHoldoutConsumption>(
            await ScalarStringAsync(
                connection,
                transaction,
                "SELECT canonical_json FROM holdout_consumptions WHERE holdout_id = $id;",
                cancellationToken,
                ("$id", manifest.Holdout!.HoldoutId)));
        if (consumption is null ||
            !consumption.ResearchRunId.Equals(manifest.ResearchRunId, StringComparison.Ordinal) ||
            consumption.ConsumedAtUtc != manifest.CreatedAtUtc)
        {
            throw new InvalidDataException(
                "The committed research run and its holdout consumption are inconsistent.");
        }
    }

    private async Task RequireDatasetLineageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken)
    {
        var plan = await RequirePlanAsync(
            connection,
            transaction,
            manifest.CollectionJobId,
            cancellationToken);
        if (!plan.LogicalPlanHash.Equals(manifest.CollectionPlanHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Dataset collection plan hash does not match its run.");
        }

        foreach (var source in manifest.Partitions.SelectMany(partition => partition.SourceObservations))
        {
            var observation = Deserialize<EvidenceSourceObservation>(await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT source.canonical_json
                FROM source_observations AS source
                WHERE source.observation_id = $id
                  AND NOT EXISTS (
                      SELECT 1 FROM retention_tombstones AS tombstone
                      WHERE tombstone.artifact_namespace = source.artifact_namespace
                        AND tombstone.artifact_sha256 = source.artifact_sha256
                  );
                """,
                cancellationToken,
                ("$id", source.ObservationId)));
            if (observation is null ||
                observation.ToReference() != source)
            {
                throw new InvalidOperationException(
                    $"Dataset source observation '{source.ObservationId}' is missing or inconsistent.");
            }

            if (!observation.CollectionJobId.Equals(manifest.CollectionJobId, StringComparison.Ordinal) ||
                !observation.CollectionPlanHash.Equals(manifest.CollectionPlanHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dataset source observation '{source.ObservationId}' belongs to a different collection attempt.");
            }

            await RequireValidArtifactAsync(observation.Artifact, cancellationToken);
        }
    }

    private async Task RequireDatasetReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EvidenceDatasetReference reference,
        CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT manifest_namespace, manifest_sha256, canonical_json
            FROM datasets WHERE dataset_id = $id;
            """,
            cancellationToken,
            ("$id", reference.DatasetId));
        if (row is null)
        {
            throw new InvalidOperationException(
                $"Dataset reference '{reference.DatasetId}' is missing or has a manifest mismatch.");
        }

        await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
            row[0],
            row[1],
            row[2],
            "application/vnd.tradingflow.evidence-dataset+json",
            cancellationToken);
        var canonicalBytes = Encoding.UTF8.GetBytes(row[2]);
        var authoritativeReference = Reference(
            row[0],
            row[1],
            canonicalBytes.Length,
            "application/vnd.tradingflow.evidence-dataset+json");
        if (reference.ManifestArtifact != authoritativeReference)
        {
            throw new InvalidOperationException(
                $"Dataset reference '{reference.DatasetId}' is missing or has a manifest mismatch.");
        }
    }

    private async Task<IReadOnlyList<EvidenceArtifactReference>> ResolveClosureAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvidencePinSubject subject,
        CancellationToken cancellationToken)
    {
        var artifacts = new HashSet<EvidenceArtifactReference>();
        if (subject.Kind == EvidencePinSubjectKind.Dataset)
        {
            await AddDatasetClosureAsync(
                connection,
                transaction,
                subject.SubjectId,
                artifacts,
                cancellationToken);
        }
        else if (subject.Kind == EvidencePinSubjectKind.ResearchRun)
        {
            var row = await ReadRowAsync(
                connection,
                transaction,
                """
                SELECT manifest_namespace, manifest_sha256, canonical_json
                FROM research_runs WHERE research_run_id = $id;
                """,
                cancellationToken,
                ("$id", subject.SubjectId))
                ?? throw new InvalidOperationException($"Research run '{subject.SubjectId}' is not registered.");
            var run = await ReadAuthoritativeManifestAsync<EvidenceResearchRunManifest>(
                row[0],
                row[1],
                row[2],
                "application/vnd.tradingflow.evidence-research-run+json",
                cancellationToken);
            artifacts.Add(Reference(row[0], row[1], Encoding.UTF8.GetByteCount(row[2]),
                "application/vnd.tradingflow.evidence-research-run+json"));
            artifacts.Add(run.UniverseLedgerArtifact);
            artifacts.Add(run.StudyConfigArtifact);
            artifacts.Add(run.Assumptions.CostModel);
            artifacts.Add(run.Assumptions.SpreadModel);
            artifacts.Add(run.Assumptions.SlippageModel);
            artifacts.Add(run.Assumptions.BorrowModel);
            artifacts.Add(run.Assumptions.BenchmarkDefinition);
            if (run.Holdout is not null)
            {
                artifacts.Add(run.Holdout.PartitionDefinitionArtifact);
            }

            foreach (var output in run.Outputs)
            {
                artifacts.Add(output);
            }

            foreach (var dataset in run.InputDatasets)
            {
                await AddDatasetClosureAsync(
                    connection,
                    transaction,
                    dataset.DatasetId,
                    artifacts,
                    cancellationToken);
            }
        }
        else
        {
            var json = await ScalarStringAsync(
                connection,
                transaction,
                """
                SELECT canonical_json FROM external_reference_subjects
                WHERE subject_kind = $kind AND subject_id = $id;
                """,
                cancellationToken,
                ("$kind", (int)subject.Kind),
                ("$id", subject.SubjectId))
                ?? throw new InvalidOperationException(
                    $"External reference subject '{subject.Kind}:{subject.SubjectId}' is not registered.");
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceReferenceSubjectManifest>(
                Encoding.UTF8.GetBytes(json));
            foreach (var artifact in manifest.ReferencedArtifacts)
            {
                artifacts.Add(artifact);
            }
        }

        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException("Reference subject resolved to an empty closure.");
        }

        return artifacts
            .OrderBy(value => value.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Content.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task AddDatasetClosureAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string datasetId,
        ISet<EvidenceArtifactReference> artifacts,
        CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(
            connection,
            transaction,
            """
            SELECT manifest_namespace, manifest_sha256, canonical_json
            FROM datasets WHERE dataset_id = $id;
            """,
            cancellationToken,
            ("$id", datasetId))
            ?? throw new InvalidOperationException($"Dataset '{datasetId}' is not registered.");
        var dataset = await ReadAuthoritativeManifestAsync<EvidenceDatasetManifest>(
            row[0],
            row[1],
            row[2],
            "application/vnd.tradingflow.evidence-dataset+json",
            cancellationToken);
        artifacts.Add(Reference(
            row[0],
            row[1],
            Encoding.UTF8.GetByteCount(row[2]),
            "application/vnd.tradingflow.evidence-dataset+json"));
        foreach (var partition in dataset.Partitions)
        {
            artifacts.Add(partition.Artifact);
            if (partition.QuarantineLedger is not null)
            {
                artifacts.Add(partition.QuarantineLedger);
            }

            foreach (var source in partition.SourceObservations)
            {
                artifacts.Add(source.Artifact);
            }
        }
    }

    private async Task RequireValidArtifactAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        var verification = await artifactStore.VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Evidence artifact {artifact.ObjectNamespace}/{artifact.Content.Sha256} " +
                $"is unavailable or corrupt: {verification.FailureReason}");
        }
    }

    private async Task<T> ReadAuthoritativeManifestAsync<T>(
        string objectNamespace,
        string sha256,
        string canonicalJson,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var canonicalHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!canonicalHash.Equals(sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Catalog JSON for immutable manifest {objectNamespace}/{sha256} does not match its hash.");
        }

        var artifact = Reference(objectNamespace, sha256, bytes.Length, mediaType);
        await RequireValidArtifactAsync(artifact, cancellationToken);
        return EvidenceCanonicalJson.Deserialize<T>(bytes);
    }

    private static EvidenceArtifactReference Artifact(
        byte[] bytes,
        EvidenceObjectNamespace objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant(),
                bytes.Length,
                mediaType),
            objectNamespace);

    private static EvidenceArtifactReference Reference(
        string objectNamespace,
        string sha256,
        int byteLength,
        string mediaType) =>
        new(
            new EvidenceContentAddress(sha256, byteLength, mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static string Canonical<T>(T value) =>
        Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(value));

    private static T? Deserialize<T>(string? json) =>
        json is null
            ? default
            : EvidenceCanonicalJson.Deserialize<T>(Encoding.UTF8.GetBytes(json));

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static void EnsureSame(
        string existing,
        string candidate,
        string entity,
        string id)
    {
        if (!existing.Equals(candidate, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Immutable {entity} '{id}' was reused with different content.");
        }
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string[]?> ReadRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetString(index);
        }

        return values;
    }

    private sealed record AtomicDatasetCommit(
        EvidenceDatasetManifest Manifest,
        EvidenceArtifactReference ManifestArtifact);
}
