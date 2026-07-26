using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Governance;

/// <summary>
/// Append-only, separately authorized strategy governance store. Research code has no
/// dependency on this type or on <see cref="IPromotionRegistry"/>.
/// </summary>
public sealed class SqliteStrategyPromotionRegistry : IPromotionRegistry
{
    private const int SchemaVersion = 1;
    private const string DecisionMediaType =
        "application/vnd.tradingflow.strategy-promotion-decision+json";
    private static readonly EvidenceObjectNamespace DecisionNamespace =
        new("governance/promotion-decisions");
    private static readonly EvidenceObjectNamespace ResearchManifestNamespace =
        new("manifests/research-runs");

    private readonly string connectionString;
    private readonly string databasePath;
    private readonly StrategyPromotionRegistryOpenMode openMode;
    private readonly IEvidenceCatalog evidenceCatalog;
    private readonly IEvidenceReferenceSubjectRegistry referenceSubjects;
    private readonly IImmutableArtifactStore artifactStore;
    private readonly IPromotionPrincipalAuthorizer authorizer;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool initialized;

    public SqliteStrategyPromotionRegistry(
        StrategyPromotionRegistryOptions options,
        IEvidenceCatalog evidenceCatalog,
        IEvidenceReferenceSubjectRegistry referenceSubjects,
        IImmutableArtifactStore artifactStore,
        IPromotionPrincipalAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.evidenceCatalog = evidenceCatalog ?? throw new ArgumentNullException(nameof(evidenceCatalog));
        this.referenceSubjects = referenceSubjects ?? throw new ArgumentNullException(nameof(referenceSubjects));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        databasePath = options.DatabasePath;
        openMode = options.OpenMode;

        var directory = Path.GetDirectoryName(databasePath);
        if (!String.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (openMode == StrategyPromotionRegistryOpenMode.BootstrapNew && File.Exists(databasePath))
        {
            throw new IOException(
                $"A promotion registry already exists at bootstrap path '{databasePath}'.");
        }

        if (openMode == StrategyPromotionRegistryOpenMode.OpenExisting && !File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "The existing strategy promotion registry was not found.",
                databasePath);
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = openMode == StrategyPromotionRegistryOpenMode.BootstrapNew
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task<EvidenceCatalogCommitResult> RegisterDecisionAsync(
        StrategyPromotionDecision authorizedDecision,
        string actingPrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedDecision);
        actingPrincipal = Required(actingPrincipal, nameof(actingPrincipal));
        if (!authorizer.IsAuthorizedHumanPrincipal(actingPrincipal) ||
            !actingPrincipal.Equals(authorizedDecision.ApprovedBy, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The authenticated human principal is not authorized for this promotion decision.");
        }

        var validation = await ValidateEvidenceAsync(authorizedDecision, cancellationToken);
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(authorizedDecision);
        var decisionArtifact = Artifact(bytes, DecisionNamespace, DecisionMediaType);
        await artifactStore.PutIfAbsentAsync(
            new ImmutableArtifactWriteRequest(decisionArtifact),
            bytes,
            cancellationToken);
        await RequireValidArtifactAsync(decisionArtifact, cancellationToken);
        await referenceSubjects.RegisterAsync(
            new EvidenceReferenceSubjectManifest(
                new EvidencePinSubject(
                    EvidencePinSubjectKind.PromotionDecision,
                    authorizedDecision.DecisionId),
                authorizedDecision.DecidedAtUtc,
                validation.ReferencedArtifacts
                    .Append(decisionArtifact)
                    .Distinct()
                    .ToArray()),
            cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var canonicalJson = Encoding.UTF8.GetString(bytes);
        var existing = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM promotion_decisions WHERE decision_id = $id;",
            cancellationToken,
            ("$id", authorizedDecision.DecisionId));
        if (existing is not null)
        {
            if (!existing.Equals(canonicalJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Promotion decision '{authorizedDecision.DecisionId}' was reused with different content.");
            }

            transaction.Commit();
            return new EvidenceCatalogCommitResult(authorizedDecision.DecisionId, true);
        }

        if (authorizedDecision.TargetDecisionId is not null)
        {
            var targetJson = await ReadScalarAsync(
                connection,
                transaction,
                "SELECT canonical_json FROM promotion_decisions WHERE decision_id = $id;",
                cancellationToken,
                ("$id", authorizedDecision.TargetDecisionId));
            if (targetJson is null)
            {
                throw new InvalidOperationException(
                    $"Target promotion decision '{authorizedDecision.TargetDecisionId}' does not exist.");
            }

            var target = EvidenceCanonicalJson.Deserialize<StrategyPromotionDecision>(
                Encoding.UTF8.GetBytes(targetJson));
            if (target.Status != StrategyPromotionDecisionStatus.Accepted ||
                !target.StrategyId.Equals(authorizedDecision.StrategyId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Revocation and supersession must target an accepted decision for the same strategy.");
            }

            var alreadyTerminated = await ReadScalarAsync(
                connection,
                transaction,
                """
                SELECT decision_id
                FROM promotion_decisions
                WHERE target_decision_id = $target
                  AND status IN ($revoked, $superseded)
                LIMIT 1;
                """,
                cancellationToken,
                ("$target", target.DecisionId),
                ("$revoked", (int)StrategyPromotionDecisionStatus.Revoked),
                ("$superseded", (int)StrategyPromotionDecisionStatus.Superseded));
            if (alreadyTerminated is not null)
            {
                throw new InvalidOperationException(
                    $"Accepted decision '{target.DecisionId}' is already inactive.");
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO promotion_decisions(
                decision_id, status, strategy_id, target_decision_id,
                decided_at_utc, artifact_namespace, artifact_sha256,
                artifact_byte_length, canonical_json)
            VALUES(
                $id, $status, $strategy, $target,
                $decided, $namespace, $sha, $length, $json);
            """,
            cancellationToken,
            ("$id", authorizedDecision.DecisionId),
            ("$status", (int)authorizedDecision.Status),
            ("$strategy", authorizedDecision.StrategyId),
            ("$target", (object?)authorizedDecision.TargetDecisionId ?? DBNull.Value),
            ("$decided", Utc(authorizedDecision.DecidedAtUtc)),
            ("$namespace", decisionArtifact.ObjectNamespace.Value),
            ("$sha", decisionArtifact.Content.Sha256),
            ("$length", decisionArtifact.Content.ByteLength),
            ("$json", canonicalJson));
        transaction.Commit();
        return new EvidenceCatalogCommitResult(authorizedDecision.DecisionId, false);
    }

    public async Task<IReadOnlyList<StrategyPromotionDecision>> GetDecisionHistoryAsync(
        string? strategyId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = String.IsNullOrWhiteSpace(strategyId)
            ? """
              SELECT artifact_namespace, artifact_sha256, artifact_byte_length, canonical_json
              FROM promotion_decisions
              ORDER BY decided_at_utc, decision_id;
              """
            : """
              SELECT artifact_namespace, artifact_sha256, artifact_byte_length, canonical_json
              FROM promotion_decisions
              WHERE strategy_id = $strategy
              ORDER BY decided_at_utc, decision_id;
              """;
        if (!String.IsNullOrWhiteSpace(strategyId))
        {
            command.Parameters.AddWithValue("$strategy", strategyId.Trim());
        }

        return await ReadDecisionsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StrategyPromotionDecision>> GetActiveDecisionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT accepted.artifact_namespace, accepted.artifact_sha256,
                   accepted.artifact_byte_length, accepted.canonical_json
            FROM promotion_decisions AS accepted
            WHERE accepted.status = $accepted
              AND NOT EXISTS (
                  SELECT 1
                  FROM promotion_decisions AS terminal
                  WHERE terminal.target_decision_id = accepted.decision_id
                    AND terminal.status IN ($revoked, $superseded)
              )
            ORDER BY accepted.strategy_id, accepted.decided_at_utc, accepted.decision_id;
            """;
        command.Parameters.AddWithValue("$accepted", (int)StrategyPromotionDecisionStatus.Accepted);
        command.Parameters.AddWithValue("$revoked", (int)StrategyPromotionDecisionStatus.Revoked);
        command.Parameters.AddWithValue("$superseded", (int)StrategyPromotionDecisionStatus.Superseded);
        return await ReadDecisionsAsync(command, cancellationToken);
    }

    private async Task<ValidatedPromotionEvidence> ValidateEvidenceAsync(
        StrategyPromotionDecision decision,
        CancellationToken cancellationToken)
    {
        var manifest = await evidenceCatalog.GetResearchRunAsync(
                decision.ResearchRun.ResearchRunId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Research run '{decision.ResearchRun.ResearchRunId}' is not registered.");
        var manifestBytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest);
        var manifestArtifact = Artifact(
            manifestBytes,
            ResearchManifestNamespace,
            "application/vnd.tradingflow.evidence-research-run+json");
        if (!manifestArtifact.Content.Sha256.Equals(
                decision.ResearchRun.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The promotion references a forged research manifest hash.");
        }

        await RequireValidArtifactAsync(manifestArtifact, cancellationToken);
        if (!manifest.EvidenceReady || manifest.ReadinessFailures.Count != 0 || manifest.Holdout is null)
        {
            throw new InvalidOperationException(
                "Only evidence-ready research runs with an immutable holdout may be governed.");
        }

        var consumption = await evidenceCatalog.GetHoldoutConsumptionAsync(
            manifest.Holdout.HoldoutId,
            cancellationToken);
        if (consumption is null ||
            !consumption.ResearchRunId.Equals(manifest.ResearchRunId, StringComparison.Ordinal) ||
            consumption.ConsumedAtUtc != manifest.CreatedAtUtc ||
            !decision.HoldoutId.Equals(manifest.Holdout.HoldoutId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The promotion does not reference the exact holdout consumed atomically by this run.");
        }

        if (!decision.StrategyId.Equals(manifest.StudyId, StringComparison.Ordinal) ||
            !decision.StrategyConfigHash.Equals(
                manifest.StudyConfigArtifact.Content.Sha256,
                StringComparison.Ordinal) ||
            !decision.CodeVersion.Equals(manifest.CodeVersion, StringComparison.Ordinal) ||
            !decision.Datasets.SequenceEqual(manifest.InputDatasets) ||
            !decision.UniverseLedgerId.Equals(manifest.UniverseLedgerId, StringComparison.Ordinal) ||
            decision.UniverseLedgerArtifact != manifest.UniverseLedgerArtifact)
        {
            throw new InvalidDataException(
                "The promotion identity does not match the research config, code, datasets, or universe.");
        }

        if (decision.Evidence.CostModel != manifest.Assumptions.CostModel)
        {
            throw new InvalidDataException(
                "The promotion cost model is not the model used by the research run.");
        }

        var requiredOutputs = new[]
        {
            decision.Evidence.DevelopmentReport,
            decision.Evidence.ValidationReport,
            decision.Evidence.HoldoutReport,
            decision.Evidence.ConcentrationReport
        };
        if (requiredOutputs.Any(output => !manifest.Outputs.Contains(output)))
        {
            throw new InvalidDataException(
                "Every promotion report must be an immutable output of the referenced research run.");
        }

        var referencedArtifacts = new[]
            {
                manifestArtifact,
                manifest.StudyConfigArtifact,
                manifest.UniverseLedgerArtifact,
                manifest.Holdout.PartitionDefinitionArtifact,
                decision.Evidence.CostModel
            }
            .Concat(requiredOutputs)
            .Distinct()
            .ToArray();
        foreach (var artifact in referencedArtifacts)
        {
            await RequireValidArtifactAsync(artifact, cancellationToken);
        }

        return new ValidatedPromotionEvidence(referencedArtifacts);
    }

    private async Task<IReadOnlyList<StrategyPromotionDecision>> ReadDecisionsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var decisions = new List<StrategyPromotionDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var canonicalJson = reader.GetString(3);
            var bytes = Encoding.UTF8.GetBytes(canonicalJson);
            var artifact = new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    reader.GetString(1),
                    reader.GetInt64(2),
                    DecisionMediaType),
                new EvidenceObjectNamespace(reader.GetString(0)));
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actualHash.Equals(artifact.Content.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Promotion decision {artifact.Content.Sha256} has been modified in the registry.");
            }

            await RequireValidArtifactAsync(artifact, cancellationToken);
            decisions.Add(EvidenceCanonicalJson.Deserialize<StrategyPromotionDecision>(bytes));
        }

        return decisions;
    }

    private async Task RequireValidArtifactAsync(
        EvidenceArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        var verification = await artifactStore.VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Required immutable artifact {artifact.ObjectNamespace}/{artifact.Content.Sha256} " +
                $"is missing or corrupt: {verification.FailureReason}");
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SqliteDurability.ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await SqliteDurability.ConfigureDatabaseAsync(connection, cancellationToken);
            if (openMode == StrategyPromotionRegistryOpenMode.BootstrapNew)
            {
                await ExecuteAsync(connection, null, SchemaSql, cancellationToken);
                await ExecuteAsync(
                    connection,
                    null,
                    "INSERT INTO promotion_registry_metadata(singleton, schema_version) VALUES(1, $version);",
                    cancellationToken,
                    ("$version", SchemaVersion));
            }
            else
            {
                var version = await ReadScalarAsync(
                    connection,
                    null,
                    "SELECT CAST(schema_version AS TEXT) FROM promotion_registry_metadata WHERE singleton = 1;",
                    cancellationToken);
                if (!Int32.TryParse(
                        version,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsed) ||
                    parsed != SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Strategy promotion registry schema '{version ?? "missing"}' is unsupported.");
                }

                var integrity = await ReadScalarAsync(
                    connection,
                    null,
                    "PRAGMA quick_check;",
                    cancellationToken);
                if (!String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Strategy promotion registry integrity check failed: {integrity ?? "missing"}.");
                }
            }

            initialized = true;
        }
        finally
        {
            initialization.Release();
        }
    }

    private static EvidenceArtifactReference Artifact(
        ReadOnlySpan<byte> bytes,
        EvidenceObjectNamespace objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                mediaType),
            objectNamespace);

    private static async Task ExecuteAsync(
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

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadScalarAsync(
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

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private const string SchemaSql =
        """
        CREATE TABLE promotion_registry_metadata (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL
        );

        CREATE TABLE promotion_decisions (
            decision_id TEXT NOT NULL PRIMARY KEY,
            status INTEGER NOT NULL,
            strategy_id TEXT NOT NULL,
            target_decision_id TEXT NULL,
            decided_at_utc TEXT NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(target_decision_id) REFERENCES promotion_decisions(decision_id)
        );
        CREATE INDEX ix_promotion_decisions_strategy_time
            ON promotion_decisions(strategy_id, decided_at_utc, decision_id);
        CREATE INDEX ix_promotion_decisions_target
            ON promotion_decisions(target_decision_id, status);
        """;

    private sealed record ValidatedPromotionEvidence(
        IReadOnlyList<EvidenceArtifactReference> ReferencedArtifacts);
}
