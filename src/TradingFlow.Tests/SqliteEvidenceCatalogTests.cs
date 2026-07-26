using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;

namespace TradingFlow.Tests;

public sealed class SqliteEvidenceCatalogTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-catalog-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
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
    public async Task CollectionPlans_DistinguishAttemptsAndReplayIdempotently()
    {
        var first = Plan(Now, "run-1");
        var retry = Plan(Now.AddMinutes(1), "run-2");

        var created = await catalog.RegisterCollectionPlanAsync(first);
        var replay = await catalog.RegisterCollectionPlanAsync(first);
        var secondAttempt = await catalog.RegisterCollectionPlanAsync(retry);

        Assert.False(created.AlreadyCommitted);
        Assert.True(replay.AlreadyCommitted);
        Assert.False(secondAttempt.AlreadyCommitted);
        Assert.Equal(first.LogicalPlanHash, retry.LogicalPlanHash);
        Assert.NotEqual(first.JobId, retry.JobId);
        Assert.Equal(first.JobId, (await catalog.GetCollectionPlanAsync(first.JobId))!.JobId);
    }

    [Fact]
    public async Task FindSourceObservations_ReturnsEveryReceiptInPageThenReceiptOrder()
    {
        var plan = Plan(Now, "ordered-source-observations");
        await catalog.RegisterCollectionPlanAsync(plan);
        var retryArtifact = await PutAsync(
            """{"error":"retry"}""",
            "raw/alpaca",
            "application/json");
        var firstPageArtifact = await PutAsync(
            """{"bars":{},"next_page_token":"next"}""",
            "raw/alpaca",
            "application/json");
        var secondPageArtifact = await PutAsync(
            """{"bars":{},"next_page_token":null}""",
            "raw/alpaca",
            "application/json");
        var secondPage = PagedObservation(
            plan,
            secondPageArtifact,
            "observation-page-2",
            2,
            3,
            Now.AddSeconds(3),
            200);
        var firstSuccess = PagedObservation(
            plan,
            firstPageArtifact,
            "observation-page-1-success",
            1,
            2,
            Now.AddSeconds(2),
            200);
        var firstRetry = PagedObservation(
            plan,
            retryArtifact,
            "observation-page-1-retry",
            1,
            1,
            Now.AddSeconds(1),
            429);
        await catalog.RegisterSourceObservationAsync(secondPage);
        await catalog.RegisterSourceObservationAsync(firstSuccess);
        await catalog.RegisterSourceObservationAsync(firstRetry);

        var observations = await catalog.FindSourceObservationsAsync(
            plan.JobId,
            "bars");

        Assert.Equal(
            [
                firstRetry.ObservationId,
                firstSuccess.ObservationId,
                secondPage.ObservationId
            ],
            observations.Select(observation => observation.ObservationId));
    }

    [Fact]
    public void BootstrapConstruction_DoesNotCreateDatabaseOrTemporaryArtifacts()
    {
        var databasePath = Path.Combine(rootPath, "deferred-bootstrap.db");

        _ = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.BootstrapNew),
            store);

        Assert.False(File.Exists(databasePath));
        Assert.Empty(Directory.EnumerateFiles(
            rootPath,
            "deferred-bootstrap.db.bootstrap-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ConcurrentBootstrap_PublishesOneHealthyCatalogWithoutTemporaryArtifacts()
    {
        var databasePath = Path.Combine(rootPath, "concurrent-bootstrap.db");
        var first = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.BootstrapNew),
            store);
        var second = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.BootstrapNew),
            store);
        var firstPlan = Plan(Now, "bootstrap-winner-1");
        var secondPlan = Plan(Now.AddMinutes(1), "bootstrap-winner-2");

        var results = await Task.WhenAll(
            CaptureAsync(firstPlan, () => first.RegisterCollectionPlanAsync(firstPlan)),
            CaptureAsync(secondPlan, () => second.RegisterCollectionPlanAsync(secondPlan)));

        Assert.Single(results, result => result.Error is null);
        Assert.Single(results, result => result.Error is IOException);
        Assert.True(File.Exists(databasePath));
        Assert.Empty(Directory.EnumerateFiles(
            rootPath,
            "concurrent-bootstrap.db.bootstrap-*.tmp*",
            SearchOption.TopDirectoryOnly));

        var reopened = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.OpenExisting),
            store);
        var winner = results.Single(result => result.Error is null).Plan;
        Assert.Equal(winner.JobId, (await reopened.GetCollectionPlanAsync(winner.JobId))!.JobId);
    }

    [Fact]
    public async Task Catalog_RejectsUnknownSchemaVersion()
    {
        var databasePath = Path.Combine(rootPath, "unknown-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE catalog_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                INSERT INTO catalog_metadata(singleton, schema_version) VALUES(1, 999);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var incompatible = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.OpenExisting),
            store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            incompatible.GetCollectionPlanAsync("missing"));

        Assert.Contains("schema 999 is not supported", error.Message);
        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync();
        await using var tableQuery = verification.CreateCommand();
        tableQuery.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await tableQuery.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(["catalog_metadata"], tables);
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public async Task Catalog_RejectsPartialCurrentSchemaWithoutMutation()
    {
        var databasePath = Path.Combine(rootPath, "partial-current-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 CREATE TABLE catalog_metadata (
                     singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                     schema_version INTEGER NOT NULL
                 );
                 INSERT INTO catalog_metadata(singleton, schema_version)
                 VALUES(1, 4);
                 """;
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(databasePath);
        var partial = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.OpenExisting),
            store);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            partial.GetCollectionPlanAsync("missing"));

        Assert.Contains("schema structure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
    }

    [Fact]
    public async Task Catalog_RejectsCorruptDatabaseWithoutChangingItsBytes()
    {
        var databasePath = Path.Combine(rootPath, "corrupt.db");
        var corruptBytes = Encoding.UTF8.GetBytes("not-a-sqlite-database");
        await File.WriteAllBytesAsync(databasePath, corruptBytes);
        var corrupt = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.OpenExisting),
            store);

        await Assert.ThrowsAnyAsync<SqliteException>(() =>
            corrupt.GetCollectionPlanAsync("missing"));

        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public async Task ExistingCatalog_ReopensWithoutBootstrapMutation()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var reopened = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.OpenExisting),
            store);

        Assert.Equal(plan.JobId, (await reopened.GetCollectionPlanAsync(plan.JobId))!.JobId);
    }

    [Fact]
    public async Task LegacyCatalog_IsRejectedWithoutMutation()
    {
        var databasePath = Path.Combine(rootPath, "version-one.db");
        var plan = Plan(Now, "run-v1");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = ON;
                CREATE TABLE catalog_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                CREATE TABLE collection_plans (
                    job_id TEXT NOT NULL PRIMARY KEY,
                    logical_plan_hash TEXT NOT NULL,
                    attempt_hash TEXT NOT NULL UNIQUE,
                    canonical_json TEXT NOT NULL
                );
                CREATE TABLE quarantines (
                    quarantine_id TEXT NOT NULL PRIMARY KEY,
                    status INTEGER NOT NULL,
                    canonical_json TEXT NOT NULL,
                    resolution_json TEXT NULL
                );
                INSERT INTO catalog_metadata(singleton, schema_version) VALUES(1, 1);
                INSERT INTO collection_plans(
                    job_id, logical_plan_hash, attempt_hash, canonical_json)
                VALUES($job, $logical, $attempt, $json);
                """;
            command.Parameters.AddWithValue("$job", plan.JobId);
            command.Parameters.AddWithValue("$logical", plan.LogicalPlanHash);
            command.Parameters.AddWithValue("$attempt", plan.CollectionAttemptHash);
            command.Parameters.AddWithValue(
                "$json",
                Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(plan)));
            await command.ExecuteNonQueryAsync();
        }

        var legacy = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(databasePath, EvidenceCatalogOpenMode.OpenExisting),
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            legacy.GetCollectionPlanAsync(plan.JobId));
        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync();
        await using var query = verification.CreateCommand();
        query.CommandText =
            """
            SELECT
                (SELECT schema_version FROM catalog_metadata WHERE singleton = 1),
                (SELECT COUNT(*) FROM collection_plans),
                (SELECT COUNT(*) FROM sqlite_schema WHERE name = 'artifact_publications');
            """;
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    [Fact]
    public void OpenExisting_RejectsMissingCatalog()
    {
        var missing = Path.Combine(rootPath, "missing.db");

        Assert.Throws<FileNotFoundException>(() =>
            new SqliteEvidenceCatalog(
                new EvidenceCatalogOptions(missing, EvidenceCatalogOpenMode.OpenExisting),
                store));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task ConcurrentCatalogInstances_ConvergeIdempotentRegistrations()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var second = ReopenCatalog();

        var planResults = await Task.WhenAll(
            catalog.RegisterCollectionPlanAsync(plan),
            second.RegisterCollectionPlanAsync(plan));

        Assert.All(planResults, result => Assert.Equal(plan.JobId, result.Id));
        Assert.All(planResults, result => Assert.True(result.AlreadyCommitted));

        var artifact = await PutAsync("audit-report", "audit/reports", "application/json");
        var subject = new EvidenceReferenceSubjectManifest(
            new EvidencePinSubject(EvidencePinSubjectKind.AuditCase, "audit-1"),
            Now,
            [artifact]);
        var subjectResults = await Task.WhenAll(
            catalog.RegisterAsync(subject),
            second.RegisterAsync(subject));

        Assert.Contains(subjectResults, result => !result.AlreadyCommitted);
        Assert.Contains(subjectResults, result => result.AlreadyCommitted);
    }

    [Fact]
    public async Task Checkpoints_AreMonotonicAndPreserveCompletedRequests()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));
        var raw = await PutAsync("checkpoint-response", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "checkpoint-observation");
        await catalog.RegisterSourceObservationAsync(observation);
        await catalog.SaveRequestCursorCheckpointAsync(new(
            plan.JobId,
            "bars",
            1,
            null,
            [],
            observation.ObservationId,
            1,
            exhausted: true,
            Now.AddMilliseconds(500)));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Collecting,
            Now.AddSeconds(1),
            ["bars"]));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Normalizing,
            Now.AddSeconds(2),
            []));

        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId);

        Assert.Equal(EvidenceCollectionState.Normalizing, checkpoint!.State);
        Assert.Equal(["bars"], checkpoint.CompletedRequestIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                Now,
                [])));
    }

    [Fact]
    public async Task CollectionCheckpoint_RejectsUnknownOrIncompleteCompletedRequests()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                Now,
                ["unknown"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Committed,
                Now,
                [])));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Committed,
                Now,
                ["bars"])));
    }

    [Fact]
    public async Task CollectionCheckpoint_RequiresPlannedStartAndExactTerminalReplay()
    {
        var plan = Plan(Now, "run-terminal");
        await catalog.RegisterCollectionPlanAsync(plan);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                Now)));

        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));
        var raw = await PutAsync("terminal-empty-response", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "terminal-observation");
        await catalog.RegisterSourceObservationAsync(observation);
        await catalog.SaveRequestCursorCheckpointAsync(new(
            plan.JobId,
            "bars",
            1,
            null,
            [],
            observation.ObservationId,
            1,
            exhausted: true,
            Now.AddMilliseconds(500)));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Collecting,
            Now.AddSeconds(1),
            ["bars"]));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Normalizing,
            Now.AddSeconds(2),
            ["bars"]));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Validating,
            Now.AddSeconds(3),
            ["bars"]));
        var committed = new EvidenceCollectionCheckpoint(
            plan.JobId,
            EvidenceCollectionState.Committed,
            Now.AddSeconds(4),
            ["bars"]);
        await catalog.SaveCollectionCheckpointAsync(committed);
        await catalog.SaveCollectionCheckpointAsync(committed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Committed,
                Now.AddSeconds(5),
                ["bars"])));
    }

    [Fact]
    public async Task CollectionCheckpoint_CannotCommitWithoutTerminalRequestEvidence()
    {
        var plan = Plan(Now, "run-without-receipt");
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Collecting,
            Now.AddSeconds(1),
            []));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Normalizing,
            Now.AddSeconds(2),
            []));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Validating,
            Now.AddSeconds(3),
            []));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveCollectionCheckpointAsync(new(
                plan.JobId,
                EvidenceCollectionState.Committed,
                Now.AddSeconds(4),
                ["bars"])));

        Assert.Contains("terminal page receipt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalizationRecovery_ResolvesQuarantineAndReusesCompletedRawCollection()
    {
        var plan = Plan(Now, "normalization-recovery");
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));
        var raw = await PutAsync(
            """{"bars":{},"next_page_token":null}""",
            "raw/alpaca",
            "application/json");
        var observation = Observation(plan, raw, "normalization-recovery-observation");
        await catalog.RegisterSourceObservationAsync(observation);
        await catalog.SaveRequestCursorCheckpointAsync(new(
            plan.JobId,
            "bars",
            1,
            null,
            [],
            observation.ObservationId,
            1,
            exhausted: true,
            Now.AddMilliseconds(500)));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Collecting,
            Now.AddSeconds(1),
            ["bars"]));
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Normalizing,
            Now.AddSeconds(2),
            ["bars"]));
        var diagnostic = await PutAsync(
            "normalization failed",
            "quarantine",
            "text/plain");
        var quarantine = new EvidenceQuarantineRecord(
            "normalization-recovery-quarantine",
            EvidenceQuarantineScope.Dataset,
            "normalization_unexpectedsymbol",
            plan.JobId,
            new EvidenceRowLineage([observation.ToReference()]),
            diagnostic,
            retryable: false,
            Now.AddSeconds(3));
        await catalog.RegisterQuarantineAsync(quarantine);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Quarantined,
            Now.AddSeconds(3),
            ["bars"],
            "Unexpected related provider symbol."));

        await catalog.RecoverQuarantinedNormalizationAsync(
            plan.JobId,
            "research-operator",
            "Verified normalizer projection defect was corrected.",
            Now.AddSeconds(4));

        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId);
        var resolved = await catalog.GetQuarantineAsync(quarantine.QuarantineId);
        Assert.Equal(EvidenceCollectionState.Normalizing, checkpoint!.State);
        Assert.Equal(["bars"], checkpoint.CompletedRequestIds);
        Assert.Equal(EvidenceQuarantineStatus.Resolved, resolved!.Status);
        Assert.Single(resolved.Decisions);
        Assert.Equal(
            "Verified normalizer projection defect was corrected.",
            resolved.Decisions[0].Reason);
        Assert.NotNull(await catalog.GetSourceObservationAsync(observation.ObservationId));
    }

    [Fact]
    public async Task NormalizationRecovery_RejectsIncompleteOrUnquarantinedCollections()
    {
        var plan = Plan(Now, "invalid-normalization-recovery");
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(new(
            plan.JobId,
            EvidenceCollectionState.Planned,
            Now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.RecoverQuarantinedNormalizationAsync(
                plan.JobId,
                "research-operator",
                "No quarantine exists.",
                Now.AddSeconds(1)));

        Assert.Contains("quarantined collection", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestCursor_IsExactMonotonicAndBoundToRegisteredObservation()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("raw-page", "raw/alpaca", "application/json");
        var firstObservation = Observation(plan, raw, "observation-page-1");
        await catalog.RegisterSourceObservationAsync(firstObservation);
        var first = new EvidenceRequestCursorCheckpoint(
            plan.JobId,
            "bars",
            1,
            "page-token-2",
            [],
            firstObservation.ObservationId,
            1,
            exhausted: false,
            Now.AddSeconds(1));
        await catalog.SaveRequestCursorCheckpointAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                1,
                "changed-token",
                [],
                firstObservation.ObservationId,
                1,
                exhausted: false,
                Now.AddSeconds(2))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                2,
                null,
                [Hash("page-token-2")],
                "missing-observation",
                2,
                exhausted: true,
                Now.AddSeconds(3))));

        var secondRaw = await PutAsync("raw-page-2", "raw/alpaca", "application/json");
        var secondObservation = Observation(plan, secondRaw, "observation-page-2", "page-2");
        await catalog.RegisterSourceObservationAsync(secondObservation);
        var exhausted = new EvidenceRequestCursorCheckpoint(
            plan.JobId,
            "bars",
            2,
            null,
            [Hash("page-token-2")],
            secondObservation.ObservationId,
            2,
            exhausted: true,
            Now.AddSeconds(4));
        await catalog.SaveRequestCursorCheckpointAsync(exhausted);

        await using (var verification = new SqliteConnection(
                         $"Data Source={Path.Combine(rootPath, "evidence.db")}"))
        {
            await verification.OpenAsync();
            await using var command = verification.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM request_page_ledger
                     WHERE job_id = $job AND request_id = 'bars'),
                    (SELECT terminal_page_ordinal FROM request_completions
                     WHERE job_id = $job AND request_id = 'bars');
                """;
            command.Parameters.AddWithValue("$job", plan.JobId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                3,
                "page-token-4",
                [Hash("page-token-2"), Hash("page-token-3")],
                secondObservation.ObservationId,
                3,
                exhausted: false,
                Now.AddSeconds(5))));
    }

    [Fact]
    public async Task RequestCursor_RejectsSkippedFirstPageAndUnrelatedConsumedToken()
    {
        var plan = Plan(Now, "run-cursor-continuity");
        await catalog.RegisterCollectionPlanAsync(plan);
        var firstRaw = await PutAsync("cursor-first", "raw/alpaca", "application/json");
        var firstObservation = Observation(plan, firstRaw, "cursor-observation-first");
        await catalog.RegisterSourceObservationAsync(firstObservation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                2,
                null,
                [Hash("invented-token")],
                firstObservation.ObservationId,
                1,
                exhausted: true,
                Now.AddSeconds(1))));

        await catalog.SaveRequestCursorCheckpointAsync(new(
            plan.JobId,
            "bars",
            1,
            "provider-token",
            [],
            firstObservation.ObservationId,
            1,
            exhausted: false,
            Now.AddSeconds(2)));
        var secondRaw = await PutAsync("cursor-second", "raw/alpaca", "application/json");
        var secondObservation = Observation(
            plan,
            secondRaw,
            "cursor-observation-second",
            "page-2");
        await catalog.RegisterSourceObservationAsync(secondObservation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                2,
                null,
                [Hash("unrelated-token")],
                secondObservation.ObservationId,
                2,
                exhausted: true,
                Now.AddSeconds(3))));
    }

    [Fact]
    public async Task RequestCursor_ExhaustedReplayMustBeExactIncludingTimestamp()
    {
        var plan = Plan(Now, "run-cursor-terminal");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("cursor-terminal", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "cursor-observation-terminal");
        await catalog.RegisterSourceObservationAsync(observation);
        var exhausted = new EvidenceRequestCursorCheckpoint(
            plan.JobId,
            "bars",
            1,
            null,
            [],
            observation.ObservationId,
            1,
            exhausted: true,
            Now.AddSeconds(1));

        await catalog.SaveRequestCursorCheckpointAsync(exhausted);
        await catalog.SaveRequestCursorCheckpointAsync(exhausted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.SaveRequestCursorCheckpointAsync(new(
                plan.JobId,
                "bars",
                1,
                null,
                [],
                observation.ObservationId,
                1,
                exhausted: true,
                Now.AddSeconds(2))));
    }

    [Fact]
    public async Task Observation_RequiresPlanAndVerifiedRawArtifact()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("raw-response", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "observation-1");

        var created = await catalog.RegisterSourceObservationAsync(observation);
        var replay = await catalog.RegisterSourceObservationAsync(observation);
        var restored = await catalog.GetSourceObservationAsync(observation.ObservationId);

        Assert.False(created.AlreadyCommitted);
        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(observation.Artifact, restored!.Artifact);

        var missing = Artifact("not-stored", "raw/alpaca", "application/json");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RegisterSourceObservationAsync(
                Observation(plan, missing, "observation-missing")));
    }

    [Fact]
    public async Task DatasetCommit_VerifiesLineageAndPinsFullClosure()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("raw-response", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "observation-1");
        await catalog.RegisterSourceObservationAsync(observation);
        var partitionArtifact = await PutAsync(
            "normalized-partition",
            "partitions/bars",
            "application/x-parquet");
        var manifest = Dataset(plan, partitionArtifact, observation.ToReference(), Now.AddMinutes(1));

        var committed = await catalog.CommitDatasetAsync(manifest);
        var replay = await catalog.CommitDatasetAsync(manifest);
        var restored = await catalog.GetDatasetAsync(committed.Id);
        var closure = await catalog.ResolveReferenceClosureAsync(
            new EvidencePinSubject(EvidencePinSubjectKind.Dataset, committed.Id));
        var pin = await catalog.PinAsync(
            new EvidencePinSubject(EvidencePinSubjectKind.Dataset, committed.Id),
            "research evidence");

        Assert.False(committed.AlreadyCommitted);
        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(manifest.DatasetId, restored!.DatasetId);
        Assert.Contains(raw, closure);
        Assert.Contains(partitionArtifact, closure);
        Assert.Equal(closure, pin.ReferencedArtifacts);
        Assert.Contains(closure, artifact =>
            artifact.ObjectNamespace == EvidenceObjectNamespaces.DatasetManifests);
    }

    [Fact]
    public async Task EquivalentDatasetAttempts_ConvergeOnFirstCommittedGeneration()
    {
        var firstPlan = Plan(Now, "run-1");
        var secondPlan = Plan(Now.AddMinutes(1), "run-2");
        await catalog.RegisterCollectionPlanAsync(firstPlan);
        await catalog.RegisterCollectionPlanAsync(secondPlan);
        var partition = await PutAsync("same-normalized-bytes", "partitions/bars", "application/x-parquet");

        var firstRaw = await PutAsync("raw-first", "raw/alpaca", "application/json");
        var secondRaw = await PutAsync("raw-second", "raw/alpaca", "application/json");
        var firstObservation = Observation(firstPlan, firstRaw, "observation-first");
        var secondObservation = Observation(secondPlan, secondRaw, "observation-second");
        await catalog.RegisterSourceObservationAsync(firstObservation);
        await catalog.RegisterSourceObservationAsync(secondObservation);
        var first = Dataset(firstPlan, partition, firstObservation.ToReference(), Now.AddMinutes(2));
        var second = Dataset(secondPlan, partition, secondObservation.ToReference(), Now.AddMinutes(3));

        var firstCommit = await catalog.CommitDatasetAsync(first);
        var secondCommit = await catalog.CommitDatasetAsync(second);

        Assert.Equal(first.LogicalDatasetKey, second.LogicalDatasetKey);
        Assert.NotEqual(first.DatasetId, second.DatasetId);
        Assert.False(firstCommit.AlreadyCommitted);
        Assert.True(secondCommit.AlreadyCommitted);
        Assert.Equal(first.DatasetId, secondCommit.Id);
        await using var verification = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText =
            "SELECT state FROM artifact_publications WHERE owner_id = $owner;";
        command.Parameters.AddWithValue("$owner", second.DatasetId);
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DatasetCommit_RejectsUnregisteredLineage()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var partition = await PutAsync("normalized", "partitions/bars", "application/x-parquet");
        var raw = await PutAsync("raw", "raw/alpaca", "application/json");
        var unregistered = Observation(plan, raw, "never-registered");
        var manifest = Dataset(plan, partition, unregistered.ToReference(), Now.AddMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CommitDatasetAsync(manifest));

        await using var verification = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await verification.OpenAsync();
        await using var command = verification.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM artifact_publications WHERE owner_id = $owner;";
        command.Parameters.AddWithValue("$owner", manifest.DatasetId);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DatasetCommit_RejectsTamperedRawLineage()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("raw", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "observation-1");
        await catalog.RegisterSourceObservationAsync(observation);
        var partition = await PutAsync("normalized", "partitions/bars", "application/x-parquet");
        var manifest = Dataset(plan, partition, observation.ToReference(), Now.AddMinutes(1));
        var contentPath = Path.Combine(
            rootPath,
            "objects",
            raw.ObjectNamespace.Value.Replace('/', Path.DirectorySeparatorChar),
            raw.Content.Sha256[..2],
            raw.Content.Sha256,
            "content.bin");
        await File.WriteAllTextAsync(contentPath, "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CommitDatasetAsync(manifest));
    }

    [Fact]
    public async Task DatasetCommit_RejectsObservationFromDifferentCollectionAttempt()
    {
        var manifestPlan = Plan(Now, "run-1");
        var foreignPlan = Plan(Now.AddMinutes(1), "run-2");
        await catalog.RegisterCollectionPlanAsync(manifestPlan);
        await catalog.RegisterCollectionPlanAsync(foreignPlan);
        var raw = await PutAsync("foreign-raw", "raw/alpaca", "application/json");
        var foreignObservation = Observation(foreignPlan, raw, "foreign-observation");
        await catalog.RegisterSourceObservationAsync(foreignObservation);
        var partition = await PutAsync("normalized", "partitions/bars", "application/x-parquet");
        var manifest = Dataset(
            manifestPlan,
            partition,
            foreignObservation.ToReference(),
            Now.AddMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CommitDatasetAsync(manifest));
    }

    [Fact]
    public async Task DatasetRead_RejectsCatalogJsonThatDoesNotMatchImmutableManifest()
    {
        var registered = await CreateRegisteredHoldoutAsync();
        var datasetId = registered.Holdout.Datasets.Single().DatasetId;
        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(rootPath, "evidence.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE datasets SET canonical_json = '{}' WHERE dataset_id = $id;";
            command.Parameters.AddWithValue("$id", datasetId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.GetDatasetAsync(datasetId));
    }

    [Fact]
    public async Task ResearchRunRegistration_RejectsIncompleteHoldoutDatasetReferenceAtomically()
    {
        var registered = await CreateRegisteredHoldoutAsync();
        var dataset = registered.Holdout.Datasets.Single();
        var wrongReference = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                dataset.ManifestSha256,
                dataset.ManifestArtifact.Content.ByteLength + 1,
                dataset.ManifestArtifact.Content.MediaType),
            dataset.ManifestArtifact.ObjectNamespace);
        var invalid = new EvidenceHoldoutIdentity(
            registered.Holdout.StudyFamily,
            registered.Holdout.StartUtc,
            registered.Holdout.EndUtc,
            [new EvidenceDatasetReference(dataset.DatasetId, wrongReference)],
            registered.Holdout.UniverseLedgerId,
            registered.Holdout.UniverseLedgerArtifact,
            registered.Holdout.PartitionDefinitionArtifact);

        var manifest = await ReadyResearchRunAsync(invalid, "research-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.RegisterResearchRunAsync(manifest));

        Assert.Null(await catalog.GetResearchRunAsync(manifest.ResearchRunId));
        Assert.Null(await catalog.GetHoldoutConsumptionAsync(invalid.HoldoutId));
    }

    [Fact]
    public async Task ResearchRunRegistration_PermanentlyAssignsHoldoutToFirstConcurrentRun()
    {
        var holdout = (await CreateRegisteredHoldoutAsync()).Holdout;
        var first = await ReadyResearchRunAsync(holdout, "research-a");
        var second = await ReadyResearchRunAsync(holdout, "research-b");

        var attempts = await Task.WhenAll(
            CaptureAsync(() => catalog.RegisterResearchRunAsync(first)),
            CaptureAsync(() => catalog.RegisterResearchRunAsync(second)));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is InvalidOperationException);
        var consumption = Assert.IsType<EvidenceHoldoutConsumption>(
            await catalog.GetHoldoutConsumptionAsync(holdout.HoldoutId));
        Assert.True(
            consumption.ResearchRunId == first.ResearchRunId ||
            consumption.ResearchRunId == second.ResearchRunId);
        Assert.NotNull(await catalog.GetResearchRunAsync(consumption.ResearchRunId));
        var losingRunId = consumption.ResearchRunId == first.ResearchRunId
            ? second.ResearchRunId
            : first.ResearchRunId;
        Assert.Null(await catalog.GetResearchRunAsync(losingRunId));
    }

    [Fact]
    public async Task ResearchRunRegistration_RejectsUnregisteredHoldoutEvidenceAtomically()
    {
        var holdout = Holdout();
        var manifest = await ReadyResearchRunAsync(holdout, "research-a");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RegisterResearchRunAsync(manifest));

        Assert.Null(await catalog.GetResearchRunAsync(manifest.ResearchRunId));
        Assert.Null(await catalog.GetHoldoutConsumptionAsync(holdout.HoldoutId));
    }

    [Fact]
    public async Task EvidenceReadyResearchRun_AtomicallyConsumesHoldoutAndPinsPartitionDefinition()
    {
        var registered = await CreateRegisteredHoldoutAsync();
        var manifest = await ReadyResearchRunAsync(registered.Holdout, "research-a");

        await catalog.RegisterResearchRunAsync(manifest);
        var replay = await catalog.RegisterResearchRunAsync(manifest);
        Assert.True(replay.AlreadyCommitted);
        var consumption = Assert.IsType<EvidenceHoldoutConsumption>(
            await catalog.GetHoldoutConsumptionAsync(registered.Holdout.HoldoutId));
        Assert.Equal(manifest.ResearchRunId, consumption.ResearchRunId);
        Assert.Equal(manifest.CreatedAtUtc, consumption.ConsumedAtUtc);
        var closure = await catalog.ResolveReferenceClosureAsync(
            new EvidencePinSubject(EvidencePinSubjectKind.ResearchRun, manifest.ResearchRunId));

        Assert.Contains(registered.Holdout.PartitionDefinitionArtifact, closure);
    }

    [Fact]
    public async Task Quarantine_IsImmutableAndDecisionReplayIsIdempotent()
    {
        var plan = Plan(Now, "run-1");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync("raw", "raw/alpaca", "application/json");
        var observation = Observation(plan, raw, "observation-1");
        await catalog.RegisterSourceObservationAsync(observation);
        var diagnostic = await PutAsync("diagnostic", "quarantine", "application/json");
        var quarantine = new EvidenceQuarantineRecord(
            "quarantine-1",
            EvidenceQuarantineScope.Row,
            "invalid_ohlc",
            "AAPL:2026-07-25",
            new EvidenceRowLineage([observation.ToReference()]),
            diagnostic,
            retryable: true,
            Now);

        var second = ReopenCatalog();
        var registrations = await Task.WhenAll(
            catalog.RegisterQuarantineAsync(quarantine),
            second.RegisterQuarantineAsync(quarantine));
        Assert.Contains(registrations, result => !result.AlreadyCommitted);
        Assert.Contains(registrations, result => result.AlreadyCommitted);
        Assert.Single(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open));
        var retryDecision = new EvidenceQuarantineResolution(
            quarantine.QuarantineId,
            EvidenceQuarantineStatus.RetryScheduled,
            "reviewer",
            "Retry after provider backfill.",
            Now.AddMinutes(1));
        await Task.WhenAll(
            catalog.ResolveQuarantineAsync(retryDecision),
            second.ResolveQuarantineAsync(retryDecision));
        Assert.Single(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.RetryScheduled));
        var openDecision = new EvidenceQuarantineResolution(
            quarantine.QuarantineId,
            EvidenceQuarantineStatus.Open,
            "collector",
            "Retry started.",
            Now.AddMinutes(2));
        await catalog.ResolveQuarantineAsync(openDecision);
        Assert.Single(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ResolveQuarantineAsync(retryDecision));
        var terminalDecision = new EvidenceQuarantineResolution(
            quarantine.QuarantineId,
            EvidenceQuarantineStatus.Terminal,
            "reviewer",
            "Provider row is irreconcilable.",
            Now.AddMinutes(3));
        await catalog.ResolveQuarantineAsync(terminalDecision);
        await catalog.ResolveQuarantineAsync(terminalDecision);

        Assert.Empty(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open));
        Assert.Single(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Terminal));
        var entry = await catalog.GetQuarantineAsync(quarantine.QuarantineId);
        Assert.Equal(EvidenceQuarantineStatus.Terminal, entry!.Status);
        Assert.Equal(3, entry.Decisions.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ResolveQuarantineAsync(new(
                quarantine.QuarantineId,
                EvidenceQuarantineStatus.Resolved,
                "reviewer",
                "Second decision.",
                Now.AddMinutes(4))));
    }

    private async Task<EvidenceArtifactReference> PutAsync(
        string value,
        string objectNamespace,
        string mediaType)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var artifact = Artifact(value, objectNamespace, mediaType);
        await store.PutIfAbsentAsync(new(artifact), bytes);
        return artifact;
    }

    private static EvidenceCollectionPlan Plan(DateTimeOffset createdAtUtc, string runId) =>
        new(
            createdAtUtc,
            runId,
            Hash("config"),
            "commit-1",
            "collection-v1",
            "daily-v1",
            [
                new EvidenceCollectionRequest(
                    "bars",
                    "alpaca",
                    "/v2/stocks/bars",
                    ["AAPL"],
                    Now.AddDays(-1),
                    Now,
                    "sip",
                    "all",
                    "USD",
                    new DateOnly(2026, 7, 25))
            ]);

    private static EvidenceSourceObservation Observation(
        EvidenceCollectionPlan plan,
        EvidenceArtifactReference artifact,
        string observationId,
        string pageIdentity = "page-1") =>
        new(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            "bars",
            pageIdentity,
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            200,
            ["AAPL"],
            Now.AddDays(-1),
            Now,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            Now,
            artifact,
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion,
            new Dictionary<string, string> { ["content-type"] = "application/json" });

    private static EvidenceSourceObservation PagedObservation(
        EvidenceCollectionPlan plan,
        EvidenceArtifactReference artifact,
        string observationId,
        int pageOrdinal,
        int attempt,
        DateTimeOffset receivedAtUtc,
        int statusCode) =>
        new(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            "bars",
            $"page-{pageOrdinal}",
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            statusCode,
            ["AAPL"],
            Now.AddDays(-1),
            Now,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            receivedAtUtc,
            artifact,
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion,
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            new Dictionary<string, string>
            {
                ["page_ordinal"] = pageOrdinal.ToString(),
                ["attempt"] = attempt.ToString(),
                ["receipt_id"] = observationId
            });

    private static EvidenceDatasetManifest Dataset(
        EvidenceCollectionPlan plan,
        EvidenceArtifactReference partitionArtifact,
        EvidenceSourceReference source,
        DateTimeOffset createdAtUtc) =>
        new(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            createdAtUtc,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.ConfigHash,
            plan.CodeVersion,
            "normalizer-v1",
            "sip",
            [
                new EvidenceDatasetPartitionManifest(
                    "aapl-2026-07",
                    EvidenceDatasetKind.MarketBarsResearchAdjusted,
                    1,
                    new EvidencePartitionProvenance(
                        "alpaca",
                        "/v2/stocks/bars",
                        "sip",
                        "all",
                        "USD",
                        new DateOnly(2026, 7, 25),
                        ["security-aapl"],
                        ["issuer-apple"],
                        ["AAPL"],
                        "1d",
                        Now.AddDays(-1),
                        Now),
                    Now.AddDays(-1),
                    Now,
                    1,
                    partitionArtifact,
                    [source],
                    "normalizer-v1",
                    plan.CodeVersion,
                    new EvidenceQualityReport())
            ],
            new EvidenceQualityReport());

    private static EvidenceHoldoutIdentity Holdout()
    {
        var manifest = Artifact("dataset-manifest", "manifests/datasets", "application/json");
        return new EvidenceHoldoutIdentity(
            "momentum",
            Now,
            Now.AddDays(30),
            [new EvidenceDatasetReference("dataset-1", manifest)],
            "ledger-1",
            Artifact("ledger", "ledgers", "application/json"),
            Artifact("partition", "partitions", "application/json"));
    }

    private async Task<RegisteredHoldout> CreateRegisteredHoldoutAsync()
    {
        var plan = Plan(Now, $"run-{Guid.NewGuid():N}");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await PutAsync(
            $"raw-{plan.JobId}",
            "raw/alpaca",
            "application/json");
        var observation = Observation(plan, raw, $"observation-{plan.JobId}");
        await catalog.RegisterSourceObservationAsync(observation);
        var partition = await PutAsync(
            $"partition-{plan.JobId}",
            "partitions/bars",
            "application/x-parquet");
        var dataset = Dataset(plan, partition, observation.ToReference(), Now.AddMinutes(1));
        await catalog.CommitDatasetAsync(dataset);
        var datasetBytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(dataset);
        var datasetManifest = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant(),
                datasetBytes.Length,
                "application/vnd.tradingflow.evidence-dataset+json"),
            EvidenceObjectNamespaces.DatasetManifests);
        var ledger = await PutAsync(
            $"ledger-{plan.JobId}",
            "research/universe-ledgers",
            "application/json");
        var partitionDefinition = await PutAsync(
            $"partition-definition-{plan.JobId}",
            "research/partition-definitions",
            "application/json");
        var holdout = new EvidenceHoldoutIdentity(
            "momentum",
            Now,
            Now.AddDays(30),
            [new EvidenceDatasetReference(dataset.DatasetId, datasetManifest)],
            $"ledger-{plan.JobId}",
            ledger,
            partitionDefinition);
        return new RegisteredHoldout(holdout);
    }

    private async Task<EvidenceResearchRunManifest> ReadyResearchRunAsync(
        EvidenceHoldoutIdentity holdout,
        string researchRunId)
    {
        var studyConfig = await PutAsync(
            $"study-config-{researchRunId}",
            "research/configs",
            "application/json");
        var assumptions = new EvidenceSimulationAssumptions(
            await PutAsync($"cost-{researchRunId}", "research/assumptions", "application/json"),
            await PutAsync($"spread-{researchRunId}", "research/assumptions", "application/json"),
            await PutAsync($"slippage-{researchRunId}", "research/assumptions", "application/json"),
            await PutAsync($"borrow-{researchRunId}", "research/assumptions", "application/json"),
            await PutAsync($"benchmark-{researchRunId}", "research/assumptions", "application/json"));
        var report = await PutAsync(
            $"report-{researchRunId}",
            "research/outputs",
            "application/json");
        return new EvidenceResearchRunManifest(
            researchRunId,
            holdout.StudyFamily,
            Now,
            holdout.Datasets,
            holdout.UniverseLedgerId,
            holdout.UniverseLedgerArtifact,
            studyConfig,
            "commit-1",
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow(
                    "development",
                    holdout.StartUtc.AddDays(-60),
                    holdout.StartUtc.AddDays(-30)),
                new EvidenceStudyWindow(
                    "validation",
                    holdout.StartUtc.AddDays(-30),
                    holdout.StartUtc),
                new EvidenceStudyWindow("holdout", holdout.StartUtc, holdout.EndUtc)),
            assumptions,
            holdout,
            evidenceReady: true,
            readinessFailures: null,
            outputs: [report]);
    }

    private SqliteEvidenceCatalog ReopenCatalog() =>
        new(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.OpenExisting),
            store);

    private static EvidenceArtifactReference Artifact(
        string value,
        string objectNamespace,
        string mediaType)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new EvidenceArtifactReference(
            new EvidenceContentAddress(Hash(value), bytes.Length, mediaType),
            new EvidenceObjectNamespace(objectNamespace));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<BootstrapAttempt> CaptureAsync(
        EvidenceCollectionPlan plan,
        Func<Task<EvidenceCatalogCommitResult>> operation)
    {
        try
        {
            await operation();
            return new BootstrapAttempt(plan, null);
        }
        catch (Exception error)
        {
            return new BootstrapAttempt(plan, error);
        }
    }

    private static async Task<RegistrationAttempt> CaptureAsync(
        Func<Task<EvidenceCatalogCommitResult>> operation)
    {
        try
        {
            return new RegistrationAttempt(await operation(), null);
        }
        catch (Exception error)
        {
            return new RegistrationAttempt(null, error);
        }
    }

    private sealed record BootstrapAttempt(EvidenceCollectionPlan Plan, Exception? Error);

    private sealed record RegistrationAttempt(
        EvidenceCatalogCommitResult? Result,
        Exception? Error);

    private sealed record RegisteredHoldout(EvidenceHoldoutIdentity Holdout);
}
