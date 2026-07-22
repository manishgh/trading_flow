namespace TradingFlow.Domain.Persistence;

public interface ICandidateRepository
{
    Task<CandidateRecord> UpsertValidatedAsync(
        ProductionRun run,
        CandidateRecord candidate,
        CancellationToken cancellationToken = default);

    Task<CandidateRecord?> GetAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default);
}
