namespace TradingFlow.Contracts.V1;

/// <summary>
/// Carries one durable, ordered stream item. EventSequence is the SSE id and the
/// resume cursor supplied by clients after a disconnect.
/// </summary>
public sealed record EventStreamEnvelope<TPayload>(
    string ContractVersion,
    string StreamName,
    long EventSequence,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    TPayload Payload);

public sealed record StreamHeartbeatResponse(
    string ContractVersion,
    string StreamName,
    long LatestEventSequence,
    DateTimeOffset SentAtUtc);

public sealed record CandidateChangedEvent(
    Guid CandidateRunId,
    CandidateStateResponse Candidate);

public sealed record JobProgressEvent(JobProgressResponse Job);
