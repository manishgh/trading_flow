using TradingFlow.Domain.Execution;

namespace TradingFlow.Domain.Persistence;

public sealed record GateEvaluationAppendRequest(
    Guid CandidateId,
    string? ClientOrderId,
    int GateOrder,
    string GateName,
    bool Passed,
    RejectCode? RejectCode,
    DateTimeOffset EvaluatedAtUtc,
    string InputsJson);

public interface IGateEvaluationRepository
{
    Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
        ProductionRun run,
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        CancellationToken cancellationToken = default);
}
