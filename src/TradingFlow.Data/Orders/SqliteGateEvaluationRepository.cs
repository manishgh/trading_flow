using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Atomically persists the ordered entry-gate prefix before broker I/O.
/// </summary>
public sealed class SqliteGateEvaluationRepository : IGateEvaluationRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public SqliteGateEvaluationRepository(
        IDbContextFactory<TradingFlowDbContext> contextFactory,
        TimeProvider? timeProvider = null)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
        ProductionRun run,
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        CandidateGateRejection? candidateRejection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evaluations);
        Validate(evaluations, candidateRejection);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        context.Database.UseTransaction(transaction);
        var serverNowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        var records = evaluations.Select(evaluation => new GateEvaluationRecord
        {
            CandidateId = evaluation.CandidateId,
            ClientOrderId = evaluation.ClientOrderId,
            GateOrder = evaluation.GateOrder,
            GateName = evaluation.GateName,
            Passed = evaluation.Passed,
            RejectCode = evaluation.RejectCode,
            EvaluatedAtUtc = serverNowUtc,
            InputsJson = evaluation.InputsJson,
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        }).ToArray();
        context.GateEvaluations.AddRange(records);
        if (candidateRejection is not null)
        {
            await ApplyTerminalCandidateOutcomeAsync(
                context,
                run,
                candidateRejection,
                evaluations,
                serverNowUtc,
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    private static async Task ApplyTerminalCandidateOutcomeAsync(
        TradingFlowDbContext context,
        ProductionRun run,
        CandidateGateRejection rejection,
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        DateTimeOffset serverNowUtc,
        CancellationToken cancellationToken)
    {
        var candidate = await context.Candidates.AsNoTracking().SingleOrDefaultAsync(
            record => record.CandidateId == rejection.CandidateId,
            cancellationToken);
        if (candidate is null ||
            candidate.RunId != run.RunId ||
            candidate.Version != rejection.ExpectedVersion ||
            candidate.State != StrategyCandidateState.Triggered ||
            !candidate.SemanticDecisionSha256.Equals(
                rejection.SemanticDecisionSha256,
                StringComparison.Ordinal) ||
            !candidate.Symbol.Equals(rejection.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(candidate.SelectedStrategy, rejection.StrategyId, StringComparison.Ordinal))
        {
            return;
        }

        var terminalState = candidate.ExpiresAtUtc <= serverNowUtc
            ? StrategyCandidateState.Expired
            : StrategyCandidateState.RiskBlocked;
        StrategyCandidateStateMachine.RequireTransition(candidate.State, terminalState);
        var terminalVersion = checked(candidate.Version + 1);
        var affected = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE candidates
            SET state = {terminalState.ToString()},
                version = {terminalVersion},
                revalidated_at_utc = {serverNowUtc}
            WHERE candidate_id = {candidate.CandidateId}
              AND run_id = {run.RunId}
              AND state = {StrategyCandidateState.Triggered.ToString()}
              AND version = {candidate.Version}
              AND symbol = {rejection.Symbol}
              AND selected_strategy = {rejection.StrategyId}
              AND semantic_decision_sha256 = {candidate.SemanticDecisionSha256}
              AND EXISTS (
                  SELECT 1
                  FROM candidate_transitions AS transition
                  WHERE transition.candidate_id = {candidate.CandidateId}
                    AND transition.run_id = {run.RunId}
                    AND transition.sequence = {candidate.Version}
                    AND transition.new_state = {StrategyCandidateState.Triggered.ToString()}
                    AND transition.semantic_decision_sha256 = {candidate.SemanticDecisionSha256}
              );
            """,
            cancellationToken);
        if (affected != 1)
        {
            throw new DbUpdateConcurrencyException(
                $"Candidate {candidate.CandidateId:N} changed before its rejected gate outcome could be committed.");
        }

        var failedEvaluation = evaluations[^1];
        context.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            CandidateId = candidate.CandidateId,
            Sequence = terminalVersion,
            PreviousState = StrategyCandidateState.Triggered,
            NewState = terminalState,
            OccurredAtUtc = serverNowUtc,
            ReasonCode = terminalState == StrategyCandidateState.Expired
                ? "candidate_expired_during_entry_gates"
                : "candidate_blocked_by_entry_gate",
            Source = "entry_gate_repository",
            SemanticDecisionSha256 = candidate.SemanticDecisionSha256,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                rejection.FailedGateName,
                rejectCode = rejection.RejectCode.ToString(),
                evaluationCount = evaluations.Count,
                failedEvaluation.InputsJson,
                terminalState = terminalState.ToString(),
                occurredAtUtc = serverNowUtc
            }),
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
    }

    private static void Validate(
        IReadOnlyList<GateEvaluationAppendRequest> evaluations,
        CandidateGateRejection? candidateRejection)
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

        if (candidateRejection is not null)
        {
            var failed = evaluations[^1];
            if (candidateRejection.CandidateId != candidateId ||
                candidateRejection.ExpectedVersion < 0 ||
                candidateRejection.SemanticDecisionSha256 is not { Length: 64 } hash ||
                !hash.All(Uri.IsHexDigit) ||
                String.IsNullOrWhiteSpace(candidateRejection.Symbol) ||
                String.IsNullOrWhiteSpace(candidateRejection.StrategyId) ||
                failed.Passed ||
                failed.RejectCode != candidateRejection.RejectCode ||
                !failed.GateName.Equals(candidateRejection.FailedGateName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Candidate gate rejection must match the failed final evaluation and exact Triggered candidate identity.");
            }
        }
    }
}
