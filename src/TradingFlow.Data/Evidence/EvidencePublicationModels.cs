using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence;

internal enum EvidencePublicationOwnerKind
{
    SourceObservation = 1,
    Dataset = 2,
    ResearchRun = 3
}

internal enum EvidencePublicationState
{
    Prepared = 1,
    ObjectPublished = 2,
    CatalogCommitted = 3,
    Superseded = 4,
    RetryableFailure = 5,
    Quarantined = 6
}

internal enum EvidenceArtifactKind
{
    RawObservation = 1,
    NormalizedPartition = 2,
    DatasetManifest = 3,
    ResearchRunManifest = 4,
    ResearchInput = 5,
    ResearchOutput = 6,
    Diagnostic = 7
}

internal enum EvidenceArtifactLifecycleStatus
{
    Active = 1,
    Tombstoned = 2,
    Deleted = 3,
    Corrupt = 4
}

internal sealed record EvidencePublicationRecord(
    string PublicationId,
    EvidencePublicationOwnerKind OwnerKind,
    string OwnerId,
    EvidencePublicationState State,
    EvidenceArtifactReference Artifact,
    string CanonicalOwnerJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int AttemptCount,
    string? LastError);
