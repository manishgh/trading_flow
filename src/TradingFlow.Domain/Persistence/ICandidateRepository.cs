using TradingFlow.Domain.Strategies;

namespace TradingFlow.Domain.Persistence;

public interface ICandidateRepository
{
    Task<CandidateRecord> UpsertDiscoveryAsync(
        ProductionRun run,
        CandidateRecord candidate,
        CancellationToken cancellationToken = default);

    Task<CandidateRecord> ApplyDecisionAsync(
        ProductionRun run,
        Guid candidateId,
        int expectedVersion,
        string semanticDecisionSha256,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<CandidateTransitionAppendRequest> transitions,
        CancellationToken cancellationToken = default);

    Task<CandidateRecord?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default);
}

public sealed record CandidateTransitionAppendRequest(
    StrategyCandidateState PreviousState,
    StrategyCandidateState NewState,
    DateTimeOffset OccurredAtUtc,
    string ReasonCode,
    string Source,
    string EvidenceJson);
