using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Governance;

public sealed partial class SqliteResearchTrialRegistry : IResearchStartRegistry
{
    private readonly ResearchAdmissionReviews reviews;
    private readonly TimeProvider clock;
    private readonly string databasePath;

    /// <summary>Allocates all intended variant ordinals and freezes the binding in one registry transaction.</summary>
    public async Task<ResearchStart> ReserveStartAsync(FrozenResearchStart request, CancellationToken token)
    {
        RequireFrozenRequest(request);
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadStartAsync(connection, transaction, request.StartId, token);
        if (prior is not null)
        {
            if (EvidenceCanonicalJson.ComputeSha256(prior.Binding.Request) != EvidenceCanonicalJson.ComputeSha256(request))
                throw new IdempotencyKeyConflictException(request.StartId);
            await RequireBoundTrialsAsync(connection, transaction, prior.Binding, token);
            return prior;
        }

        var (review, replay, refusal, observationCutoff) = await ReviewStartAsync(connection, transaction, request, token);
        var now = clock.GetUtcNow();
        var trials = new List<ResearchStartTrial>();
        if (refusal is null && !replay)
        {
            foreach (var variant in request.Variants)
                trials.Add(await RegisterVariantAsync(connection, transaction, request, variant, now, token));
        }
        var binding = new ResearchStartBinding(request, trials, review, replay, observationCutoff);
        var state = refusal is null ? ResearchStartState.Pending : ResearchStartState.Refused;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO research_starts(start_id, canonical_json, sha256, state, reason, registered_at_utc, updated_at_utc)
            VALUES($id, $json, $hash, $state, $reason, $now, $now);
            """, token, ("$id", request.StartId), ("$json", Canonical(binding)), ("$hash", EvidenceCanonicalJson.ComputeSha256(binding)),
            ("$state", state.ToString()), ("$reason", refusal), ("$now", Utc(now)));
        transaction.Commit();
        return new(binding, state, refusal, now, now);
    }

    /// <summary>Refuses malformed start matrices and reserves holdout operations for the existing one-use path.</summary>
    private static void RequireFrozenRequest(FrozenResearchStart request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartId);
        _ = ResearchStartIdentity.RequireSha256(request.RequestSha256);
        _ = ResearchStartIdentity.RequireSha256(request.CodeSha256);
        var variants = request.Variants.Select(value => value.VariantKey).ToHashSet(StringComparer.Ordinal);
        if (variants.Count == 0 || variants.Count != request.Variants.Count || variants.Any(String.IsNullOrWhiteSpace) ||
            request.Executions.Count == 0 || request.Executions.Any(pair => String.IsNullOrWhiteSpace(pair.Key) || pair.Value.Length == 0 ||
                pair.Value.Distinct(StringComparer.Ordinal).Count() != pair.Value.Length) ||
            !variants.SetEquals(request.Executions.Values.SelectMany(keys => keys)))
            throw new InvalidDataException("Each frozen research variant must belong to an exact intended execution.");
        foreach (var variant in request.Variants)
        {
            _ = ResearchStartIdentity.RequireSha256(variant.RuleSha256);
            if (variant.Partitions.Any(partition => partition.StartUtc.Year < 2016))
                throw new InvalidDataException("A research partition cannot begin before the 2016 evidence floor.");
        }
    }

    /// <summary>Checks only deployment-owned review records. The caller cannot select replay or claim a study passed.</summary>
    private async Task<(string? Review, bool Replay, string? Refusal, DateTimeOffset? ObservationCutoff)> ReviewStartAsync(SqliteConnection connection,
        SqliteTransaction transaction, FrozenResearchStart request, CancellationToken token)
    {
        if (request.Kind is ResearchStartKind.SignalStudy or ResearchStartKind.CatalogStudy)
            return (null, false, null, null);
        var replay = reviews.DiagnosticReplays.SingleOrDefault(entry => entry.RequestSha256 == request.RequestSha256);
        if (replay is not null)
        {
            _ = ResearchStartIdentity.RequireSha256(replay.OriginalArtifactSha256);
            ArgumentException.ThrowIfNullOrWhiteSpace(replay.ReviewReference);
            if (replay.ObservationCutoffUtc.Offset != TimeSpan.Zero ||
                request.Variants.Any(variant => variant.Partitions.Count == 0 ||
                    variant.Partitions.Any(partition => partition.EndUtc > replay.ObservationCutoffUtc)) ||
                !File.Exists(replay.OriginalArtifactPath) || ResearchStartIdentity.HashFile(replay.OriginalArtifactPath) != replay.OriginalArtifactSha256)
                return (null, false, "The exact diagnostic replay needs its verified original artifact and bounded historical observation window.", null);
            return ($"{replay.ReviewReference} · original artifact SHA-256 {replay.OriginalArtifactSha256}",
                true, null, replay.ObservationCutoffUtc);
        }

        var verified = new List<string>();
        var checkedStudies = new Dictionary<(ReviewedSignalStudy Review, string PartitionsSha256), DateTimeOffset?>();
        DateTimeOffset? earliestCutoff = null;
        foreach (var variant in request.Variants)
        {
            if (!variant.DatasetEvidenceFrozen)
                return (null, false, $"Research for '{variant.FamilyId}' needs exact frozen datasets, price basis and costs, " +
                    $"or a reviewed diagnostic replay for request {request.RequestSha256}.", null);
            var review = reviews.SignalStudies.SingleOrDefault(entry => entry.PortfolioRequestSha256 == request.RequestSha256 &&
                entry.FamilyId == variant.FamilyId && entry.RuleSha256 == variant.RuleSha256);
            DateTimeOffset? cutoff = null;
            if (review is not null)
            {
                var key = (review, EvidenceCanonicalJson.ComputeSha256(variant.Partitions));
                if (!checkedStudies.TryGetValue(key, out cutoff))
                {
                    cutoff = await VerifyStudyReviewAsync(connection, transaction, review, variant.Partitions, token);
                    checkedStudies.Add(key, cutoff);
                }
            }
            if (review is null || cutoff is null)
                return (null, false, $"Portfolio research for '{variant.FamilyId}' needs a reviewed successful frozen signal study " +
                    $"for this exact variant and request ({request.RequestSha256}).", null);
            if (earliestCutoff is null || cutoff < earliestCutoff) earliestCutoff = cutoff;
            verified.Add($"{review.ReviewReference} · {review.ExperimentId} · {review.ResultId} · {review.StudyManifestSha256}");
        }
        if (request.Variants.SelectMany(variant => variant.Partitions).Any(partition => partition.EndUtc > earliestCutoff))
            return (null, false, "The variants' reviewed development windows do not cover the same execution window.", null);
        return (String.Join(" / ", verified.Distinct(StringComparer.Ordinal)), false, null, earliestCutoff);
    }

    /// <summary>Verifies the exact frozen trial, result and published manifest hash, and checks the study's pass verdict.</summary>
    private static async Task<DateTimeOffset?> VerifyStudyReviewAsync(SqliteConnection connection, SqliteTransaction transaction,
        ReviewedSignalStudy review, IReadOnlyList<ResearchPartitionDefinition> portfolioPartitions, CancellationToken token)
    {
        var trial = await ReadVerifiedRowAsync<ResearchTrialDefinition>(connection, transaction,
            "SELECT canonical_json, sha256 FROM research_trials WHERE experiment_id=$id;", review.ExperimentId, token);
        var result = await ReadVerifiedRowAsync<CanonicalResearchResultManifest>(connection, transaction,
            "SELECT canonical_json, sha256 FROM research_results WHERE result_id=$id;", review.ResultId, token);
        if (trial is null || result is null || String.IsNullOrWhiteSpace(review.ReviewReference)) return null;
        var development = trial.Value.Value.Partitions.SingleOrDefault(partition => partition.Name == "development");
        var exact = development is not null && trial.Value.Value.ExperimentId == review.ExperimentId && trial.Value.Hash == review.TrialDefinitionSha256 &&
            trial.Value.Value.FamilyId == review.FamilyId &&
            trial.Value.Value.Formulas.GetValueOrDefault("start_kind") == nameof(ResearchStartKind.SignalStudy) &&
            trial.Value.Value.Formulas.GetValueOrDefault("rule_sha256") == (review.StudyRuleSha256 ?? review.RuleSha256) &&
            result.Value.Value.ResultId == review.ResultId && result.Value.Hash == review.ResultSha256 &&
            result.Value.Value.ExperimentId == review.ExperimentId && result.Value.Value.TrialDefinitionSha256 == trial.Value.Hash &&
            result.Value.Value.Partition == "development" && result.Value.Value.Metrics.GetValueOrDefault("passed") == 1m &&
            result.Value.Value.OutputArtifactSha256.Contains(review.StudyManifestSha256, StringComparer.Ordinal) &&
            !portfolioPartitions.Any(portfolio => trial.Value.Value.Partitions.Any(frozen => frozen.Name is "holdout" or "embargo" &&
                frozen.StartUtc < portfolio.EndUtc && portfolio.StartUtc < frozen.EndUtc)) &&
            portfolioPartitions.All(portfolio => development.StartUtc <= portfolio.StartUtc && portfolio.EndUtc <= development.EndUtc);
        return exact && await VerifyPublishedStudyAsync(connection, transaction, result.Value.Value, review.StudyManifestSha256, token)
            ? development!.EndUtc : null;
    }

    /// <summary>Checks the successful study start and its immutable output closure are still available before admitting a portfolio.</summary>
    private static async Task<bool> VerifyPublishedStudyAsync(SqliteConnection connection, SqliteTransaction transaction,
        CanonicalResearchResultManifest result, string manifestSha, CancellationToken token)
    {
        var study = await ReadStartAsync(connection, transaction, result.ResearchRunId, token);
        if (study is null || study.Binding.Request.Kind != ResearchStartKind.SignalStudy || study.State != ResearchStartState.Admitted ||
            study.Binding.Request.Executions.Count != 1 ||
            !study.Binding.Trials.Any(trial => trial.ExperimentId == result.ExperimentId && trial.DefinitionSha256 == result.TrialDefinitionSha256))
            return false;
        var execution = await ReadOutcomeAsync(connection, transaction, result.ResearchRunId, study.Binding.Request.Executions.Keys.Single(), token);
        if (execution?.State != "completed" || execution.ArtifactSha256 != manifestSha || execution.ArtifactReference is not { } path)
            return false;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token);
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != manifestSha) return false;
            using var document = JsonDocument.Parse(bytes);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var publishedHashes = new HashSet<string>(StringComparer.Ordinal) { manifestSha };
            foreach (var variant in document.RootElement.GetProperty("variants").EnumerateArray())
            foreach (var artifact in variant.GetProperty("artifacts").EnumerateObject())
            {
                if (String.IsNullOrWhiteSpace(artifact.Name) || Path.GetFileName(artifact.Name) != artifact.Name ||
                    ResearchStartIdentity.HashFile(Path.Combine(directory, artifact.Name)) != artifact.Value.GetString())
                    return false;
                publishedHashes.Add(artifact.Value.GetString()!);
            }
            return publishedHashes.Count > 1 && result.OutputArtifactSha256.All(publishedHashes.Contains);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Stores the same canonical trial contract used by the independent trial and holdout APIs.</summary>
    private static async Task<ResearchStartTrial> RegisterVariantAsync(SqliteConnection connection, SqliteTransaction transaction,
        FrozenResearchStart request, FrozenResearchVariant variant, DateTimeOffset now, CancellationToken token)
    {
        var ordinal = await ReadCountAsync(connection, transaction,
            "SELECT COUNT(*) FROM research_trials WHERE family_id=$family;", token, ("$family", variant.FamilyId)) + 1;
        var experimentId = "trial-" + EvidenceCanonicalJson.ComputeSha256(new { request.StartId, variant.VariantKey });
        var formulas = variant.Formulas.ToDictionary();
        formulas.Add("start_kind", request.Kind.ToString());
        formulas.Add("rule_sha256", variant.RuleSha256);
        formulas.Add("request_sha256", request.RequestSha256);
        formulas.Add("code_sha256", request.CodeSha256);
        var definition = new ResearchTrialDefinition(experimentId, variant.FamilyId, null, checked((int)ordinal),
            variant.Hypothesis, variant.DatasetIds, variant.Clocks, formulas, variant.Thresholds, variant.Costs, variant.Partitions,
            variant.PrimaryMetric, variant.RejectionCriteria, ResearchHoldoutState.Unopened, now);
        var hash = EvidenceCanonicalJson.ComputeSha256(definition);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO research_trials(experiment_id, family_id, family_trial_count, registered_at_utc, sha256, canonical_json)
            VALUES($id, $family, $count, $now, $hash, $json);
            """, token, ("$id", experimentId), ("$family", definition.FamilyId), ("$count", ordinal), ("$now", Utc(now)),
            ("$hash", hash), ("$json", Canonical(definition)));
        return new(variant.VariantKey, experimentId, hash, definition.FamilyTrialCount);
    }

    /// <summary>Persists the first complete preparation before either database can admit any of its jobs.</summary>
    public async Task<IReadOnlyList<PersistedJob>> FreezeQueueJobsAsync(
        string startId, IReadOnlyList<PersistedJob> jobs, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var start = await ReadStartAsync(connection, transaction, startId, token)
            ?? throw new InvalidDataException("The research start was not registered.");
        if (start.State == ResearchStartState.Refused)
            throw new InvalidOperationException(start.Reason);
        if (start.Binding.Request.Kind is not (ResearchStartKind.Portfolio or ResearchStartKind.Optimization))
            throw new InvalidDataException("A signal or catalog study cannot enqueue a portfolio or optimization job.");
        var expected = start.Binding.Request.Executions.Keys.ToHashSet(StringComparer.Ordinal);
        if (jobs.Count != expected.Count || !expected.SetEquals(jobs.Select(job => job.Id.ToString("N"))))
            throw new InvalidDataException("Prepared research jobs differ from the frozen execution matrix.");
        var prior = await ReadQueueJobsAsync(connection, transaction, startId, token);
        if (prior.Count != 0) return prior;
        foreach (var job in jobs)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO research_queue_jobs(execution_id, start_id, canonical_json, sha256)
                VALUES($id, $start, $json, $hash);
                """, token, ("$id", job.Id.ToString("N")), ("$start", startId),
                ("$json", Canonical(job)), ("$hash", EvidenceCanonicalJson.ComputeSha256(job)));
        }
        transaction.Commit();
        return jobs;
    }

    /// <summary>Returns the first preparation's exact enqueue payloads in stable order for retry after a crash.</summary>
    public async Task<IReadOnlyList<PersistedJob>> ReadQueueJobsAsync(string startId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        return await ReadQueueJobsAsync(connection, null, startId, token);
    }

    /// <summary>Loads only the captured queue payloads, never their market observations.</summary>
    private static Task<IReadOnlyList<PersistedJob>> ReadQueueJobsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string startId, CancellationToken token) => ReadVerifiedRowsAsync<PersistedJob>(connection, transaction,
            "SELECT canonical_json, sha256 FROM research_queue_jobs WHERE start_id=$id ORDER BY execution_id;", token, startId);

    /// <summary>Protects the first durable preparation's files, including before enqueue or after an interrupted call.</summary>
    public async Task<bool> OwnsQueueInputsAsync(PersistedJob job, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        var frozen = await ReadVerifiedRowAsync<PersistedJob>(connection, null, QueueJobById, job.Id.ToString("N"), token);
        return frozen is not null && SameQueueBinding(frozen.Value.Value, job);
    }

    /// <summary>Updates the operational state without changing the immutable request, trials or prepared inputs.</summary>
    public async Task SetStartStateAsync(string startId, ResearchStartState state, string? reason, CancellationToken token)
    {
        if (state is not (ResearchStartState.Admitted or ResearchStartState.Interrupted))
            throw new ArgumentException("Only a reserved research start can be admitted or interrupted.", nameof(state));
        await using var connection = await OpenAsync(token);
        await SetStartStateAsync(connection, null, startId, state, reason, clock.GetUtcNow(), token);
    }

    /// <summary>Keeps admitted queue starts admitted if a losing submitter observes a timeout after enqueue.</summary>
    private static Task SetStartStateAsync(SqliteConnection connection, SqliteTransaction? transaction, string startId,
        ResearchStartState state, string? reason, DateTimeOffset now, CancellationToken token, bool direct = false) =>
        ExecuteAsync(connection, transaction, """
            UPDATE research_starts SET state=$state, reason=$reason, updated_at_utc=$now
            WHERE start_id=$id AND state <> 'Refused'
                AND (state <> 'Admitted' OR $state='Admitted' OR $direct=1);
            """, token, ("$id", startId), ("$state", state.ToString()), ("$reason", reason), ("$now", Utc(now)), ("$direct", direct));

    /// <summary>Checks the worker's durable job against the registered bytes before opening any run inputs.</summary>
    public async Task<DateTimeOffset?> RequireQueueJobAsync(PersistedJob job, string codeSha256, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var start = await FindQueueStartAsync(connection, transaction, job, token)
            ?? throw new InvalidDataException("This queued research job has no durable trial binding. Submit a reviewed start.");
        if (start.Binding.Request.CodeSha256 != codeSha256)
            throw new InvalidDataException("The worker code build differs from the frozen research start. Use the original build.");
        await RequireBoundTrialsAsync(connection, transaction, start.Binding, token);
        await SetStartStateAsync(connection, transaction, start.Binding.Request.StartId, ResearchStartState.Admitted, null, clock.GetUtcNow(), token);
        transaction.Commit();
        return start.Binding.ProtectedObservationStartUtc;
    }

    /// <summary>Checks the invariant enqueue fields, ignoring queue-owned status, heartbeat and result fields.</summary>
    private static bool SameQueueBinding(PersistedJob a, PersistedJob b) =>
        a.Id == b.Id && a.JobType == b.JobType && a.ConfigPath == b.ConfigPath && a.RequestJson == b.RequestJson &&
        a.RunName == b.RunName && a.CreatedAt == b.CreatedAt;

    /// <summary>Resolves the exact owning start and verifies that the job is still an intended member.</summary>
    private static async Task<ResearchStart?> FindQueueStartAsync(SqliteConnection connection, SqliteTransaction? transaction,
        PersistedJob job, CancellationToken token)
    {
        var executionId = job.Id.ToString("N");
        var startId = await ReadScalarAsync(connection, transaction,
            "SELECT start_id FROM research_queue_jobs WHERE execution_id=$id;", token, ("$id", executionId));
        if (startId is null) return null;
        var frozen = await ReadVerifiedRowAsync<PersistedJob>(connection, transaction, QueueJobById, executionId, token);
        var start = await ReadStartAsync(connection, transaction, startId, token);
        if (frozen is null || !SameQueueBinding(frozen.Value.Value, job) || start is null || start.State == ResearchStartState.Refused ||
            !start.Binding.Request.Executions.ContainsKey(executionId))
            throw new InvalidDataException("The queued research job differs from its exact frozen start.");
        return start;
    }

    /// <summary>Verifies each canonical trial belongs to the immutable start and has its original ordinal.</summary>
    private static async Task RequireBoundTrialsAsync(SqliteConnection connection, SqliteTransaction transaction,
        ResearchStartBinding binding, CancellationToken token)
    {
        foreach (var trial in binding.Trials)
        {
            var row = await ReadVerifiedRowAsync<ResearchTrialDefinition>(connection, transaction,
                "SELECT canonical_json, sha256 FROM research_trials WHERE experiment_id=$id;", trial.ExperimentId, token);
            if (row is null || row.Value.Hash != trial.DefinitionSha256 || row.Value.Value.ExperimentId != trial.ExperimentId ||
                row.Value.Value.FamilyTrialCount != trial.FamilyOrdinal)
                throw new InvalidDataException("A research start's exact canonical trial is missing or changed.");
        }
    }

    /// <summary>Records terminal queue evidence only after the durable queue has published its winning result.</summary>
    public async Task RecordQueueResultAsync(PersistedJob job, Func<CancellationToken, Task<ResearchPublicationEvidence?>>? verifyPublication,
        CancellationToken token)
    {
        if (job.Status is not ("completed" or "failed" or "cancelled" or "interrupted") || !File.Exists(databasePath)) return;
        await using var connection = await OpenAsync(token);
        var owningStart = await FindQueueStartAsync(connection, null, job, token);
        if (owningStart is null) return;
        var startId = owningStart.Binding.Request.StartId;
        var artifact = job.ResultReference;
        var at = job.FinishedAt ?? job.CreatedAt;
        var prior = await ReadOutcomeAsync(connection, null, startId, job.Id.ToString("N"), token);
        if (prior is not null && prior.State == job.Status && prior.ArtifactReference == artifact && prior.AtUtc == at &&
            prior.Detail == job.ErrorMessage) return;
        // Never hold the registry writer lock while hashing finished output.
        var artifactHash = artifact is null || job.Status != "completed" ? null : ResearchStartIdentity.HashFile(artifact);
        var publication = verifyPublication is not null && job.Status == "completed" ? await verifyPublication(token) : null;
        await using var transaction = connection.BeginTransaction(deferred: false);
        var start = await FindQueueStartAsync(connection, transaction, job, token);
        if (start is null || start.Binding.Request.StartId != startId)
            throw new InvalidDataException("A terminal research job no longer matches its frozen start.");
        await StoreOutcomeAsync(connection, transaction, start.Binding.Request.StartId,
            new(job.Id.ToString("N"), job.Status, at, artifact, artifactHash, job.ErrorMessage, publication), token);
        await PublishFinishedQueueTrialsAsync(connection, transaction, start, token);
        await SetStartStateAsync(connection, transaction, start.Binding.Request.StartId, ResearchStartState.Admitted, null, clock.GetUtcNow(), token);
        transaction.Commit();
    }

    /// <summary>Publishes one canonical outcome per variant when its complete cohort matrix becomes terminal.</summary>
    private static async Task PublishFinishedQueueTrialsAsync(SqliteConnection connection, SqliteTransaction transaction,
        ResearchStart start, CancellationToken token)
    {
        if (start.Binding.Trials.Count == 0) return;
        var request = start.Binding.Request;
        var outcomes = (await ReadVerifiedRowsAsync<ResearchExecutionResult>(connection, transaction, """
            SELECT canonical_json, sha256 FROM research_execution_results WHERE start_id=$id ORDER BY sequence;
            """, token, request.StartId)).GroupBy(outcome => outcome.ExecutionId).ToDictionary(group => group.Key, group => group.Last());
        foreach (var trial in start.Binding.Trials)
        {
            var executionIds = request.Executions.Where(pair => pair.Value.Contains(trial.VariantKey, StringComparer.Ordinal)).Select(pair => pair.Key).ToArray();
            if (executionIds.Any(id => !outcomes.ContainsKey(id))) continue;
            var terminal = executionIds.Select(id => outcomes[id]).ToArray();
            var completed = terminal.Where(outcome => outcome.State == "completed" && outcome.ArtifactSha256 is not null).ToArray();
            if (completed.Length == 0) continue;
            var variant = request.Variants.Single(item => item.VariantKey == trial.VariantKey);
            var hashes = completed.SelectMany(outcome => new[] { outcome.ArtifactSha256, outcome.Publication?.ManifestSha256 })
                .OfType<string>().ToList();
            var metrics = new Dictionary<string, decimal>
            {
                ["completed_executions"] = completed.Length,
                ["failed_executions"] = terminal.Count(outcome => outcome.State == "failed"),
                ["cancelled_executions"] = terminal.Count(outcome => outcome.State == "cancelled")
            };
            if (request.Kind == ResearchStartKind.Optimization)
            {
                var exact = completed.Single().Publication?.VariantOutcomes?.SingleOrDefault(outcome => outcome.VariantKey == trial.VariantKey);
                if (exact is null || exact.Metric != variant.PrimaryMetric)
                    throw new InvalidDataException("The optimization did not publish the exact frozen variant objective.");
                hashes.AddRange([exact.ResultSha256, exact.ManifestSha256]);
                metrics[variant.PrimaryMetric] = exact.Value;
            }
            var manifest = new CanonicalResearchResultManifest(trial.ExperimentId + "-development", trial.ExperimentId, trial.DefinitionSha256,
                request.StartId, request.CodeSha256, "development", variant.PrimaryMetric, metrics, hashes,
                terminal.Max(outcome => outcome.AtUtc).ToUniversalTime());
            var checkedTrial = await ReadAndValidateTrialForResultAsync(connection, transaction, manifest, token);
            await RegisterCanonicalResultAsync(connection, transaction, manifest, token, checkedTrial.Trial);
        }
    }

    /// <summary>Stores a synchronous execution outcome together with its resulting admission state.</summary>
    public async Task RecordExecutionResultAsync(string startId, ResearchExecutionResult outcome, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var start = await ReadStartAsync(connection, transaction, startId, token);
        if (start is null || start.State == ResearchStartState.Refused || !start.Binding.Request.Executions.ContainsKey(outcome.ExecutionId))
            throw new InvalidDataException("The result does not belong to a frozen research execution.");
        await StoreOutcomeAsync(connection, transaction, startId, outcome, token);
        var state = outcome.State == "completed" ? ResearchStartState.Admitted : ResearchStartState.Interrupted;
        await SetStartStateAsync(connection, transaction, startId, state, outcome.Detail, clock.GetUtcNow(), token, direct: true);
        transaction.Commit();
    }

    /// <summary>Preserves unsuccessful attempts and retries without accepting replacement evidence for a completed execution.</summary>
    private static async Task StoreOutcomeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string startId, ResearchExecutionResult outcome, CancellationToken token)
    {
        var prior = await ReadOutcomeAsync(connection, transaction, startId, outcome.ExecutionId, token);
        if (prior?.State == "completed")
        {
            if (prior.ArtifactReference != outcome.ArtifactReference || prior.ArtifactSha256 != outcome.ArtifactSha256 || prior.State != outcome.State ||
                !SamePublication(prior.Publication, outcome.Publication))
                throw new InvalidDataException("The completed research execution already has different immutable result evidence.");
            return;
        }
        var hash = EvidenceCanonicalJson.ComputeSha256(outcome);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO research_execution_results(start_id, execution_id, canonical_json, sha256)
            VALUES($start, $execution, $json, $hash) ON CONFLICT(start_id, execution_id, sha256) DO NOTHING;
            """, token, ("$start", startId), ("$execution", outcome.ExecutionId), ("$json", Canonical(outcome)), ("$hash", hash));
    }

    /// <summary>Compares immutable publication content even when separate callers materialize their own variant arrays.</summary>
    private static bool SamePublication(ResearchPublicationEvidence? prior, ResearchPublicationEvidence? outcome) =>
        prior is null || outcome is null ? prior is null && outcome is null :
            EvidenceCanonicalJson.ComputeSha256(prior) == EvidenceCanonicalJson.ComputeSha256(outcome);

    /// <summary>Checks previous results before a synchronous retry decides whether any evaluation is needed.</summary>
    public async Task<ResearchExecutionResult?> FindExecutionResultAsync(string startId, string executionId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        return await ReadOutcomeAsync(connection, null, startId, executionId, token);
    }

    /// <summary>Reads the most recent event for one intended execution and verifies its canonical identity.</summary>
    private static async Task<ResearchExecutionResult?> ReadOutcomeAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string startId, string executionId, CancellationToken token)
    {
        var row = await ReadRowAsync(connection, transaction, """
            SELECT canonical_json, sha256 FROM research_execution_results
            WHERE start_id=$start AND execution_id=$id ORDER BY sequence DESC LIMIT 1;
            """, token, ("$start", startId), ("$id", executionId));
        return row is null ? null : VerifyCanonical<ResearchExecutionResult>(row.Value.Json, row.Value.Hash);
    }

    /// <summary>Reads immutable start metadata and its separately maintained operational state.</summary>
    private static async Task<ResearchStart?> ReadStartAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string startId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_json, sha256, state, reason, registered_at_utc, updated_at_utc
            FROM research_starts WHERE start_id=$id;
            """;
        command.Parameters.AddWithValue("$id", startId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var binding = VerifyCanonical<ResearchStartBinding>(reader.GetString(0), reader.GetString(1));
        if (binding.Request.StartId != startId)
            throw new InvalidDataException("The stored start identifier does not match its canonical request.");
        return new(binding, Enum.Parse<ResearchStartState>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Serializes the exact bytes used by the existing canonical research registry.</summary>
    private static string Canonical<T>(T value) => Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(value));

    /// <summary>Rejects corrupted, noncanonical or changed registry bytes before any use.</summary>
    private static T VerifyCanonical<T>(string json, string hash)
    {
        var value = EvidenceCanonicalJson.Deserialize<T>(Encoding.UTF8.GetBytes(json));
        RequireCanonicalMatch(json, hash, Canonical(value), EvidenceCanonicalJson.ComputeSha256(value), typeof(T).Name);
        return value;
    }

    /// <summary>Reads one record and the exact canonical hash checked against its reconstructed bytes.</summary>
    private static async Task<(T Value, string Hash)?> ReadVerifiedRowAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, string id, CancellationToken token)
    {
        var row = await ReadRowAsync(connection, transaction, sql, token, ("$id", id));
        return row is null ? null : (VerifyCanonical<T>(row.Value.Json, row.Value.Hash), row.Value.Hash);
    }

    /// <summary>Reads the known set of verified registry records without opening source or result artifacts.</summary>
    private static async Task<IReadOnlyList<T>> ReadVerifiedRowsAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken token, string? id = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<T>();
        while (await reader.ReadAsync(token))
            rows.Add(VerifyCanonical<T>(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private const string QueueJobById = "SELECT canonical_json, sha256 FROM research_queue_jobs WHERE execution_id=$id;";

    private const string StartsSchemaSql = """
        CREATE TABLE research_starts (
            start_id TEXT NOT NULL PRIMARY KEY,
            canonical_json TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Pending', 'Admitted', 'Refused', 'Interrupted')),
            reason TEXT,
            registered_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE research_queue_jobs (
            execution_id TEXT NOT NULL PRIMARY KEY,
            start_id TEXT NOT NULL REFERENCES research_starts(start_id),
            canonical_json TEXT NOT NULL,
            sha256 TEXT NOT NULL
        );
        CREATE INDEX ix_research_queue_jobs_start ON research_queue_jobs(start_id);

        CREATE TABLE research_execution_results (
            sequence INTEGER NOT NULL PRIMARY KEY,
            start_id TEXT NOT NULL REFERENCES research_starts(start_id),
            execution_id TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            UNIQUE(start_id, execution_id, sha256)
        );
        """;
}
