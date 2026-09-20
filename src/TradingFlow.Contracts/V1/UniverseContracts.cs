namespace TradingFlow.Contracts.V1;

public sealed record UniversePreviewRequest(
    string ContractVersion,
    string Horizon,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<UniverseSourceSelectionRequest> Sources);

public sealed record UniverseSourceSelectionRequest(
    string SourceKind,
    string SourceKey,
    Guid? WishlistId = null,
    string? Query = null);

public sealed record UniversePreviewResponse(
    string ContractVersion,
    Guid UniverseSnapshotId,
    string Horizon,
    DateTimeOffset ResolvedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ContentSha256,
    IReadOnlyList<UniverseMemberResponse> Members,
    IReadOnlyList<UniverseExclusionResponse> Exclusions);

public sealed record UniverseMemberResponse(
    string Symbol,
    string Readiness,
    IReadOnlyList<UniverseSourceEvidenceResponse> Sources);

public sealed record UniverseSourceEvidenceResponse(
    string SourceKind,
    string SourceKey,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ContentSha256,
    bool IsDiagnostic,
    string? ProviderReference = null);

public sealed record UniverseExclusionResponse(
    string Symbol,
    string ReasonCode,
    string Reason);
