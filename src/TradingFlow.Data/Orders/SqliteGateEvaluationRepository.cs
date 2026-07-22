using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Atomically persists the ordered entry-gate prefix before broker I/O.
/// </summary>
public sealed class SqliteGateEvaluationRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IGateEvaluationRepository
{
    public async Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
        ProductionRun run,
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evaluations);
        Validate(evaluations);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var records = evaluations.Select(evaluation => new GateEvaluationRecord
        {
            CandidateId = evaluation.CandidateId,
            ClientOrderId = evaluation.ClientOrderId,
            GateOrder = evaluation.GateOrder,
            GateName = evaluation.GateName,
            Passed = evaluation.Passed,
            RejectCode = evaluation.RejectCode,
            EvaluatedAtUtc = evaluation.EvaluatedAtUtc.ToUniversalTime(),
            InputsJson = evaluation.InputsJson,
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        }).ToArray();
        context.GateEvaluations.AddRange(records);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    private static void Validate(IReadOnlyList<GateEvaluationAppendRequest> evaluations)
    {
        if (evaluations.Count == 0)
        {
            throw new InvalidOperationException("At least one entry-gate evaluation is required.");
        }

        var candidateId = evaluations[0].CandidateId;
        for (var index = 0; index < evaluations.Count; index++)
        {
            var evaluation = evaluations[index];
            if (evaluation.CandidateId == Guid.Empty || evaluation.CandidateId != candidateId)
            {
                throw new InvalidOperationException("Gate evaluations require one non-empty candidate ID.");
            }

            if (evaluation.GateOrder != index + 1 || String.IsNullOrWhiteSpace(evaluation.GateName))
            {
                throw new InvalidOperationException("Gate evaluations must be a contiguous ordered prefix.");
            }

            if (evaluation.Passed == (evaluation.RejectCode is not null) ||
                String.IsNullOrWhiteSpace(evaluation.InputsJson))
            {
                throw new InvalidOperationException(
                    "Passing gates cannot have reject codes; failed gates require one and all gates require inputs.");
            }
        }
    }
}
