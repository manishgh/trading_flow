using System.Text.Json.Serialization;

namespace TradingFlow.Contracts.V1;

public static class PaperMembershipModes
{
    public const string FixedAtLaunch = "fixed_at_launch";
    public const string RefreshSelectedSources = "refresh_selected_sources";
}

public sealed record UniversePreviewRequest(
    string ContractVersion,
    string Horizon,
    DateTimeOffset? AsOfUtc,
    IReadOnlyList<UniverseSourceSelectionRequest> Sources,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PaperMembershipMode = null);

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
    IReadOnlyList<UniverseExclusionResponse> Exclusions,
    DateTimeOffset DecisionCutoffUtc = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PaperMembershipReview? PaperMembership = null);

/// <summary>Retains every server-resolved selection, including sources with no observed stocks.</summary>
public sealed record PaperMembershipReview(string Mode, IReadOnlyList<PaperMembershipSource> Sources);

public sealed record PaperMembershipSource(
    Guid DiscoverySnapshotId,
    string SourceKind,
    string SourceKey,
    Guid? WishlistId,
    string? NormalizedQuery,
    string? QuerySha256,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int MemberCount,
    string? ProviderReference,
    bool IsBoundedObservation);

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
