using TradingFlow.Domain.Research;

namespace TradingFlow.Engine.Research;

public sealed record EvidenceDatasetQuery(
    EvidenceDatasetKind? Kind = null,
    string? DataFeed = null,
    DateTimeOffset? CreatedAtOrAfterUtc = null,
    bool RequirePassedQuality = true);

public sealed record EvidenceCatalogCommitResult(
    string Id,
    bool AlreadyCommitted);

public sealed record EvidencePublicationRecoveryResult(
    int Considered,
    int Recovered,
    int WaitingForContent,
    int Quarantined,
    int Failed);

/// <summary>
/// The only collector-facing boundary for raw provider evidence. It journals the exact
/// observation before publishing bytes and commits discoverability only after verification.
/// </summary>
public interface IEvidencePublicationCoordinator
{
    Task<EvidenceCatalogCommitResult> PublishSourceObservationAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken = default);

    Task<EvidencePublicationRecoveryResult> RecoverPendingPublicationsAsync(
        int maximumPublications,
        CancellationToken cancellationToken = default);
}

public interface IEvidenceCatalog
{
    Task<EvidenceCatalogCommitResult> RegisterCollectionPlanAsync(
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken = default);

    Task<EvidenceCollectionPlan?> GetCollectionPlanAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task SaveCollectionCheckpointAsync(
        EvidenceCollectionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<EvidenceCollectionCheckpoint?> GetCollectionCheckpointAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task SaveRequestCursorCheckpointAsync(
        EvidenceRequestCursorCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<EvidenceRequestCursorCheckpoint?> GetRequestCursorCheckpointAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken = default);

    Task<EvidenceCatalogCommitResult> RegisterSourceObservationAsync(
        EvidenceSourceObservation observation,
        CancellationToken cancellationToken = default);

    Task<EvidenceSourceObservation?> GetSourceObservationAsync(
        string observationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceSourceObservation>> FindSourceObservationsAsync(
        string jobId,
        string requestId,
        CancellationToken cancellationToken = default);

    Task<EvidenceCatalogCommitResult> CommitDatasetAsync(
        EvidenceDatasetManifest manifest,
        CancellationToken cancellationToken = default);

    Task<EvidenceDatasetManifest?> GetDatasetAsync(
        string datasetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceDatasetManifest>> FindDatasetsAsync(
        EvidenceDatasetQuery query,
        CancellationToken cancellationToken = default);

    Task<EvidenceCatalogCommitResult> RegisterResearchRunAsync(
        EvidenceResearchRunManifest manifest,
        CancellationToken cancellationToken = default);

    Task<EvidenceResearchRunManifest?> GetResearchRunAsync(
        string researchRunId,
        CancellationToken cancellationToken = default);

    Task<EvidenceHoldoutConsumption?> GetHoldoutConsumptionAsync(
        string holdoutId,
        CancellationToken cancellationToken = default);

    Task<EvidenceCatalogCommitResult> ReserveHoldoutAsync(
        EvidenceHoldoutConsumption consumption,
        CancellationToken cancellationToken = default);

    Task<EvidenceCatalogCommitResult> RegisterQuarantineAsync(
        EvidenceQuarantineRecord quarantine,
        CancellationToken cancellationToken = default);

    Task<EvidenceQuarantineEntry?> GetQuarantineAsync(
        string quarantineId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceQuarantineEntry>> FindQuarantinesAsync(
        EvidenceQuarantineStatus status,
        CancellationToken cancellationToken = default);

    Task ResolveQuarantineAsync(
        EvidenceQuarantineResolution resolution,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceArtifactReference>> ResolveReferenceClosureAsync(
        EvidencePinSubject subject,
        CancellationToken cancellationToken = default);

    Task<EvidenceRetentionPin> PinAsync(
        EvidencePinSubject subject,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Catalog boundary for a coherent group of normalized datasets that must become visible in
/// one serializable transaction. Immutable objects are published and verified before this
/// operation commits any dataset row.
/// </summary>
public interface IAtomicEvidenceDatasetCatalog
{
    Task<IReadOnlyList<EvidenceCatalogCommitResult>> CommitDatasetsAtomicallyAsync(
        IReadOnlyList<EvidenceDatasetManifest> manifests,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrow operator boundary for retrying normalization after a correctable code defect.
/// Raw collection must already be complete and every open quarantine for the job is
/// resolved atomically with the checkpoint recovery.
/// </summary>
public interface IEvidenceNormalizationRecoveryCatalog
{
    Task RecoverQuarantinedNormalizationAsync(
        string jobId,
        string decidedBy,
        string reason,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized boundary for promotion, audit, and legal-hold reference subjects that are
/// external to the research catalog.
/// </summary>
public interface IEvidenceReferenceSubjectRegistry
{
    Task<EvidenceCatalogCommitResult> RegisterAsync(
        EvidenceReferenceSubjectManifest subject,
        CancellationToken cancellationToken = default);
}

public sealed record EvidenceRetentionRunResult(
    int Considered,
    int Tombstoned,
    int Deleted,
    int Failed);

/// <summary>
/// High-level retention boundary. It deliberately exposes no direct object-delete primitive;
/// implementations must select candidates, verify pin closures, tombstone, and delete atomically
/// with respect to the catalog writer.
/// </summary>
public interface IEvidenceRetentionService
{
    Task<EvidenceRetentionRunResult> RunAsync(
        EvidenceRetentionPolicy policy,
        DateTimeOffset evaluatedAtUtc,
        int maximumObjects,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorization-sensitive boundary. Research assemblies must not receive this interface.
/// </summary>
public interface IPromotionRegistry
{
    Task<EvidenceCatalogCommitResult> RegisterDecisionAsync(
        StrategyPromotionDecision authorizedDecision,
        string actingPrincipal,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrategyPromotionDecision>> GetDecisionHistoryAsync(
        string? strategyId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrategyPromotionDecision>> GetActiveDecisionsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticates the human principal independently from the principal recorded in a decision.
/// </summary>
public interface IPromotionPrincipalAuthorizer
{
    bool IsAuthorizedHumanPrincipal(string principal);
}
