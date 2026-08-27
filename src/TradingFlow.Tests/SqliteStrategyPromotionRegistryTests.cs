using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class SqliteStrategyPromotionRegistryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-promotion-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private SqliteStrategyPromotionRegistry registry = null!;
    private EvidenceResearchRunManifest run = null!;
    private StrategyPromotionEvidence promotionEvidence = null!;

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
        run = await CreateReadyResearchRunAsync();
        await catalog.RegisterResearchRunAsync(run);
        promotionEvidence = new StrategyPromotionEvidence(
            run.Outputs[0],
            run.Outputs[1],
            run.Outputs[2],
            run.Assumptions.CostModel,
            run.Outputs[3]);
        registry = NewRegistry(StrategyPromotionRegistryOpenMode.BootstrapNew);
        await registry.GrantPaperExperimentAsync(
            new StrategyArtifactIdentity(run.StudyId, "1.0.0", run.StudyConfigArtifact.Content.Sha256),
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(-1),
            "Register exact paper experiment before shadow governance.");
        Assert.Empty(await registry.GetDecisionHistoryAsync());
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
    public async Task RegisterDecision_RequiresIndependentAuthorizedHumanPrincipal()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.RegisterDecisionAsync(decision, "intruder@example.test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.RegisterDecisionAsync(decision, "reviewer@example.test"));

        Assert.Empty(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task ActiveCatalog_IsAcceptedMinusRejectedRevokedAndSuperseded()
    {
        var accepted = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        var rejected = Decision("rejected", StrategyPromotionDecisionStatus.Rejected);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        await registry.RegisterDecisionAsync(rejected, rejected.ApprovedBy);
        Assert.Equal(
            [accepted.DecisionId],
            (await registry.GetActiveDecisionsAsync()).Select(item => item.DecisionId));

        var revoked = Decision(
            "revoked",
            StrategyPromotionDecisionStatus.Revoked,
            accepted.DecisionId);
        await registry.RegisterDecisionAsync(revoked, revoked.ApprovedBy);

        Assert.Empty(await registry.GetActiveDecisionsAsync());
        Assert.Equal(
            [accepted.DecisionId, rejected.DecisionId, revoked.DecisionId],
            (await registry.GetDecisionHistoryAsync())
                .Select(item => item.DecisionId)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RegisterDecision_IsIdempotentAndConcurrent()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);

        var results = await Task.WhenAll(
            registry.RegisterDecisionAsync(decision, decision.ApprovedBy),
            NewRegistry(StrategyPromotionRegistryOpenMode.OpenExisting)
                .RegisterDecisionAsync(decision, decision.ApprovedBy));

        Assert.Single(results, result => !result.AlreadyCommitted);
        Assert.Single(results, result => result.AlreadyCommitted);
        Assert.Single(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task RegisterDecision_RejectsSecondNonTerminalGrantForExactIdentity()
    {
        var first = Decision("accepted-first", StrategyPromotionDecisionStatus.Accepted);
        var duplicate = Decision("accepted-duplicate", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(first, first.ApprovedBy);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterDecisionAsync(duplicate, duplicate.ApprovedBy));

        Assert.Contains("non-terminal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([first.DecisionId],
            (await registry.GetDecisionHistoryAsync()).Select(decision => decision.DecisionId));

        var revoke = Decision(
            "revoke-first",
            StrategyPromotionDecisionStatus.Revoked,
            first.DecisionId);
        await registry.RegisterDecisionAsync(revoke, revoke.ApprovedBy);
        await registry.RegisterDecisionAsync(duplicate, duplicate.ApprovedBy);

        Assert.Equal([duplicate.DecisionId],
            (await registry.GetActiveDecisionsAsync()).Select(decision => decision.DecisionId));
    }

    [Fact]
    public async Task ConcurrentTerminalDecisions_AllowOnlyOnePermanentOutcome()
    {
        var accepted = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        var revoked = Decision(
            "revoked",
            StrategyPromotionDecisionStatus.Revoked,
            accepted.DecisionId);
        var superseded = Decision(
            "superseded",
            StrategyPromotionDecisionStatus.Superseded,
            accepted.DecisionId,
            semanticVersion: "2.0.0");

        var attempts = await Task.WhenAll(
            CaptureAsync(() => registry.RegisterDecisionAsync(revoked, revoked.ApprovedBy)),
            CaptureAsync(() => NewRegistry(StrategyPromotionRegistryOpenMode.OpenExisting)
                .RegisterDecisionAsync(superseded, superseded.ApprovedBy)));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Error is InvalidOperationException);
        Assert.Empty(await registry.GetActiveDecisionsAsync());
        Assert.Equal(2, (await registry.GetDecisionHistoryAsync()).Count);
    }

    [Fact]
    public async Task Supersession_AtomicallyReplacesSameFamilyAndGrantWithNewVersion()
    {
        var accepted = Decision("accepted-v1", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        var superseded = Decision(
            "supersede-v1",
            StrategyPromotionDecisionStatus.Superseded,
            accepted.DecisionId);
        var replacement = Decision(
            "accepted-v2",
            StrategyPromotionDecisionStatus.Accepted,
            semanticVersion: "2.0.0");
        await registry.GrantPaperExperimentAsync(
            replacement.ArtifactIdentity,
            "paper-experiment-grant-v2",
            replacement.ApprovedBy,
            Now.AddMinutes(-1),
            "Register replacement experiment before atomic shadow supersession.");

        var result = await registry.RegisterSupersessionAsync(
            superseded,
            replacement,
            accepted.ApprovedBy);

        Assert.False(result.Supersession.AlreadyCommitted);
        Assert.False(result.Replacement.AlreadyCommitted);
        var active = Assert.Single(await registry.GetActiveDecisionsAsync());
        Assert.Equal(replacement.DecisionId, active.DecisionId);
        Assert.Equal("2.0.0", active.SemanticVersion);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("config")]
    [InlineData("holdout")]
    [InlineData("universe")]
    public async Task RegisterDecision_RejectsForgedResearchIdentity(string forgery)
    {
        var decision = forgery switch
        {
            "manifest" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                manifestHash: Hash("forged-manifest")),
            "config" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                configHash: Hash("forged-config")),
            "holdout" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                holdoutId: Hash("forged-holdout")),
            "universe" => Decision(
                "forged",
                StrategyPromotionDecisionStatus.Accepted,
                universeLedgerId: "forged-ledger"),
            _ => throw new InvalidOperationException()
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            registry.RegisterDecisionAsync(decision, decision.ApprovedBy));
        Assert.Empty(await registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task DecisionHistory_FailsClosedWhenRegistryJsonIsModified()
    {
        var decision = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(decision, decision.ApprovedBy);
        await using (var connection = new SqliteConnection(
                         $"Data Source={Path.Combine(rootPath, "promotions.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE promotion_decisions SET canonical_json = '{}' WHERE decision_id = $id;";
            command.Parameters.AddWithValue("$id", decision.DecisionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            registry.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task Suspension_BlocksNewEligibilityUntilExplicitResume()
    {
        var accepted = Decision("accepted", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(accepted, accepted.ApprovedBy);
        var validated = Decision(
            "validated",
            StrategyPromotionDecisionStatus.Accepted,
            authorizedAuthorization: StrategyExecutionAuthorization.Validated);
        await registry.RegisterDecisionAsync(validated, validated.ApprovedBy);
        var suspended = Decision(
            "suspended",
            StrategyPromotionDecisionStatus.Suspended,
            accepted.DecisionId);
        await registry.RegisterDecisionAsync(suspended, suspended.ApprovedBy);

        Assert.Empty(await registry.GetActiveDecisionsAsync());

        var resumed = Decision(
            "resumed",
            StrategyPromotionDecisionStatus.Resumed,
            suspended.DecisionId);
        await registry.RegisterDecisionAsync(resumed, resumed.ApprovedBy);

        var active = await registry.GetActiveDecisionsAsync();
        Assert.Equal(
            [accepted.DecisionId, validated.DecisionId],
            active.Select(item => item.DecisionId).OrderBy(item => item, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PaperShadowAuthorization_RequiresRegisteredExperimentIdentity()
    {
        var isolated = new SqliteStrategyPromotionRegistry(
            new StrategyPromotionRegistryOptions(
                Path.Combine(rootPath, "no-experiment-promotions.db"),
                StrategyPromotionRegistryOpenMode.BootstrapNew),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            NoRegisteredExperiments.Instance);
        var accepted = Decision("unregistered-experiment", StrategyPromotionDecisionStatus.Accepted);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            isolated.RegisterDecisionAsync(accepted, accepted.ApprovedBy));

        Assert.Contains("paper-experiment grant", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await isolated.GetDecisionHistoryAsync());
    }

    [Fact]
    public async Task ValidatedAuthorization_DependsOnActivePaperShadowGrant()
    {
        var validated = Decision(
            "validated-first",
            StrategyPromotionDecisionStatus.Accepted,
            authorizedAuthorization: StrategyExecutionAuthorization.Validated);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterDecisionAsync(validated, validated.ApprovedBy));

        var shadow = Decision("shadow", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(shadow, shadow.ApprovedBy);
        validated = Decision(
            "validated-after-shadow",
            StrategyPromotionDecisionStatus.Accepted,
            authorizedAuthorization: StrategyExecutionAuthorization.Validated);
        await registry.RegisterDecisionAsync(validated, validated.ApprovedBy);
        Assert.Equal(2, (await registry.GetActiveDecisionsAsync()).Count);

        var revokeShadow = Decision(
            "revoke-shadow",
            StrategyPromotionDecisionStatus.Revoked,
            shadow.DecisionId);
        await registry.RegisterDecisionAsync(revokeShadow, revokeShadow.ApprovedBy);

        Assert.Empty(await registry.GetActiveDecisionsAsync());
        Assert.Contains(
            (await registry.GetDecisionHistoryAsync()).Select(item => item.DecisionId),
            item => item == validated.DecisionId);
    }

    [Fact]
    public async Task ValidatedAuthorization_CannotBeCreatedAfterExperimentRevocation()
    {
        var identity = new StrategyArtifactIdentity(
            run.StudyId,
            "1.0.0",
            run.StudyConfigArtifact.Content.Sha256);
        var shadow = Decision("shadow-before-experiment-revocation", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(shadow, shadow.ApprovedBy);
        await registry.RevokePaperExperimentAsync(
            identity,
            "revoke-before-validation",
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(1),
            "Remove the exact experiment prerequisite before validation.");

        var validated = Decision(
            "validated-after-experiment-revocation",
            StrategyPromotionDecisionStatus.Accepted,
            authorizedAuthorization: StrategyExecutionAuthorization.Validated);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterDecisionAsync(validated, validated.ApprovedBy));

        Assert.Contains("paper-experiment", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            await registry.GetDecisionHistoryAsync(),
            decision => decision.DecisionId == validated.DecisionId);
    }

    [Fact]
    public async Task RevokedExperiment_InvalidatesDependentShadowAndLiveNewEntriesOnly()
    {
        var identity = new StrategyArtifactIdentity(
            run.StudyId,
            "1.0.0",
            run.StudyConfigArtifact.Content.Sha256);
        var shadow = Decision("dependent-shadow", StrategyPromotionDecisionStatus.Accepted);
        await registry.RegisterDecisionAsync(shadow, shadow.ApprovedBy);
        var validated = Decision(
            "dependent-validated",
            StrategyPromotionDecisionStatus.Accepted,
            authorizedAuthorization: StrategyExecutionAuthorization.Validated);
        await registry.RegisterDecisionAsync(validated, validated.ApprovedBy);
        var shadowIntent = Guid.NewGuid();
        var liveIntent = Guid.NewGuid();
        var shadowAdmission = await registry.AdmitNewEntryAsync(
            identity,
            StrategySelectionMode.RunPaperShadow,
            shadowIntent,
            Now.AddMinutes(1));
        var liveAdmission = await registry.AdmitNewEntryAsync(
            identity,
            StrategySelectionMode.RunLive,
            liveIntent,
            Now.AddMinutes(1));

        await registry.RevokePaperExperimentAsync(
            identity,
            "revoke-experiment-prerequisite",
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(2),
            "Remove the prerequisite experiment authorization.");

        Assert.Empty(await registry.GetActiveGrantsAsync());
        Assert.Empty(await registry.GetActiveDecisionsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunPaperShadow,
                Guid.NewGuid(),
                Now.AddMinutes(3)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunLive,
                Guid.NewGuid(),
                Now.AddMinutes(3)));
        Assert.Equal(
            shadowAdmission,
            await registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunPaperShadow,
                shadowIntent,
                Now.AddMinutes(3)));
        Assert.Equal(
            liveAdmission,
            await registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunLive,
                liveIntent,
                Now.AddMinutes(3)));
    }

    [Fact]
    public async Task PaperExperimentGrant_RetryRequiresOriginalDecisionIdentity()
    {
        var identity = new StrategyArtifactIdentity(
            run.StudyId,
            "1.0.0",
            run.StudyConfigArtifact.Content.Sha256);

        await registry.GrantPaperExperimentAsync(
            identity,
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(-1),
            "Register exact paper experiment before shadow governance.");

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.GrantPaperExperimentAsync(
                identity,
                "different-paper-experiment-grant",
                "governor@example.test",
                Now,
                "Register exact paper experiment before shadow governance."));
        Assert.Contains("reuse its original decision ID", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaperExperimentAuthorization_RevocationBlocksNewEntriesButPreservesPriorAdmission()
    {
        var identity = new StrategyArtifactIdentity(
            run.StudyId,
            "1.0.0",
            run.StudyConfigArtifact.Content.Sha256);
        var admittedIntent = Guid.NewGuid();
        var admitted = await registry.AdmitNewEntryAsync(
            identity,
            StrategySelectionMode.RunPaperExperiment,
            admittedIntent,
            Now);

        await registry.SuspendPaperExperimentAsync(
            identity,
            "paper-experiment-suspend",
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(1),
            "Pause new experiment entries while preserving admitted work.");

        Assert.DoesNotContain(
            await registry.GetActiveGrantsAsync(),
            grant => grant.Authorization == StrategyExecutionAuthorization.PaperExperiment);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunPaperExperiment,
                Guid.NewGuid(),
                Now.AddMinutes(2)));
        Assert.Equal(
            admitted,
            await registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunPaperExperiment,
                admittedIntent,
                Now.AddMinutes(2)));

        await registry.ResumePaperExperimentAsync(
            identity,
            "paper-experiment-resume",
            "paper-experiment-suspend",
            "governor@example.test",
            Now.AddMinutes(3),
            "Resume exact experiment identity.");
        Assert.Contains(
            await registry.GetActiveGrantsAsync(),
            grant => grant.Identity == identity &&
                     grant.Authorization == StrategyExecutionAuthorization.PaperExperiment);

        await registry.RevokePaperExperimentAsync(
            identity,
            "paper-experiment-revoke",
            "paper-experiment-grant",
            "governor@example.test",
            Now.AddMinutes(4),
            "Withdraw exact experiment identity.");
        Assert.DoesNotContain(
            await registry.GetActiveGrantsAsync(),
            grant => grant.Identity == identity);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.AdmitNewEntryAsync(
                identity,
                StrategySelectionMode.RunPaperExperiment,
                Guid.NewGuid(),
                Now.AddMinutes(5)));
    }

    [Fact]
    public async Task VersionOneMigrationFailure_RollsBackAndCanBeRetried()
    {
        var path = Path.Combine(rootPath, "faulted-migration.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE promotion_registry_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                INSERT INTO promotion_registry_metadata(singleton, schema_version) VALUES(1, 1);
                CREATE TABLE promotion_decisions (
                    decision_id TEXT NOT NULL PRIMARY KEY,
                    status INTEGER NOT NULL,
                    strategy_id TEXT NOT NULL,
                    target_decision_id TEXT NULL,
                    decided_at_utc TEXT NOT NULL,
                    artifact_namespace TEXT NOT NULL,
                    artifact_sha256 TEXT NOT NULL,
                    artifact_byte_length INTEGER NOT NULL,
                    canonical_json TEXT NOT NULL
                );
                CREATE TABLE strategy_authorization_decisions(blocker INTEGER);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var faulted = new SqliteStrategyPromotionRegistry(
            new StrategyPromotionRegistryOptions(path, StrategyPromotionRegistryOpenMode.OpenExisting),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            RegisteredExperiments.Instance);
        await Assert.ThrowsAnyAsync<SqliteException>(() => faulted.GetActiveGrantsAsync());

        await using (var verification = new SqliteConnection($"Data Source={path}"))
        {
            await verification.OpenAsync();
            await using var command = verification.CreateCommand();
            command.CommandText =
                """
                SELECT schema_version || ':' ||
                       (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'promotion_decisions') || ':' ||
                       (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'legacy_promotion_decisions_v1')
                FROM promotion_registry_metadata WHERE singleton = 1;
                """;
            Assert.Equal("1:1:0", Convert.ToString(await command.ExecuteScalarAsync()));
            command.CommandText = "DROP TABLE strategy_authorization_decisions;";
            await command.ExecuteNonQueryAsync();
        }

        var retried = new SqliteStrategyPromotionRegistry(
            new StrategyPromotionRegistryOptions(path, StrategyPromotionRegistryOpenMode.OpenExisting),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            RegisteredExperiments.Instance);
        Assert.Empty(await retried.GetActiveGrantsAsync());
        await using var reopened = new SqliteConnection($"Data Source={path}");
        await reopened.OpenAsync();
        await using var verify = reopened.CreateCommand();
        verify.CommandText =
            "SELECT schema_version FROM promotion_registry_metadata WHERE singleton = 1;";
        Assert.Equal(3L, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OpenExisting_MigratesVersionOneRowsButNeverAuthorizesThem()
    {
        var path = Path.Combine(rootPath, "legacy-promotions.db");
        var current = Decision("legacy-accepted", StrategyPromotionDecisionStatus.Accepted);
        var json = JsonNode.Parse(EvidenceCanonicalJson.SerializeToUtf8Bytes(current))!.AsObject();
        json.Remove("semanticVersion");
        json.Remove("authorizedAuthorization");
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                sha,
                bytes.Length,
                "application/vnd.tradingflow.strategy-promotion-decision+json"),
            new EvidenceObjectNamespace("governance/promotion-decisions"));
        await store.PutIfAbsentAsync(new ImmutableArtifactWriteRequest(artifact), bytes);

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE promotion_registry_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                INSERT INTO promotion_registry_metadata(singleton, schema_version) VALUES(1, 1);
                CREATE TABLE promotion_decisions (
                    decision_id TEXT NOT NULL PRIMARY KEY,
                    status INTEGER NOT NULL,
                    strategy_id TEXT NOT NULL,
                    target_decision_id TEXT NULL,
                    decided_at_utc TEXT NOT NULL,
                    artifact_namespace TEXT NOT NULL,
                    artifact_sha256 TEXT NOT NULL,
                    artifact_byte_length INTEGER NOT NULL,
                    canonical_json TEXT NOT NULL
                );
                INSERT INTO promotion_decisions(
                    decision_id, status, strategy_id, target_decision_id,
                    decided_at_utc, artifact_namespace, artifact_sha256,
                    artifact_byte_length, canonical_json)
                VALUES($id, $status, $strategy, NULL, $decided, $namespace, $sha, $length, $json);
                """;
            command.Parameters.AddWithValue("$id", current.DecisionId);
            command.Parameters.AddWithValue("$status", (int)current.Status);
            command.Parameters.AddWithValue("$strategy", current.StrategyId);
            command.Parameters.AddWithValue("$decided", current.DecidedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$namespace", artifact.ObjectNamespace.Value);
            command.Parameters.AddWithValue("$sha", sha);
            command.Parameters.AddWithValue("$length", bytes.Length);
            command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(bytes));
            await command.ExecuteNonQueryAsync();
        }

        var migrated = new SqliteStrategyPromotionRegistry(
            new StrategyPromotionRegistryOptions(path, StrategyPromotionRegistryOpenMode.OpenExisting),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            RegisteredExperiments.Instance);

        var legacy = Assert.Single(await migrated.GetDecisionHistoryAsync());
        Assert.Equal("0.0.0-legacy", legacy.SemanticVersion);
        Assert.Empty(await migrated.GetActiveDecisionsAsync());

        await using var verification = new SqliteConnection($"Data Source={path}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText =
            "SELECT schema_version || ':' || (SELECT COUNT(*) FROM legacy_promotion_decisions_v1) " +
            "FROM promotion_registry_metadata WHERE singleton = 1;";
        Assert.Equal("3:1", Convert.ToString(await verifyCommand.ExecuteScalarAsync()));

        var reopened = new SqliteStrategyPromotionRegistry(
            new StrategyPromotionRegistryOptions(path, StrategyPromotionRegistryOpenMode.OpenExisting),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            RegisteredExperiments.Instance);
        Assert.Single(await reopened.GetDecisionHistoryAsync());
        Assert.Empty(await reopened.GetActiveDecisionsAsync());
    }

    private SqliteStrategyPromotionRegistry NewRegistry(
        StrategyPromotionRegistryOpenMode mode) =>
        new(
            new StrategyPromotionRegistryOptions(
                Path.Combine(rootPath, "promotions.db"),
                mode),
            catalog,
            catalog,
            store,
            new ConfiguredPromotionPrincipalAuthorizer(["governor@example.test"]),
            RegisteredExperiments.Instance);

    private StrategyPromotionDecision Decision(
        string decisionId,
        StrategyPromotionDecisionStatus status,
        string? targetDecisionId = null,
        string? manifestHash = null,
        string? configHash = null,
        string? holdoutId = null,
        string? universeLedgerId = null,
        string semanticVersion = "1.0.0",
        StrategyExecutionAuthorization? authorizedAuthorization = null) =>
        new(
            decisionId,
            status,
            new EvidenceResearchRunReference(
                run.ResearchRunId,
                manifestHash ?? ResearchManifestHash(run)),
            holdoutId ?? run.Holdout!.HoldoutId,
            run.StudyId,
            configHash ?? run.StudyConfigArtifact.Content.Sha256,
            run.CodeVersion,
            run.InputDatasets,
            universeLedgerId ?? run.UniverseLedgerId,
            run.UniverseLedgerArtifact,
            promotionEvidence,
            "governor@example.test",
            Now.AddMinutes(decisionId.Length),
            $"Governance decision {decisionId}.",
            targetDecisionId,
            semanticVersion: semanticVersion,
            authorizedAuthorization: status == StrategyPromotionDecisionStatus.Accepted
                ? authorizedAuthorization ?? StrategyExecutionAuthorization.PaperShadow
                : null);

    private async Task<EvidenceResearchRunManifest> CreateReadyResearchRunAsync()
    {
        var plan = EvidenceLifecycleTestData.Plan("promotion-evidence");
        await catalog.RegisterCollectionPlanAsync(plan);
        var raw = await EvidenceLifecycleTestData.PutAsync(
            store,
            "raw",
            "raw/alpaca",
            "application/json");
        var observation = EvidenceLifecycleTestData.Observation(
            plan,
            raw,
            "promotion-observation",
            Now);
        await catalog.RegisterSourceObservationAsync(observation);
        var partition = await EvidenceLifecycleTestData.PutAsync(
            store,
            "partition",
            "partitions/bars",
            "application/x-parquet");
        var dataset = EvidenceLifecycleTestData.Dataset(
            plan,
            partition,
            observation.ToReference(),
            Now.AddMinutes(1));
        await catalog.CommitDatasetAsync(dataset);
        var datasetReference = EvidenceLifecycleTestData.DatasetReference(dataset);
        var universe = await PutAsync("universe");
        var config = await PutAsync("config");
        var partitionDefinition = await PutAsync("partition-definition");
        var holdout = new EvidenceHoldoutIdentity(
            "strategy-a",
            Now.AddDays(30),
            Now.AddDays(60),
            [datasetReference],
            "universe-a",
            universe,
            partitionDefinition);
        var assumptions = new EvidenceSimulationAssumptions(
            await PutAsync("cost"),
            await PutAsync("spread"),
            await PutAsync("slippage"),
            await PutAsync("borrow"),
            await PutAsync("benchmark"));
        var outputs = new[]
        {
            await PutAsync("development-report"),
            await PutAsync("validation-report"),
            await PutAsync("holdout-report"),
            await PutAsync("concentration-report")
        };
        return new EvidenceResearchRunManifest(
            "research-a",
            holdout.StudyFamily,
            Now.AddHours(1),
            holdout.Datasets,
            holdout.UniverseLedgerId,
            holdout.UniverseLedgerArtifact,
            config,
            "commit-1",
            new EvidenceStudyPartitions(
                new EvidenceStudyWindow("development", Now.AddDays(-30), Now),
                new EvidenceStudyWindow("validation", Now, holdout.StartUtc),
                new EvidenceStudyWindow("holdout", holdout.StartUtc, holdout.EndUtc)),
            assumptions,
            holdout,
            evidenceReady: true,
            readinessFailures: null,
            outputs);
    }

    private async Task<EvidenceArtifactReference> PutAsync(string value) =>
        await EvidenceLifecycleTestData.PutAsync(
            store,
            value,
            "research/governance-tests",
            "application/json");

    private static string ResearchManifestHash(EvidenceResearchRunManifest manifest) =>
        Convert.ToHexString(
                SHA256.HashData(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)))
            .ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

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

    private sealed record RegistrationAttempt(
        EvidenceCatalogCommitResult? Result,
        Exception? Error);

    private sealed class RegisteredExperiments : IStrategyExperimentArtifactCatalog
    {
        public static RegisteredExperiments Instance { get; } = new();

        public bool IsRegistered(StrategyArtifactIdentity identity) => true;
    }

    private sealed class NoRegisteredExperiments : IStrategyExperimentArtifactCatalog
    {
        public static NoRegisteredExperiments Instance { get; } = new();

        public bool IsRegistered(StrategyArtifactIdentity identity) => false;
    }
}
