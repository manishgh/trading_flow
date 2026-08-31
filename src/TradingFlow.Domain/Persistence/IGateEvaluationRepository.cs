using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Strategies;

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

/// <summary>
/// Binds a rejected gate prefix to the exact Triggered candidate snapshot that
/// may be terminally blocked. Mismatched or missing candidates are audited but
/// never mutated.
/// </summary>
public sealed record CandidateGateRejection(
    Guid CandidateId,
    int ExpectedVersion,
    string SemanticDecisionSha256,
    string Symbol,
    string StrategyId,
    string FailedGateName,
    RejectCode RejectCode);

public interface IGateEvaluationRepository
{
    Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
        ProductionRun run,
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        CandidateGateRejection? candidateRejection = null,
        CancellationToken cancellationToken = default);
}
