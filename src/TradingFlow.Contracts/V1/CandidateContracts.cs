namespace TradingFlow.Contracts.V1;

public sealed record CreateCandidateRunRequest(
    string ContractVersion,
    string IdempotencyKey,
    string Mode,
    Guid UniverseSnapshotId,
    IReadOnlyList<StrategyReference> Strategies,
    DateTimeOffset AsOfUtc);

public sealed record CandidateRunResponse(
    string ContractVersion,
    Guid CandidateRunId,
    RunProvenance Provenance,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long LatestEventSequence,
    IReadOnlyList<CandidateStateResponse> Candidates);

public sealed record CandidateStateResponse(
    Guid CandidateId,
    int Version,
    string Symbol,
    StrategyReference Strategy,
    string State,
    string Readiness,
    string? Trigger,
    DateTimeOffset DiscoveredAtUtc,
    DateTimeOffset RevalidatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LatestCompletedCandleAtUtc,
    decimal? SameTimeRelativeVolume,
    int? RelativeVolumeSampleCount,
    IReadOnlyList<CandidateRejectionResponse> Rejections,
    IReadOnlyList<UniverseSourceEvidenceResponse>? Sources = null);

public sealed record CandidateRejectionResponse(
    string Code,
    string Message,
    DateTimeOffset RejectedAtUtc,
    IReadOnlyDictionary<string, string?> Evidence);

public sealed record CandidateTransitionResponse(
    int Sequence,
    string PreviousState,
    string NewState,
    DateTimeOffset OccurredAtUtc,
    string ReasonCode,
    string Source,
    string SemanticDecisionSha256);
