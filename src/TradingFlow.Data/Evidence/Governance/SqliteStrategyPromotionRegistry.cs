using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Governance;

/// <summary>
/// Append-only, separately authorized strategy governance store. Research code has no
/// dependency on this type or on <see cref="IPromotionRegistry"/>.
/// </summary>
public sealed class SqliteStrategyPromotionRegistry :
    IPromotionRegistry,
    IStrategyAuthorizationPolicy,
    IStrategyExperimentAuthorizationCommands
{
    private const int SchemaVersion = 3;
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
    private readonly IStrategyExperimentArtifactCatalog experimentArtifacts;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool initialized;

    public SqliteStrategyPromotionRegistry(
        StrategyPromotionRegistryOptions options,
        IEvidenceCatalog evidenceCatalog,
        IEvidenceReferenceSubjectRegistry referenceSubjects,
        IImmutableArtifactStore artifactStore,
        IPromotionPrincipalAuthorizer authorizer,
        IStrategyExperimentArtifactCatalog experimentArtifacts)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.evidenceCatalog = evidenceCatalog ?? throw new ArgumentNullException(nameof(evidenceCatalog));
        this.referenceSubjects = referenceSubjects ?? throw new ArgumentNullException(nameof(referenceSubjects));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        this.experimentArtifacts = experimentArtifacts
            ?? throw new ArgumentNullException(nameof(experimentArtifacts));
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
        RequireAuthorizedPrincipal(authorizedDecision, actingPrincipal);
        if (authorizedDecision.Status == StrategyPromotionDecisionStatus.Superseded)
        {
            throw new InvalidOperationException(
                "Supersession requires RegisterSupersessionAsync so the terminal decision and replacement commit atomically.");
        }

        RequireCompleteIdentity(authorizedDecision);
        var prepared = await PrepareDecisionAsync(authorizedDecision, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var result = await InsertPreparedDecisionAsync(
            connection,
            transaction,
            prepared,
            cancellationToken);
        transaction.Commit();
        return result;
    }

    public async Task<StrategySupersessionCommitResult> RegisterSupersessionAsync(
        StrategyPromotionDecision supersessionDecision,
        StrategyPromotionDecision acceptedReplacement,
        string actingPrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supersessionDecision);
        ArgumentNullException.ThrowIfNull(acceptedReplacement);
        RequireAuthorizedPrincipal(supersessionDecision, actingPrincipal);
        RequireAuthorizedPrincipal(acceptedReplacement, actingPrincipal);
        RequireCompleteIdentity(supersessionDecision);
        RequireCompleteIdentity(acceptedReplacement);
        if (supersessionDecision.Status != StrategyPromotionDecisionStatus.Superseded ||
            supersessionDecision.TargetDecisionId is null ||
            acceptedReplacement.Status != StrategyPromotionDecisionStatus.Accepted)
        {
            throw new InvalidOperationException(
                "Atomic supersession requires one superseded decision with a target and one accepted replacement.");
        }

        var preparedSupersession = await PrepareDecisionAsync(supersessionDecision, cancellationToken);
        var preparedReplacement = await PrepareDecisionAsync(acceptedReplacement, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var targetJson = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM promotion_decisions WHERE decision_id = $id;",
            cancellationToken,
            ("$id", supersessionDecision.TargetDecisionId));
        if (targetJson is null)
        {
            throw new InvalidOperationException(
                $"Target promotion decision '{supersessionDecision.TargetDecisionId}' does not exist.");
        }

        var target = EvidenceCanonicalJson.Deserialize<StrategyPromotionDecision>(
            Encoding.UTF8.GetBytes(targetJson));
        if (target.Status != StrategyPromotionDecisionStatus.Accepted ||
            !SameIdentity(target, supersessionDecision) ||
            !target.StrategyId.Equals(acceptedReplacement.StrategyId, StringComparison.Ordinal) ||
            target.AuthorizedAuthorization != acceptedReplacement.AuthorizedAuthorization ||
            target.SemanticVersion.Equals(acceptedReplacement.SemanticVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The replacement must authorize the same strategy family and grant type under a different semantic version.");
        }

        var replacementResult = await InsertPreparedDecisionAsync(
            connection,
            transaction,
            preparedReplacement,
            cancellationToken);
        var supersessionResult = await InsertPreparedDecisionAsync(
            connection,
            transaction,
            preparedSupersession,
            cancellationToken);
        transaction.Commit();
        return new StrategySupersessionCommitResult(supersessionResult, replacementResult);
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
              AND accepted.identity_complete = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM promotion_decisions AS terminal
                  WHERE terminal.target_decision_id = accepted.decision_id
                    AND terminal.status IN ($revoked, $superseded)
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM promotion_decisions AS suspension
                  JOIN promotion_decisions AS suspended_target
                    ON suspended_target.decision_id = suspension.target_decision_id
                  WHERE suspension.status = $suspended
                    AND suspended_target.strategy_id = accepted.strategy_id
                    AND suspended_target.semantic_version = accepted.semantic_version
                    AND suspended_target.strategy_content_sha256 = accepted.strategy_content_sha256
                    AND NOT EXISTS (
                        SELECT 1
                        FROM promotion_decisions AS resume
                        WHERE resume.target_decision_id = suspension.decision_id
                          AND resume.status = $resumed
                      )
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM strategy_authorization_suspensions AS authorization_suspension
                  WHERE authorization_suspension.strategy_id = accepted.strategy_id
                    AND authorization_suspension.semantic_version = accepted.semantic_version
                    AND authorization_suspension.strategy_content_sha256 = accepted.strategy_content_sha256
              )
              AND EXISTS (
                  SELECT 1
                  FROM strategy_authorization_heads AS experiment
                  WHERE experiment.strategy_id = accepted.strategy_id
                    AND experiment.semantic_version = accepted.semantic_version
                    AND experiment.strategy_content_sha256 = accepted.strategy_content_sha256
                    AND experiment.authorization = $paperExperiment
              )
              AND (
                  accepted.authorized_lifecycle = $paperShadow
                  OR (
                      accepted.authorized_lifecycle = $validated
                      AND EXISTS (
                          SELECT 1
                          FROM strategy_authorization_heads AS shadow
                          WHERE shadow.strategy_id = accepted.strategy_id
                            AND shadow.semantic_version = accepted.semantic_version
                            AND shadow.strategy_content_sha256 = accepted.strategy_content_sha256
                            AND shadow.authorization = $paperShadow
                      )
                  )
              )
            ORDER BY accepted.strategy_id, accepted.decided_at_utc, accepted.decision_id;
            """;
        command.Parameters.AddWithValue("$accepted", (int)StrategyPromotionDecisionStatus.Accepted);
        command.Parameters.AddWithValue("$revoked", (int)StrategyPromotionDecisionStatus.Revoked);
        command.Parameters.AddWithValue("$superseded", (int)StrategyPromotionDecisionStatus.Superseded);
        command.Parameters.AddWithValue("$suspended", (int)StrategyPromotionDecisionStatus.Suspended);
        command.Parameters.AddWithValue("$resumed", (int)StrategyPromotionDecisionStatus.Resumed);
        command.Parameters.AddWithValue("$paperExperiment", (int)StrategyExecutionAuthorization.PaperExperiment);
        command.Parameters.AddWithValue("$paperShadow", (int)StrategyExecutionAuthorization.PaperShadow);
        command.Parameters.AddWithValue("$validated", (int)StrategyExecutionAuthorization.Validated);
        return await ReadDecisionsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StrategyAuthorizationGrant>> GetActiveGrantsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT head.strategy_id, head.semantic_version, head.strategy_content_sha256,
                   head.authorization, head.grant_decision_id, decision.decided_at_utc
            FROM strategy_authorization_heads AS head
            JOIN strategy_authorization_decisions AS decision
              ON decision.decision_id = head.grant_decision_id
            WHERE NOT EXISTS (
                    SELECT 1
                    FROM strategy_authorization_suspensions AS suspension
                    WHERE suspension.strategy_id = head.strategy_id
                      AND suspension.semantic_version = head.semantic_version
                      AND suspension.strategy_content_sha256 = head.strategy_content_sha256
                  )
              AND (
                    head.authorization = $paperExperiment
                    OR (
                        head.authorization = $paperShadow
                        AND EXISTS (
                            SELECT 1
                            FROM strategy_authorization_heads AS experiment
                            WHERE experiment.strategy_id = head.strategy_id
                              AND experiment.semantic_version = head.semantic_version
                              AND experiment.strategy_content_sha256 = head.strategy_content_sha256
                              AND experiment.authorization = $paperExperiment
                        )
                    )
                    OR (
                        head.authorization = $validated
                        AND EXISTS (
                        SELECT 1
                        FROM strategy_authorization_heads AS shadow
                        WHERE shadow.strategy_id = head.strategy_id
                          AND shadow.semantic_version = head.semantic_version
                          AND shadow.strategy_content_sha256 = head.strategy_content_sha256
                          AND shadow.authorization = $paperShadow
                        )
                        AND EXISTS (
                            SELECT 1
                            FROM strategy_authorization_heads AS experiment
                            WHERE experiment.strategy_id = head.strategy_id
                              AND experiment.semantic_version = head.semantic_version
                              AND experiment.strategy_content_sha256 = head.strategy_content_sha256
                              AND experiment.authorization = $paperExperiment
                        )
                    )
                  )
            ORDER BY head.strategy_id, head.semantic_version, head.authorization;
            """;
        command.Parameters.AddWithValue("$paperExperiment", (int)StrategyExecutionAuthorization.PaperExperiment);
        command.Parameters.AddWithValue("$validated", (int)StrategyExecutionAuthorization.Validated);
        command.Parameters.AddWithValue("$paperShadow", (int)StrategyExecutionAuthorization.PaperShadow);
        var grants = new List<StrategyAuthorizationGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            grants.Add(new StrategyAuthorizationGrant(
                new StrategyArtifactIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                (StrategyExecutionAuthorization)reader.GetInt32(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }

        return grants;
    }

    public async Task<StrategyEntryAdmission> AdmitNewEntryAsync(
        StrategyArtifactIdentity identity,
        StrategySelectionMode selectionMode,
        Guid intentId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (intentId == Guid.Empty)
        {
            throw new ArgumentException("Entry intent ID cannot be empty.", nameof(intentId));
        }

        if (admittedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Entry admission time must be UTC.", nameof(admittedAtUtc));
        }

        var requiredAuthorization = selectionMode switch
        {
            StrategySelectionMode.RunPaperExperiment => StrategyExecutionAuthorization.PaperExperiment,
            StrategySelectionMode.RunPaperShadow => StrategyExecutionAuthorization.PaperShadow,
            StrategySelectionMode.RunLive => StrategyExecutionAuthorization.Validated,
            _ => throw new InvalidOperationException(
                $"Selection mode '{selectionMode}' cannot admit a broker entry.")
        };

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var existing = await ReadAdmissionAsync(connection, transaction, intentId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Identity != identity || existing.SelectionMode != selectionMode)
            {
                throw new InvalidDataException(
                    $"Entry intent '{intentId}' was reused with a different strategy authorization context.");
            }

            transaction.Commit();
            return existing;
        }

        var grantDecisionId = await ReadScalarAsync(
            connection,
            transaction,
            """
            SELECT head.grant_decision_id
            FROM strategy_authorization_heads AS head
            WHERE head.strategy_id = $strategy
              AND head.semantic_version = $semanticVersion
              AND head.strategy_content_sha256 = $contentSha256
              AND head.authorization = $authorization
              AND NOT EXISTS (
                  SELECT 1
                  FROM strategy_authorization_suspensions AS suspension
                  WHERE suspension.strategy_id = head.strategy_id
                    AND suspension.semantic_version = head.semantic_version
                    AND suspension.strategy_content_sha256 = head.strategy_content_sha256
              )
              AND (
                  $authorization = $paperExperiment
                  OR (
                      $authorization = $paperShadow
                      AND EXISTS (
                          SELECT 1
                          FROM strategy_authorization_heads AS experiment
                          WHERE experiment.strategy_id = head.strategy_id
                            AND experiment.semantic_version = head.semantic_version
                            AND experiment.strategy_content_sha256 = head.strategy_content_sha256
                            AND experiment.authorization = $paperExperiment
                      )
                  )
                  OR (
                      $authorization = $validated
                      AND EXISTS (
                      SELECT 1
                      FROM strategy_authorization_heads AS shadow
                      WHERE shadow.strategy_id = head.strategy_id
                        AND shadow.semantic_version = head.semantic_version
                        AND shadow.strategy_content_sha256 = head.strategy_content_sha256
                        AND shadow.authorization = $paperShadow
                      )
                      AND EXISTS (
                          SELECT 1
                          FROM strategy_authorization_heads AS experiment
                          WHERE experiment.strategy_id = head.strategy_id
                            AND experiment.semantic_version = head.semantic_version
                            AND experiment.strategy_content_sha256 = head.strategy_content_sha256
                            AND experiment.authorization = $paperExperiment
                      )
                  )
              )
            LIMIT 1;
            """,
            cancellationToken,
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$authorization", (int)requiredAuthorization),
            ("$paperExperiment", (int)StrategyExecutionAuthorization.PaperExperiment),
            ("$validated", (int)StrategyExecutionAuthorization.Validated),
            ("$paperShadow", (int)StrategyExecutionAuthorization.PaperShadow));
        if (grantDecisionId is null)
        {
            throw new UnauthorizedAccessException(
                $"Strategy '{identity.StrategyId}@{identity.SemanticVersion}' is not currently authorized for {selectionMode}.");
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO strategy_entry_admissions(
                intent_id, strategy_id, semantic_version, strategy_content_sha256,
                selection_mode, grant_decision_id, admitted_at_utc)
            VALUES($intent, $strategy, $semanticVersion, $contentSha256,
                   $selectionMode, $grantDecision, $admittedAtUtc);
            """,
            cancellationToken,
            ("$intent", intentId.ToString("D")),
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$selectionMode", (int)selectionMode),
            ("$grantDecision", grantDecisionId),
            ("$admittedAtUtc", Utc(admittedAtUtc)));
        var admission = await ReadAdmissionAsync(connection, transaction, intentId, cancellationToken)
            ?? throw new InvalidDataException("The committed entry admission could not be read back.");
        transaction.Commit();
        return admission;
    }

    public Task GrantPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        RegisterExperimentTransitionAsync(
            identity,
            decisionId,
            StrategyPromotionDecisionStatus.Accepted,
            null,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);

    public Task RevokePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        RegisterExperimentTransitionAsync(
            identity,
            decisionId,
            StrategyPromotionDecisionStatus.Revoked,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);

    public Task SuspendPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        RegisterExperimentTransitionAsync(
            identity,
            decisionId,
            StrategyPromotionDecisionStatus.Suspended,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);

    public Task ResumePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        RegisterExperimentTransitionAsync(
            identity,
            decisionId,
            StrategyPromotionDecisionStatus.Resumed,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);

    public async Task SupersedePaperExperimentAsync(
        StrategyArtifactIdentity existingIdentity,
        string supersessionDecisionId,
        string targetDecisionId,
        StrategyArtifactIdentity replacementIdentity,
        string replacementDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingIdentity);
        ArgumentNullException.ThrowIfNull(replacementIdentity);
        if (!existingIdentity.StrategyId.Equals(replacementIdentity.StrategyId, StringComparison.Ordinal) ||
            existingIdentity.SemanticVersion.Equals(replacementIdentity.SemanticVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Paper-experiment supersession requires the same strategy family under a different semantic version.");
        }

        if (!experimentArtifacts.IsRegistered(replacementIdentity))
        {
            throw new InvalidOperationException("The replacement paper-experiment artifact is not registered.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await InsertExperimentDecisionAsync(
            connection,
            transaction,
            replacementIdentity,
            replacementDecisionId,
            StrategyPromotionDecisionStatus.Accepted,
            null,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);
        await InsertExperimentDecisionAsync(
            connection,
            transaction,
            existingIdentity,
            supersessionDecisionId,
            StrategyPromotionDecisionStatus.Superseded,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);
        transaction.Commit();
    }

    private async Task RegisterExperimentTransitionAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        StrategyPromotionDecisionStatus status,
        string? targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await InsertExperimentDecisionAsync(
            connection,
            transaction,
            identity,
            decisionId,
            status,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason,
            cancellationToken);
        transaction.Commit();
    }

    private async Task InsertExperimentDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyArtifactIdentity identity,
        string decisionId,
        StrategyPromotionDecisionStatus status,
        string? targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        decisionId = Required(decisionId, nameof(decisionId));
        actor = Required(actor, nameof(actor));
        reason = Required(reason, nameof(reason));
        if (decidedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Authorization decision time must be UTC.", nameof(decidedAtUtc));
        }

        var requiresTarget = status is StrategyPromotionDecisionStatus.Revoked or
            StrategyPromotionDecisionStatus.Superseded or
            StrategyPromotionDecisionStatus.Suspended or
            StrategyPromotionDecisionStatus.Resumed;
        if (requiresTarget != !String.IsNullOrWhiteSpace(targetDecisionId))
        {
            throw new ArgumentException(
                "Revoked, superseded, suspended, and resumed experiment decisions require a target.",
                nameof(targetDecisionId));
        }

        targetDecisionId = String.IsNullOrWhiteSpace(targetDecisionId) ? null : targetDecisionId.Trim();
        var record = new ExperimentAuthorizationDecisionRecord(
            decisionId,
            status,
            identity,
            targetDecisionId,
            actor,
            decidedAtUtc,
            reason);
        var canonicalJson = Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(record));
        var existing = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM strategy_authorization_decisions WHERE decision_id = $id;",
            cancellationToken,
            ("$id", decisionId));
        if (existing is not null)
        {
            if (!existing.Equals(canonicalJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Strategy authorization decision '{decisionId}' was reused with different content.");
            }

            return;
        }

        if (status == StrategyPromotionDecisionStatus.Accepted)
        {
            if (!experimentArtifacts.IsRegistered(identity))
            {
                throw new InvalidOperationException(
                    "Paper-experiment authorization requires an exact immutable experiment artifact.");
            }

            var existingHead = await ReadAuthorizationHeadAsync(
                connection,
                transaction,
                identity,
                StrategyExecutionAuthorization.PaperExperiment,
                cancellationToken);
            if (existingHead is not null)
            {
                throw new InvalidOperationException(
                    $"Strategy identity '{identity.StrategyId}@{identity.SemanticVersion}' already has an active " +
                    $"paper-experiment grant in decision '{existingHead}'. A retry must reuse its original decision ID.");
            }
        }
        else
        {
            var target = await ReadExperimentDecisionAsync(
                connection,
                transaction,
                targetDecisionId!,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Target strategy authorization decision '{targetDecisionId}' does not exist.");
            if (target.Identity != identity)
            {
                throw new InvalidOperationException(
                    "Paper-experiment authorization transitions must target the exact strategy identity.");
            }

            if (status == StrategyPromotionDecisionStatus.Resumed)
            {
                if (target.Status != StrategyPromotionDecisionStatus.Suspended)
                {
                    throw new InvalidOperationException("Resume must target an active suspension.");
                }

                var activeSuspension = await ReadScalarAsync(
                    connection,
                    transaction,
                    """
                    SELECT suspension_decision_id
                    FROM strategy_authorization_suspensions
                    WHERE strategy_id = $strategy
                      AND semantic_version = $semanticVersion
                      AND strategy_content_sha256 = $contentSha256;
                    """,
                    cancellationToken,
                    ("$strategy", identity.StrategyId),
                    ("$semanticVersion", identity.SemanticVersion),
                    ("$contentSha256", identity.ContentSha256));
                if (!String.Equals(activeSuspension, targetDecisionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The targeted paper-experiment suspension is not active.");
                }
            }
            else
            {
                if (target.Status != StrategyPromotionDecisionStatus.Accepted)
                {
                    throw new InvalidOperationException(
                        "Revoke, supersede, and suspend must target an accepted paper-experiment grant.");
                }

                var activeHead = await ReadAuthorizationHeadAsync(
                    connection,
                    transaction,
                    identity,
                    StrategyExecutionAuthorization.PaperExperiment,
                    cancellationToken);
                if (!String.Equals(activeHead, targetDecisionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The targeted paper-experiment grant is not active.");
                }
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO strategy_authorization_decisions(
                decision_id, status, strategy_id, semantic_version,
                strategy_content_sha256, authorization, target_decision_id,
                actor, decided_at_utc, reason, canonical_json)
            VALUES($id, $status, $strategy, $semanticVersion,
                   $contentSha256, $authorization, $target,
                   $actor, $decidedAtUtc, $reason, $canonicalJson);
            """,
            cancellationToken,
            ("$id", decisionId),
            ("$status", (int)status),
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$authorization", (int)StrategyExecutionAuthorization.PaperExperiment),
            ("$target", (object?)targetDecisionId ?? DBNull.Value),
            ("$actor", actor),
            ("$decidedAtUtc", Utc(decidedAtUtc)),
            ("$reason", reason),
            ("$canonicalJson", canonicalJson));

        switch (status)
        {
            case StrategyPromotionDecisionStatus.Accepted:
                await InsertAuthorizationHeadAsync(
                    connection,
                    transaction,
                    identity,
                    StrategyExecutionAuthorization.PaperExperiment,
                    decisionId,
                    cancellationToken);
                break;
            case StrategyPromotionDecisionStatus.Revoked:
            case StrategyPromotionDecisionStatus.Superseded:
                await DeleteAuthorizationHeadAsync(
                    connection,
                    transaction,
                    identity,
                    StrategyExecutionAuthorization.PaperExperiment,
                    targetDecisionId!,
                    cancellationToken);
                break;
            case StrategyPromotionDecisionStatus.Suspended:
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO strategy_authorization_suspensions(
                        strategy_id, semantic_version, strategy_content_sha256,
                        suspension_decision_id)
                    VALUES($strategy, $semanticVersion, $contentSha256, $decisionId);
                    """,
                    cancellationToken,
                    ("$strategy", identity.StrategyId),
                    ("$semanticVersion", identity.SemanticVersion),
                    ("$contentSha256", identity.ContentSha256),
                    ("$decisionId", decisionId));
                break;
            case StrategyPromotionDecisionStatus.Resumed:
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM strategy_authorization_suspensions
                    WHERE strategy_id = $strategy
                      AND semantic_version = $semanticVersion
                      AND strategy_content_sha256 = $contentSha256
                      AND suspension_decision_id = $target;
                    """,
                    cancellationToken,
                    ("$strategy", identity.StrategyId),
                    ("$semanticVersion", identity.SemanticVersion),
                    ("$contentSha256", identity.ContentSha256),
                    ("$target", targetDecisionId!));
                break;
            default:
                throw new InvalidOperationException(
                    $"Experiment authorization status '{status}' is unsupported.");
        }
    }

    private static async Task<string?> ReadAuthorizationHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyArtifactIdentity identity,
        StrategyExecutionAuthorization authorization,
        CancellationToken cancellationToken) =>
        await ReadScalarAsync(
            connection,
            transaction,
            """
            SELECT grant_decision_id
            FROM strategy_authorization_heads
            WHERE strategy_id = $strategy
              AND semantic_version = $semanticVersion
              AND strategy_content_sha256 = $contentSha256
              AND authorization = $authorization;
            """,
            cancellationToken,
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$authorization", (int)authorization));

    private static async Task InsertAuthorizationHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyArtifactIdentity identity,
        StrategyExecutionAuthorization authorization,
        string grantDecisionId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO strategy_authorization_heads(
                strategy_id, semantic_version, strategy_content_sha256,
                authorization, grant_decision_id)
            VALUES($strategy, $semanticVersion, $contentSha256,
                   $authorization, $grantDecisionId);
            """,
            cancellationToken,
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$authorization", (int)authorization),
            ("$grantDecisionId", grantDecisionId));

    private static async Task DeleteAuthorizationHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyArtifactIdentity identity,
        StrategyExecutionAuthorization authorization,
        string expectedGrantDecisionId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM strategy_authorization_heads
            WHERE strategy_id = $strategy
              AND semantic_version = $semanticVersion
              AND strategy_content_sha256 = $contentSha256
              AND authorization = $authorization
              AND grant_decision_id = $grantDecisionId;
            """,
            cancellationToken,
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256),
            ("$authorization", (int)authorization),
            ("$grantDecisionId", expectedGrantDecisionId));
    }

    private static async Task<ExperimentAuthorizationDecisionRecord?> ReadExperimentDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string decisionId,
        CancellationToken cancellationToken)
    {
        var json = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM strategy_authorization_decisions WHERE decision_id = $id;",
            cancellationToken,
            ("$id", decisionId));
        return json is null
            ? null
            : EvidenceCanonicalJson.Deserialize<ExperimentAuthorizationDecisionRecord>(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<StrategyEntryAdmission?> ReadAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid intentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT sequence, strategy_id, semantic_version, strategy_content_sha256,
                   selection_mode, grant_decision_id, admitted_at_utc
            FROM strategy_entry_admissions
            WHERE intent_id = $intent;
            """;
        command.Parameters.AddWithValue("$intent", intentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StrategyEntryAdmission(
            intentId,
            new StrategyArtifactIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3)),
            (StrategySelectionMode)reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt64(0),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
    }

    private static void RequireCompleteIdentity(StrategyPromotionDecision decision)
    {
        if (decision.SemanticVersion.Equals("0.0.0-legacy", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "New promotion decisions require an explicit semantic version and fully resolved content hash.");
        }
    }

    private void RequireAuthorizedPrincipal(
        StrategyPromotionDecision decision,
        string actingPrincipal)
    {
        actingPrincipal = Required(actingPrincipal, nameof(actingPrincipal));
        if (!authorizer.IsAuthorizedHumanPrincipal(actingPrincipal) ||
            !actingPrincipal.Equals(decision.ApprovedBy, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The authenticated human principal is not authorized for this promotion decision.");
        }
    }

    private async Task<PreparedPromotionDecision> PrepareDecisionAsync(
        StrategyPromotionDecision decision,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateEvidenceAsync(decision, cancellationToken);
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(decision);
        var artifact = Artifact(bytes, DecisionNamespace, DecisionMediaType);
        await artifactStore.PutIfAbsentAsync(
            new ImmutableArtifactWriteRequest(artifact),
            bytes,
            cancellationToken);
        await RequireValidArtifactAsync(artifact, cancellationToken);
        await referenceSubjects.RegisterAsync(
            new EvidenceReferenceSubjectManifest(
                new EvidencePinSubject(
                    EvidencePinSubjectKind.PromotionDecision,
                    decision.DecisionId),
                decision.DecidedAtUtc,
                validation.ReferencedArtifacts
                    .Append(artifact)
                    .Distinct()
                    .ToArray()),
            cancellationToken);
        return new PreparedPromotionDecision(
            decision,
            artifact,
            Encoding.UTF8.GetString(bytes));
    }

    private async Task<EvidenceCatalogCommitResult> InsertPreparedDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PreparedPromotionDecision prepared,
        CancellationToken cancellationToken)
    {
        var decision = prepared.Decision;
        var existing = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT canonical_json FROM promotion_decisions WHERE decision_id = $id;",
            cancellationToken,
            ("$id", decision.DecisionId));
        if (existing is not null)
        {
            if (!existing.Equals(prepared.CanonicalJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Promotion decision '{decision.DecisionId}' was reused with different content.");
            }

            return new EvidenceCatalogCommitResult(decision.DecisionId, true);
        }

        if (decision.TargetDecisionId is not null)
        {
            var targetJson = await ReadScalarAsync(
                connection,
                transaction,
                "SELECT canonical_json FROM promotion_decisions WHERE decision_id = $id;",
                cancellationToken,
                ("$id", decision.TargetDecisionId));
            if (targetJson is null)
            {
                throw new InvalidOperationException(
                    $"Target promotion decision '{decision.TargetDecisionId}' does not exist.");
            }

            var target = EvidenceCanonicalJson.Deserialize<StrategyPromotionDecision>(
                Encoding.UTF8.GetBytes(targetJson));
            await ValidateTargetTransitionAsync(
                connection,
                transaction,
                decision,
                target,
                cancellationToken);
        }

        await ValidateAcceptedPrerequisitesAsync(
            connection,
            transaction,
            decision,
            cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO promotion_decisions(
                decision_id, status, strategy_id, semantic_version,
                strategy_content_sha256, authorized_lifecycle,
                identity_complete, target_decision_id,
                decided_at_utc, artifact_namespace, artifact_sha256,
                artifact_byte_length, canonical_json)
            VALUES(
                $id, $status, $strategy, $semanticVersion,
                $contentSha256, $authorizedLifecycle,
                1, $target,
                $decided, $namespace, $sha, $length, $json);
            """,
            cancellationToken,
            ("$id", decision.DecisionId),
            ("$status", (int)decision.Status),
            ("$strategy", decision.StrategyId),
            ("$semanticVersion", decision.SemanticVersion),
            ("$contentSha256", decision.StrategyConfigHash),
            ("$authorizedLifecycle", decision.AuthorizedAuthorization is { } authorization
                ? (int)authorization
                : DBNull.Value),
            ("$target", (object?)decision.TargetDecisionId ?? DBNull.Value),
            ("$decided", Utc(decision.DecidedAtUtc)),
            ("$namespace", prepared.Artifact.ObjectNamespace.Value),
            ("$sha", prepared.Artifact.Content.Sha256),
            ("$length", prepared.Artifact.Content.ByteLength),
            ("$json", prepared.CanonicalJson));
        await ApplyPromotionAuthorizationProjectionAsync(
            connection,
            transaction,
            decision,
            cancellationToken);
        return new EvidenceCatalogCommitResult(decision.DecisionId, false);
    }

    private static async Task ApplyPromotionAuthorizationProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyPromotionDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Status == StrategyPromotionDecisionStatus.Rejected)
        {
            return;
        }

        StrategyExecutionAuthorization authorization;
        if (decision.Status == StrategyPromotionDecisionStatus.Accepted)
        {
            authorization = decision.AuthorizedAuthorization
                ?? throw new InvalidDataException("Accepted promotion decision has no authorization grant.");
        }
        else
        {
            var targetAuthorization = await ReadScalarAsync(
                connection,
                transaction,
                "SELECT CAST(authorization AS TEXT) FROM strategy_authorization_decisions WHERE decision_id = $target;",
                cancellationToken,
                ("$target", decision.TargetDecisionId!));
            if (!Int32.TryParse(targetAuthorization, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                !Enum.IsDefined(typeof(StrategyExecutionAuthorization), parsed))
            {
                throw new InvalidDataException(
                    $"Target authorization decision '{decision.TargetDecisionId}' has no valid grant type.");
            }

            authorization = (StrategyExecutionAuthorization)parsed;
        }

        var projection = new PromotionAuthorizationDecisionRecord(
            decision.DecisionId,
            decision.Status,
            decision.ArtifactIdentity,
            authorization,
            decision.TargetDecisionId,
            decision.ApprovedBy,
            decision.DecidedAtUtc,
            decision.Reason);
        var canonicalJson = Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(projection));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO strategy_authorization_decisions(
                decision_id, status, strategy_id, semantic_version,
                strategy_content_sha256, authorization, target_decision_id,
                actor, decided_at_utc, reason, canonical_json)
            VALUES($id, $status, $strategy, $semanticVersion,
                   $contentSha256, $authorization, $target,
                   $actor, $decidedAtUtc, $reason, $canonicalJson);
            """,
            cancellationToken,
            ("$id", decision.DecisionId),
            ("$status", (int)decision.Status),
            ("$strategy", decision.StrategyId),
            ("$semanticVersion", decision.SemanticVersion),
            ("$contentSha256", decision.StrategyConfigHash),
            ("$authorization", (int)authorization),
            ("$target", (object?)decision.TargetDecisionId ?? DBNull.Value),
            ("$actor", decision.ApprovedBy),
            ("$decidedAtUtc", Utc(decision.DecidedAtUtc)),
            ("$reason", decision.Reason),
            ("$canonicalJson", canonicalJson));

        switch (decision.Status)
        {
            case StrategyPromotionDecisionStatus.Accepted:
                await InsertAuthorizationHeadAsync(
                    connection,
                    transaction,
                    decision.ArtifactIdentity,
                    authorization,
                    decision.DecisionId,
                    cancellationToken);
                break;
            case StrategyPromotionDecisionStatus.Revoked:
            case StrategyPromotionDecisionStatus.Superseded:
                await DeleteAuthorizationHeadAsync(
                    connection,
                    transaction,
                    decision.ArtifactIdentity,
                    authorization,
                    decision.TargetDecisionId!,
                    cancellationToken);
                break;
            case StrategyPromotionDecisionStatus.Suspended:
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO strategy_authorization_suspensions(
                        strategy_id, semantic_version, strategy_content_sha256,
                        suspension_decision_id)
                    VALUES($strategy, $semanticVersion, $contentSha256, $decisionId);
                    """,
                    cancellationToken,
                    ("$strategy", decision.StrategyId),
                    ("$semanticVersion", decision.SemanticVersion),
                    ("$contentSha256", decision.StrategyConfigHash),
                    ("$decisionId", decision.DecisionId));
                break;
            case StrategyPromotionDecisionStatus.Resumed:
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM strategy_authorization_suspensions
                    WHERE strategy_id = $strategy
                      AND semantic_version = $semanticVersion
                      AND strategy_content_sha256 = $contentSha256
                      AND suspension_decision_id = $target;
                    """,
                    cancellationToken,
                    ("$strategy", decision.StrategyId),
                    ("$semanticVersion", decision.SemanticVersion),
                    ("$contentSha256", decision.StrategyConfigHash),
                    ("$target", decision.TargetDecisionId!));
                break;
        }
    }

    private async Task ValidateAcceptedPrerequisitesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyPromotionDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Status != StrategyPromotionDecisionStatus.Accepted)
        {
            return;
        }

        var existingGrant = await ReadScalarAsync(
            connection,
            transaction,
            """
            SELECT accepted.decision_id
            FROM promotion_decisions AS accepted
            WHERE accepted.status = $accepted
              AND accepted.authorized_lifecycle = $authorization
              AND accepted.strategy_id = $strategy
              AND accepted.semantic_version = $semanticVersion
              AND accepted.strategy_content_sha256 = $contentSha256
              AND NOT EXISTS (
                  SELECT 1
                  FROM promotion_decisions AS terminal
                  WHERE terminal.target_decision_id = accepted.decision_id
                    AND terminal.status IN ($revoked, $superseded)
              )
            LIMIT 1;
            """,
            cancellationToken,
            ("$accepted", (int)StrategyPromotionDecisionStatus.Accepted),
            ("$authorization", (int)decision.AuthorizedAuthorization!.Value),
            ("$strategy", decision.StrategyId),
            ("$semanticVersion", decision.SemanticVersion),
            ("$contentSha256", decision.StrategyConfigHash),
            ("$revoked", (int)StrategyPromotionDecisionStatus.Revoked),
            ("$superseded", (int)StrategyPromotionDecisionStatus.Superseded));
        if (existingGrant is not null)
        {
            throw new InvalidOperationException(
                $"Strategy identity '{decision.StrategyId}@{decision.SemanticVersion}' already has a non-terminal " +
                $"{decision.AuthorizedAuthorization} authorization in decision '{existingGrant}'.");
        }

        if (decision.AuthorizedAuthorization == StrategyExecutionAuthorization.PaperShadow)
        {
            var experimentGrant = await ReadAuthorizationHeadAsync(
                connection,
                transaction,
                decision.ArtifactIdentity,
                StrategyExecutionAuthorization.PaperExperiment,
                cancellationToken);
            var suspended = await IsIdentitySuspendedAsync(
                connection,
                transaction,
                decision.ArtifactIdentity,
                cancellationToken);
            if (experimentGrant is null || suspended)
            {
                throw new InvalidOperationException(
                    "Paper-shadow authorization requires an active, unsuspended paper-experiment grant for the exact strategy identity.");
            }

            return;
        }

        if (decision.AuthorizedAuthorization != StrategyExecutionAuthorization.Validated)
        {
            throw new InvalidOperationException(
                "Accepted promotion decisions must authorize paper shadow or validated execution.");
        }

        var activeExperiment = await ReadAuthorizationHeadAsync(
            connection,
            transaction,
            decision.ArtifactIdentity,
            StrategyExecutionAuthorization.PaperExperiment,
            cancellationToken);
        var activeShadow = await ReadAuthorizationHeadAsync(
            connection,
            transaction,
            decision.ArtifactIdentity,
            StrategyExecutionAuthorization.PaperShadow,
            cancellationToken);
        if (activeExperiment is null || activeShadow is null || await IsIdentitySuspendedAsync(
                connection,
                transaction,
                decision.ArtifactIdentity,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "Validated authorization requires active paper-experiment and paper-shadow authorizations, " +
                "with no suspension, for the exact strategy identity.");
        }
    }

    private static async Task<bool> IsIdentitySuspendedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyArtifactIdentity identity,
        CancellationToken cancellationToken) =>
        await ReadScalarAsync(
            connection,
            transaction,
            """
            SELECT suspension_decision_id
            FROM strategy_authorization_suspensions
            WHERE strategy_id = $strategy
              AND semantic_version = $semanticVersion
              AND strategy_content_sha256 = $contentSha256
            LIMIT 1;
            """,
            cancellationToken,
            ("$strategy", identity.StrategyId),
            ("$semanticVersion", identity.SemanticVersion),
            ("$contentSha256", identity.ContentSha256)) is not null;

    private static async Task ValidateTargetTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StrategyPromotionDecision decision,
        StrategyPromotionDecision target,
        CancellationToken cancellationToken)
    {
        if (!target.StrategyId.Equals(decision.StrategyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle transitions must target a decision for the same strategy family.");
        }

        if (decision.Status == StrategyPromotionDecisionStatus.Resumed)
        {
            if (target.Status != StrategyPromotionDecisionStatus.Suspended ||
                !SameIdentity(target, decision))
            {
                throw new InvalidOperationException(
                    "Resume must target a suspension for the exact strategy identity.");
            }

            var existingResume = await ReadScalarAsync(
                connection,
                transaction,
                """
                SELECT decision_id
                FROM promotion_decisions
                WHERE target_decision_id = $target
                  AND status = $resumed
                LIMIT 1;
                """,
                cancellationToken,
                ("$target", target.DecisionId),
                ("$resumed", (int)StrategyPromotionDecisionStatus.Resumed));
            if (existingResume is not null)
            {
                throw new InvalidOperationException(
                    $"Suspension '{target.DecisionId}' is already resumed.");
            }

            return;
        }

        if (target.Status != StrategyPromotionDecisionStatus.Accepted)
        {
            throw new InvalidOperationException(
                "Revocation, supersession, and suspension must target an accepted decision.");
        }

        if (!SameIdentity(target, decision))
        {
            throw new InvalidOperationException(
                "Revocation, supersession, and suspension must target the exact strategy identity.");
        }

        var terminal = await ReadScalarAsync(
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
        if (terminal is not null)
        {
            throw new InvalidOperationException(
                $"Accepted decision '{target.DecisionId}' is already inactive.");
        }

        if (decision.Status != StrategyPromotionDecisionStatus.Suspended)
        {
            return;
        }

        var activeSuspension = await ReadScalarAsync(
            connection,
            transaction,
            """
            SELECT suspension.decision_id
            FROM promotion_decisions AS suspension
            WHERE suspension.target_decision_id = $target
              AND suspension.status = $suspended
              AND NOT EXISTS (
                  SELECT 1
                  FROM promotion_decisions AS resume
                  WHERE resume.target_decision_id = suspension.decision_id
                    AND resume.status = $resumed
              )
            LIMIT 1;
            """,
            cancellationToken,
            ("$target", target.DecisionId),
            ("$suspended", (int)StrategyPromotionDecisionStatus.Suspended),
            ("$resumed", (int)StrategyPromotionDecisionStatus.Resumed));
        if (activeSuspension is not null)
        {
            throw new InvalidOperationException(
                $"Accepted decision '{target.DecisionId}' is already suspended.");
        }
    }

    private static bool SameIdentity(
        StrategyPromotionDecision left,
        StrategyPromotionDecision right) =>
        left.StrategyId.Equals(right.StrategyId, StringComparison.Ordinal) &&
        left.SemanticVersion.Equals(right.SemanticVersion, StringComparison.Ordinal) &&
        left.StrategyConfigHash.Equals(right.StrategyConfigHash, StringComparison.Ordinal);

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
                        out var parsed))
                {
                    throw new InvalidDataException(
                        $"Strategy promotion registry schema '{version ?? "missing"}' is unsupported.");
                }

                if (parsed == 1)
                {
                    await MigrateVersionOneAsync(connection, cancellationToken);
                    parsed = SchemaVersion;
                }

                if (parsed != SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Strategy promotion registry schema '{parsed}' is unsupported.");
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

    private static async Task MigrateVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var originalCount = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT CAST(COUNT(*) AS TEXT) FROM promotion_decisions;",
            cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            DROP INDEX IF EXISTS ix_promotion_decisions_strategy_time;
            DROP INDEX IF EXISTS ix_promotion_decisions_target;
            ALTER TABLE promotion_decisions RENAME TO legacy_promotion_decisions_v1;

            CREATE TABLE promotion_decisions (
                decision_id TEXT NOT NULL PRIMARY KEY,
                status INTEGER NOT NULL,
                strategy_id TEXT NOT NULL,
                semantic_version TEXT NOT NULL,
                strategy_content_sha256 TEXT NULL,
                authorized_lifecycle INTEGER NULL,
                identity_complete INTEGER NOT NULL CHECK(identity_complete IN (0, 1)),
                target_decision_id TEXT NULL,
                decided_at_utc TEXT NOT NULL,
                artifact_namespace TEXT NOT NULL,
                artifact_sha256 TEXT NOT NULL,
                artifact_byte_length INTEGER NOT NULL,
                canonical_json TEXT NOT NULL,
                FOREIGN KEY(target_decision_id) REFERENCES promotion_decisions(decision_id)
            );

            INSERT INTO promotion_decisions(
                decision_id, status, strategy_id, semantic_version,
                strategy_content_sha256, authorized_lifecycle, identity_complete,
                target_decision_id, decided_at_utc, artifact_namespace,
                artifact_sha256, artifact_byte_length, canonical_json)
            SELECT decision_id, status, strategy_id, '0.0.0-legacy',
                   NULL, NULL, 0, target_decision_id, decided_at_utc,
                   artifact_namespace, artifact_sha256, artifact_byte_length,
                   canonical_json
            FROM legacy_promotion_decisions_v1;

            CREATE INDEX ix_promotion_decisions_strategy_time
                ON promotion_decisions(strategy_id, semantic_version, decided_at_utc, decision_id);
            CREATE INDEX ix_promotion_decisions_identity
                ON promotion_decisions(strategy_id, semantic_version, strategy_content_sha256);
            CREATE INDEX ix_promotion_decisions_target
                ON promotion_decisions(target_decision_id, status);

            CREATE TABLE strategy_authorization_decisions (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                decision_id TEXT NOT NULL UNIQUE,
                status INTEGER NOT NULL,
                strategy_id TEXT NOT NULL,
                semantic_version TEXT NOT NULL,
                strategy_content_sha256 TEXT NOT NULL,
                authorization INTEGER NOT NULL,
                target_decision_id TEXT NULL,
                actor TEXT NOT NULL,
                decided_at_utc TEXT NOT NULL,
                reason TEXT NOT NULL,
                canonical_json TEXT NOT NULL,
                FOREIGN KEY(target_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
            );
            CREATE INDEX ix_strategy_authorization_decisions_identity
                ON strategy_authorization_decisions(
                    strategy_id, semantic_version, strategy_content_sha256, authorization, sequence);

            CREATE TABLE strategy_authorization_heads (
                strategy_id TEXT NOT NULL,
                semantic_version TEXT NOT NULL,
                strategy_content_sha256 TEXT NOT NULL,
                authorization INTEGER NOT NULL,
                grant_decision_id TEXT NOT NULL UNIQUE,
                PRIMARY KEY(strategy_id, semantic_version, strategy_content_sha256, authorization),
                FOREIGN KEY(grant_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
            );

            CREATE TABLE strategy_authorization_suspensions (
                strategy_id TEXT NOT NULL,
                semantic_version TEXT NOT NULL,
                strategy_content_sha256 TEXT NOT NULL,
                suspension_decision_id TEXT NOT NULL UNIQUE,
                PRIMARY KEY(strategy_id, semantic_version, strategy_content_sha256),
                FOREIGN KEY(suspension_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
            );

            CREATE TABLE strategy_entry_admissions (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                intent_id TEXT NOT NULL UNIQUE,
                strategy_id TEXT NOT NULL,
                semantic_version TEXT NOT NULL,
                strategy_content_sha256 TEXT NOT NULL,
                selection_mode INTEGER NOT NULL,
                grant_decision_id TEXT NOT NULL,
                admitted_at_utc TEXT NOT NULL,
                FOREIGN KEY(grant_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
            );
            UPDATE promotion_registry_metadata SET schema_version = 3 WHERE singleton = 1;
            """,
            cancellationToken);

        var migratedCount = await ReadScalarAsync(
            connection,
            transaction,
            "SELECT CAST(COUNT(*) AS TEXT) FROM promotion_decisions WHERE identity_complete = 0;",
            cancellationToken);
        if (!String.Equals(originalCount, migratedCount, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Strategy promotion registry v1 migration did not preserve every decision row.");
        }

        var integrity = await ReadScalarAsync(
            connection,
            transaction,
            "PRAGMA quick_check;",
            cancellationToken);
        if (!String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Strategy promotion registry failed integrity verification after v1 migration: {integrity ?? "missing"}.");
        }

        transaction.Commit();
    }

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
            semantic_version TEXT NOT NULL,
            strategy_content_sha256 TEXT NULL,
            authorized_lifecycle INTEGER NULL,
            identity_complete INTEGER NOT NULL CHECK(identity_complete IN (0, 1)),
            target_decision_id TEXT NULL,
            decided_at_utc TEXT NOT NULL,
            artifact_namespace TEXT NOT NULL,
            artifact_sha256 TEXT NOT NULL,
            artifact_byte_length INTEGER NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(target_decision_id) REFERENCES promotion_decisions(decision_id)
        );
        CREATE INDEX ix_promotion_decisions_strategy_time
            ON promotion_decisions(strategy_id, semantic_version, decided_at_utc, decision_id);
        CREATE INDEX ix_promotion_decisions_identity
            ON promotion_decisions(strategy_id, semantic_version, strategy_content_sha256);
        CREATE INDEX ix_promotion_decisions_target
            ON promotion_decisions(target_decision_id, status);

        CREATE TABLE strategy_authorization_decisions (
            sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            decision_id TEXT NOT NULL UNIQUE,
            status INTEGER NOT NULL,
            strategy_id TEXT NOT NULL,
            semantic_version TEXT NOT NULL,
            strategy_content_sha256 TEXT NOT NULL,
            authorization INTEGER NOT NULL,
            target_decision_id TEXT NULL,
            actor TEXT NOT NULL,
            decided_at_utc TEXT NOT NULL,
            reason TEXT NOT NULL,
            canonical_json TEXT NOT NULL,
            FOREIGN KEY(target_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
        );
        CREATE INDEX ix_strategy_authorization_decisions_identity
            ON strategy_authorization_decisions(
                strategy_id, semantic_version, strategy_content_sha256, authorization, sequence);

        CREATE TABLE strategy_authorization_heads (
            strategy_id TEXT NOT NULL,
            semantic_version TEXT NOT NULL,
            strategy_content_sha256 TEXT NOT NULL,
            authorization INTEGER NOT NULL,
            grant_decision_id TEXT NOT NULL UNIQUE,
            PRIMARY KEY(strategy_id, semantic_version, strategy_content_sha256, authorization),
            FOREIGN KEY(grant_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
        );

        CREATE TABLE strategy_authorization_suspensions (
            strategy_id TEXT NOT NULL,
            semantic_version TEXT NOT NULL,
            strategy_content_sha256 TEXT NOT NULL,
            suspension_decision_id TEXT NOT NULL UNIQUE,
            PRIMARY KEY(strategy_id, semantic_version, strategy_content_sha256),
            FOREIGN KEY(suspension_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
        );

        CREATE TABLE strategy_entry_admissions (
            sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            intent_id TEXT NOT NULL UNIQUE,
            strategy_id TEXT NOT NULL,
            semantic_version TEXT NOT NULL,
            strategy_content_sha256 TEXT NOT NULL,
            selection_mode INTEGER NOT NULL,
            grant_decision_id TEXT NOT NULL,
            admitted_at_utc TEXT NOT NULL,
            FOREIGN KEY(grant_decision_id) REFERENCES strategy_authorization_decisions(decision_id)
        );
        """;

    private sealed record ValidatedPromotionEvidence(
        IReadOnlyList<EvidenceArtifactReference> ReferencedArtifacts);

    private sealed record PreparedPromotionDecision(
        StrategyPromotionDecision Decision,
        EvidenceArtifactReference Artifact,
        string CanonicalJson);

    private sealed record ExperimentAuthorizationDecisionRecord(
        string DecisionId,
        StrategyPromotionDecisionStatus Status,
        StrategyArtifactIdentity Identity,
        string? TargetDecisionId,
        string Actor,
        DateTimeOffset DecidedAtUtc,
        string Reason);

    private sealed record PromotionAuthorizationDecisionRecord(
        string DecisionId,
        StrategyPromotionDecisionStatus Status,
        StrategyArtifactIdentity Identity,
        StrategyExecutionAuthorization Authorization,
        string? TargetDecisionId,
        string Actor,
        DateTimeOffset DecidedAtUtc,
        string Reason);
}
