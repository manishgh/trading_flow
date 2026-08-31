using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Commits an order intent as one append-only SQLite transaction.
/// </summary>
public sealed class SqliteOrderIntentRepository : IOrderIntentRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim reservationLock = new(1, 1);

    public SqliteOrderIntentRepository(
        IDbContextFactory<TradingFlowDbContext> contextFactory,
        TimeProvider? timeProvider = null)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OrderIntentRecord?> GetByIntentIdAsync(
        Guid intentId,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty)
        {
            throw new ArgumentException("Intent ID is required.", nameof(intentId));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.OrderIntents
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.IntentId == intentId, cancellationToken);
    }

    public async Task<OrderIntentRecord?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalized = !String.IsNullOrWhiteSpace(clientOrderId)
            ? clientOrderId.Trim()
            : throw new ArgumentException("Client order ID is required.", nameof(clientOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.OrderIntents
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.ClientOrderId == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Order-intent symbol is required.", nameof(symbol));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var rows = await (
                from intent in context.OrderIntents.AsNoTracking()
                join orderEvent in context.OrderEvents.AsNoTracking()
                    on intent.ClientOrderId equals orderEvent.ClientOrderId
                where intent.Symbol == normalized &&
                      intent.StrategyId != "BACKSTOP" &&
                      latestEventIds.Contains(orderEvent.EventId)
                select new
                {
                    intent.ClientOrderId,
                    intent.StrategyId,
                    intent.Symbol,
                    intent.Side,
                    orderEvent.NewState
                })
            .ToArrayAsync(cancellationToken);

        return rows
            .Select(row => new ActiveOrderIntent(
                row.ClientOrderId,
                row.StrategyId,
                row.Symbol,
                row.Side,
                OrderStateMachine.ParseStorageValue(row.NewState)))
            .Where(intent => !OrderStateMachine.IsTerminal(intent.State))
            .OrderBy(intent => intent.ClientOrderId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<OrderIntentReservationResult> ReserveAsync(
        ProductionRun run,
        OrderIntentReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateReservation(run, reservation);

        await reservationLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            // Sequence allocation and candidate consumption must serialize across
            // processes, not merely across callers sharing this repository instance.
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            context.Database.UseTransaction(transaction);
            var serverNowUtc = timeProvider.GetUtcNow().ToUniversalTime();

            var existingRun = await context.ProductionRuns
                .SingleOrDefaultAsync(record => record.RunId == run.RunId, cancellationToken);
            var existingIntent = await context.OrderIntents
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.IntentId == reservation.IntentId, cancellationToken);
            if (existingIntent is not null)
            {
                var owningRun = existingIntent.RunId == run.RunId
                    ? existingRun
                    : await context.ProductionRuns.SingleOrDefaultAsync(
                        record => record.RunId == existingIntent.RunId,
                        cancellationToken);
                if (owningRun is null)
                {
                    throw new InvalidOperationException(
                        $"Intent {reservation.IntentId:N} has no owning production run.");
                }

                if (existingIntent.Kind == OrderIntentKind.ProtectiveStop)
                {
                    EnsureSameProtectiveIntent(existingIntent, reservation);
                }
                else
                {
                    EnsureSameRun(owningRun, run);
                    EnsureSameLogicalIntent(existingIntent, run, reservation);
                }
                await EnsurePersistedCandidateConsumptionAsync(
                    context,
                    existingIntent,
                    cancellationToken);
                return new OrderIntentReservationResult(
                    existingIntent,
                    Created: false,
                    owningRun.Status);
            }

            if (existingRun is null)
            {
                context.ProductionRuns.Add(run);
            }
            else
            {
                EnsureRunCanSubmit(existingRun, run);
            }

            var previousSequence = await context.OrderIntents
                .Where(record =>
                    record.StrategyId == reservation.StrategyId &&
                    record.Side == reservation.Side &&
                    record.Symbol == reservation.Symbol &&
                    record.SessionDate == reservation.SessionDate)
                .MaxAsync(record => (int?)record.SequenceNumber, cancellationToken) ?? 0;
            var sequenceNumber = checked(previousSequence + 1);
            var clientOrderId = TradingFlow.Domain.Orders.ClientOrderIdFactory.Create(
                reservation.StrategyId,
                reservation.Side,
                reservation.Symbol,
                reservation.SessionDate,
                sequenceNumber,
                reservation.IntentId);

            int? triggeredVersion = null;
            int? consumedVersion = null;
            string? candidateSemanticDecisionSha256 = null;
            string? triggeredEvidenceSha256 = null;
            string? consumptionEvidenceSha256 = null;
            string? consumptionEvidenceJson = null;
            if (reservation.Kind == OrderIntentKind.StrategyEntry)
            {
                var candidateId = reservation.CandidateId!.Value;
                var candidateIntent = await context.OrderIntents
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        record => record.CandidateId == candidateId,
                        cancellationToken);
                if (candidateIntent is not null)
                {
                    throw new CandidateOrderIntentConflictException(
                        candidateId,
                        reservation.IntentId,
                        candidateIntent.IntentId);
                }

                var candidate = await context.Candidates
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        record => record.CandidateId == candidateId,
                        cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Candidate {candidateId:N} was not persisted before order reservation.");
                var trigger = await context.CandidateTransitions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        transition =>
                            transition.CandidateId == candidateId &&
                            transition.Sequence == reservation.CandidateExpectedVersion &&
                            transition.NewState == StrategyCandidateState.Triggered,
                        cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Candidate {candidateId:N} has no matching persisted Triggered transition.");
                ValidateCandidateSnapshot(run, reservation, candidate, trigger, serverNowUtc);
                if (candidate.ExpiresAtUtc <= serverNowUtc)
                {
                    await ExpireCandidateAsync(
                        context,
                        run,
                        reservation,
                        candidate,
                        trigger,
                        serverNowUtc,
                        cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    throw new ExpiredCandidateOrderIntentException(
                        candidateId,
                        reservation.IntentId,
                        candidate.ExpiresAtUtc);
                }

                triggeredVersion = reservation.CandidateExpectedVersion!.Value;
                consumedVersion = checked(triggeredVersion.Value + 1);
                candidateSemanticDecisionSha256 = reservation.CandidateSemanticDecisionSha256;
                triggeredEvidenceSha256 = ComputeSha256(trigger.EvidenceJson);
                var requestSha256 = ComputeSha256(reservation.RequestJson);
                consumptionEvidenceJson = JsonSerializer.Serialize(new CandidateConsumptionEvidence(
                    SchemaVersion: 1,
                    candidateId,
                    reservation.IntentId,
                    clientOrderId,
                    triggeredVersion.Value,
                    consumedVersion.Value,
                    candidateSemanticDecisionSha256!,
                    triggeredEvidenceSha256,
                    requestSha256,
                    serverNowUtc));
                consumptionEvidenceSha256 = ComputeSha256(consumptionEvidenceJson);

                var affected = await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE candidates
                    SET state = {StrategyCandidateState.Consumed.ToString()},
                        version = {consumedVersion.Value},
                        revalidated_at_utc = {serverNowUtc}
                    WHERE candidate_id = {candidateId}
                      AND run_id = {run.RunId}
                      AND state = {StrategyCandidateState.Triggered.ToString()}
                      AND version = {triggeredVersion.Value}
                      AND symbol = {reservation.Symbol}
                      AND selected_strategy = {reservation.StrategyId}
                      AND semantic_decision_sha256 = {candidateSemanticDecisionSha256}
                      AND expires_at_utc > {serverNowUtc}
                      AND revalidated_at_utc <= {serverNowUtc}
                      AND EXISTS (
                          SELECT 1
                          FROM candidate_transitions AS transition
                          WHERE transition.candidate_id = {candidateId}
                            AND transition.run_id = {run.RunId}
                            AND transition.sequence = {triggeredVersion.Value}
                            AND transition.new_state = {StrategyCandidateState.Triggered.ToString()}
                            AND transition.semantic_decision_sha256 = {candidateSemanticDecisionSha256}
                            AND transition.evidence_json = {trigger.EvidenceJson}
                      );
                    """,
                    cancellationToken);
                if (affected != 1)
                {
                    throw new DbUpdateConcurrencyException(
                        $"Candidate {candidateId:N} changed or expired before intent {reservation.IntentId:N} could consume it.");
                }

                context.CandidateTransitions.Add(new CandidateTransitionRecord
                {
                    CandidateId = candidateId,
                    Sequence = consumedVersion.Value,
                    PreviousState = StrategyCandidateState.Triggered,
                    NewState = StrategyCandidateState.Consumed,
                    OccurredAtUtc = serverNowUtc,
                    ReasonCode = "candidate_consumed_by_order_intent",
                    Source = "order_intent_repository",
                    SemanticDecisionSha256 = candidateSemanticDecisionSha256!,
                    EvidenceJson = consumptionEvidenceJson,
                    RunId = run.RunId,
                    SchemaVersion = run.SchemaVersion,
                    ConfigHash = run.ConfigHash,
                    CodeVersion = run.CodeVersion
                });
            }

            var intent = new OrderIntentRecord
            {
                IntentId = reservation.IntentId,
                Kind = reservation.Kind,
                CandidateId = reservation.CandidateId,
                CandidateTriggeredVersion = triggeredVersion,
                CandidateConsumedVersion = consumedVersion,
                CandidateSemanticDecisionSha256 = candidateSemanticDecisionSha256,
                CandidateTriggeredEvidenceSha256 = triggeredEvidenceSha256,
                CandidateConsumptionEvidenceSha256 = consumptionEvidenceSha256,
                ClientOrderId = clientOrderId,
                StrategyId = reservation.StrategyId,
                Symbol = reservation.Symbol,
                Side = reservation.Side,
                OrderType = reservation.OrderType,
                TimeInForce = reservation.TimeInForce,
                RequestedQuantity = reservation.RequestedQuantity,
                LimitPrice = reservation.LimitPrice,
                StopPrice = reservation.StopPrice,
                SessionDate = reservation.SessionDate,
                SequenceNumber = sequenceNumber,
                CreatedAtUtc = serverNowUtc,
                RequestJson = reservation.RequestJson,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            };

            context.OrderIntents.Add(intent);
            context.OrderEvents.Add(new OrderEventRecord
            {
                ClientOrderId = clientOrderId,
                PreviousState = null,
                NewState = OrderState.Intent.ToStorageValue(),
                Source = "engine",
                LocalTimestampUtc = serverNowUtc,
                PayloadJson = reservation.RequestJson,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OrderIntentReservationResult(intent, Created: true, run.Status);
        }
        catch (DbUpdateException exception) when (
            reservation.CandidateId is { } candidateId &&
            IsCandidateIntentUniqueViolation(exception))
        {
            throw new CandidateOrderIntentConflictException(
                candidateId,
                reservation.IntentId,
                innerException: exception);
        }
        finally
        {
            reservationLock.Release();
        }
    }

    private static void ValidateReservation(ProductionRun run, OrderIntentReservation reservation)
    {
        if (run.RunId == Guid.Empty || reservation.IntentId == Guid.Empty)
        {
            throw new InvalidOperationException("Run ID and intent ID are required.");
        }

        if (!run.Status.Equals("running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A new order-intent request requires a running owner; run {run.RunId:N} supplied state '{run.Status}'.");
        }

        if (!Enum.IsDefined(reservation.Kind) ||
            reservation.RequestedQuantity <= 0m ||
            String.IsNullOrWhiteSpace(reservation.RequestJson) ||
            String.IsNullOrWhiteSpace(reservation.StrategyId) ||
            String.IsNullOrWhiteSpace(reservation.Symbol) ||
            String.IsNullOrWhiteSpace(reservation.Side) ||
            String.IsNullOrWhiteSpace(reservation.OrderType) ||
            String.IsNullOrWhiteSpace(reservation.TimeInForce) ||
            reservation.CreatedAtUtc == default ||
            reservation.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Order intent kind, normalized order fields, quantity, UTC timestamp, and request payload are required.");
        }

        if (run.ConfigHash.Length != 64 || String.IsNullOrWhiteSpace(run.CodeVersion))
        {
            throw new InvalidOperationException("Order intent provenance is incomplete.");
        }

        if (reservation.Kind == OrderIntentKind.StrategyEntry)
        {
            if (reservation.CandidateId is null ||
                reservation.CandidateExpectedVersion is null or < 0 ||
                reservation.CandidateSemanticDecisionSha256 is not { Length: 64 } decisionHash ||
                !decisionHash.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException(
                    "Strategy-entry reservation requires candidate identity, Triggered version, and semantic decision hash.");
            }
        }
        else if (reservation.CandidateId is not null ||
                 reservation.CandidateExpectedVersion is not null ||
                 !String.IsNullOrWhiteSpace(reservation.CandidateSemanticDecisionSha256))
        {
            throw new InvalidOperationException(
                "Only strategy-entry intents may carry candidate authorization fields.");
        }
    }

    private static void ValidateCandidateSnapshot(
        ProductionRun run,
        OrderIntentReservation reservation,
        CandidateRecord candidate,
        CandidateTransitionRecord trigger,
        DateTimeOffset serverNowUtc)
    {
        if (candidate.RunId != run.RunId ||
            candidate.State != StrategyCandidateState.Triggered ||
            candidate.Version != reservation.CandidateExpectedVersion ||
            !candidate.SemanticDecisionSha256.Equals(
                reservation.CandidateSemanticDecisionSha256,
                StringComparison.Ordinal) ||
            !candidate.Symbol.Equals(reservation.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(candidate.SelectedStrategy, reservation.StrategyId, StringComparison.Ordinal) ||
            candidate.RevalidatedAtUtc > serverNowUtc ||
            trigger.RunId != run.RunId ||
            trigger.Sequence != candidate.Version ||
            trigger.NewState != StrategyCandidateState.Triggered ||
            !trigger.SemanticDecisionSha256.Equals(candidate.SemanticDecisionSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Candidate {candidate.CandidateId:N} is not the current Triggered setup authorized for this order intent.");
        }

        StrategyCandidateStateMachine.RequireTransition(
            StrategyCandidateState.Triggered,
            StrategyCandidateState.Consumed);
    }

    private static async Task ExpireCandidateAsync(
        TradingFlowDbContext context,
        ProductionRun run,
        OrderIntentReservation reservation,
        CandidateRecord candidate,
        CandidateTransitionRecord trigger,
        DateTimeOffset serverNowUtc,
        CancellationToken cancellationToken)
    {
        StrategyCandidateStateMachine.RequireTransition(
            StrategyCandidateState.Triggered,
            StrategyCandidateState.Expired);
        var expiredVersion = checked(candidate.Version + 1);
        var affected = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE candidates
            SET state = {StrategyCandidateState.Expired.ToString()},
                version = {expiredVersion},
                revalidated_at_utc = {serverNowUtc}
            WHERE candidate_id = {candidate.CandidateId}
              AND run_id = {run.RunId}
              AND state = {StrategyCandidateState.Triggered.ToString()}
              AND version = {candidate.Version}
              AND symbol = {reservation.Symbol}
              AND selected_strategy = {reservation.StrategyId}
              AND semantic_decision_sha256 = {candidate.SemanticDecisionSha256}
              AND expires_at_utc <= {serverNowUtc}
              AND EXISTS (
                  SELECT 1
                  FROM candidate_transitions AS transition
                  WHERE transition.candidate_id = {candidate.CandidateId}
                    AND transition.run_id = {run.RunId}
                    AND transition.sequence = {candidate.Version}
                    AND transition.new_state = {StrategyCandidateState.Triggered.ToString()}
                    AND transition.semantic_decision_sha256 = {candidate.SemanticDecisionSha256}
                    AND transition.evidence_json = {trigger.EvidenceJson}
              );
            """,
            cancellationToken);
        if (affected != 1)
        {
            throw new DbUpdateConcurrencyException(
                $"Candidate {candidate.CandidateId:N} changed before its expiry could be committed.");
        }

        context.CandidateTransitions.Add(new CandidateTransitionRecord
        {
            CandidateId = candidate.CandidateId,
            Sequence = expiredVersion,
            PreviousState = StrategyCandidateState.Triggered,
            NewState = StrategyCandidateState.Expired,
            OccurredAtUtc = serverNowUtc,
            ReasonCode = "candidate_expired_before_order_intent",
            Source = "order_intent_repository",
            SemanticDecisionSha256 = candidate.SemanticDecisionSha256,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                reservation.IntentId,
                expectedVersion = candidate.Version,
                candidate.ExpiresAtUtc,
                observedAtUtc = serverNowUtc
            }),
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
    }

    private static async Task EnsurePersistedCandidateConsumptionAsync(
        TradingFlowDbContext context,
        OrderIntentRecord intent,
        CancellationToken cancellationToken)
    {
        if (intent.Kind != OrderIntentKind.StrategyEntry)
        {
            return;
        }

        if (intent.CandidateId is not { } candidateId ||
            intent.CandidateTriggeredVersion is not { } triggeredVersion ||
            intent.CandidateConsumedVersion is not { } consumedVersion ||
            String.IsNullOrWhiteSpace(intent.CandidateSemanticDecisionSha256) ||
            String.IsNullOrWhiteSpace(intent.CandidateTriggeredEvidenceSha256) ||
            String.IsNullOrWhiteSpace(intent.CandidateConsumptionEvidenceSha256))
        {
            throw new InvalidOperationException(
                $"Strategy intent {intent.IntentId:N} has incomplete candidate-consumption evidence.");
        }

        var candidate = await context.Candidates.AsNoTracking().SingleOrDefaultAsync(
            record => record.CandidateId == candidateId,
            cancellationToken);
        var transition = await context.CandidateTransitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.CandidateId == candidateId && record.Sequence == consumedVersion,
            cancellationToken);
        var triggeredTransition = await context.CandidateTransitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.CandidateId == candidateId && record.Sequence == triggeredVersion,
            cancellationToken);
        if (candidate is null ||
            transition is null ||
            triggeredTransition is null ||
            candidate.State != StrategyCandidateState.Consumed ||
            candidate.Version != consumedVersion ||
            consumedVersion != triggeredVersion + 1 ||
            !candidate.SemanticDecisionSha256.Equals(intent.CandidateSemanticDecisionSha256, StringComparison.Ordinal) ||
            transition.PreviousState != StrategyCandidateState.Triggered ||
            transition.NewState != StrategyCandidateState.Consumed ||
            triggeredTransition.NewState != StrategyCandidateState.Triggered ||
            !ComputeSha256(triggeredTransition.EvidenceJson).Equals(
                intent.CandidateTriggeredEvidenceSha256,
                StringComparison.Ordinal) ||
            !transition.SemanticDecisionSha256.Equals(intent.CandidateSemanticDecisionSha256, StringComparison.Ordinal) ||
            !ComputeSha256(transition.EvidenceJson).Equals(
                intent.CandidateConsumptionEvidenceSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Strategy intent {intent.IntentId:N} does not have a valid persisted candidate consumption.");
        }

        CandidateConsumptionEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<CandidateConsumptionEvidence>(transition.EvidenceJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Strategy intent {intent.IntentId:N} has malformed candidate consumption evidence.",
                exception);
        }

        if (evidence is null ||
            evidence.CandidateId != candidateId ||
            evidence.IntentId != intent.IntentId ||
            !evidence.ClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) ||
            evidence.TriggeredVersion != triggeredVersion ||
            evidence.ConsumedVersion != consumedVersion ||
            !evidence.SemanticDecisionSha256.Equals(
                intent.CandidateSemanticDecisionSha256,
                StringComparison.Ordinal) ||
            !evidence.TriggeredEvidenceSha256.Equals(
                intent.CandidateTriggeredEvidenceSha256,
                StringComparison.Ordinal) ||
            !evidence.RequestSha256.Equals(ComputeSha256(intent.RequestJson), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Strategy intent {intent.IntentId:N} candidate consumption evidence does not match the intent.");
        }
    }

    private static bool IsCandidateIntentUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19 &&
                sqlite.Message.Contains("order_intents.candidate_id", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void EnsureSameRun(ProductionRun existing, ProductionRun requested)
    {
        if (existing.SchemaVersion != requested.SchemaVersion ||
            !existing.ConfigHash.Equals(requested.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(requested.CodeVersion, StringComparison.Ordinal) ||
            !existing.Profile.Equals(requested.Profile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run {requested.RunId:N} already exists with different provenance.");
        }
    }

    private static void EnsureRunCanSubmit(ProductionRun existing, ProductionRun requested)
    {
        EnsureSameRun(existing, requested);
        if (!existing.Status.Equals("running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot submit an order intent for run {requested.RunId:N} in state '{existing.Status}'.");
        }
    }

    private static void EnsureSameLogicalIntent(
        OrderIntentRecord existing,
        ProductionRun run,
        OrderIntentReservation requested)
    {
        if (existing.RunId != run.RunId ||
            existing.SchemaVersion != run.SchemaVersion ||
            !existing.ConfigHash.Equals(run.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(run.CodeVersion, StringComparison.Ordinal) ||
            existing.Kind != requested.Kind ||
            existing.CandidateId != requested.CandidateId ||
            existing.CandidateTriggeredVersion != requested.CandidateExpectedVersion ||
            !String.Equals(
                existing.CandidateSemanticDecisionSha256,
                requested.CandidateSemanticDecisionSha256,
                StringComparison.Ordinal) ||
            !existing.StrategyId.Equals(requested.StrategyId, StringComparison.Ordinal) ||
            !existing.Symbol.Equals(requested.Symbol, StringComparison.Ordinal) ||
            !existing.Side.Equals(requested.Side, StringComparison.Ordinal) ||
            !existing.OrderType.Equals(requested.OrderType, StringComparison.Ordinal) ||
            !existing.TimeInForce.Equals(requested.TimeInForce, StringComparison.Ordinal) ||
            existing.RequestedQuantity != requested.RequestedQuantity ||
            existing.LimitPrice != requested.LimitPrice ||
            existing.StopPrice != requested.StopPrice ||
            existing.SessionDate != requested.SessionDate ||
            !existing.RequestJson.Equals(requested.RequestJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Intent {requested.IntentId:N} was retried with a different order request.");
        }
    }

    private static void EnsureSameProtectiveIntent(
        OrderIntentRecord existing,
        OrderIntentReservation requested)
    {
        if (requested.Kind != OrderIntentKind.ProtectiveStop ||
            requested.CandidateId is not null ||
            existing.CandidateId is not null ||
            !existing.StrategyId.Equals(requested.StrategyId, StringComparison.Ordinal) ||
            !existing.Symbol.Equals(requested.Symbol, StringComparison.Ordinal) ||
            !existing.Side.Equals(requested.Side, StringComparison.Ordinal) ||
            !existing.OrderType.Equals(requested.OrderType, StringComparison.Ordinal) ||
            !existing.TimeInForce.Equals(requested.TimeInForce, StringComparison.Ordinal) ||
            existing.RequestedQuantity != requested.RequestedQuantity ||
            existing.LimitPrice != requested.LimitPrice ||
            existing.StopPrice != requested.StopPrice ||
            existing.SessionDate != requested.SessionDate ||
            !existing.RequestJson.Equals(requested.RequestJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Protective intent {requested.IntentId:N} was retried for different position coverage.");
        }
    }

    private sealed record CandidateConsumptionEvidence(
        int SchemaVersion,
        Guid CandidateId,
        Guid IntentId,
        string ClientOrderId,
        int TriggeredVersion,
        int ConsumedVersion,
        string SemanticDecisionSha256,
        string TriggeredEvidenceSha256,
        string RequestSha256,
        DateTimeOffset ConsumedAtUtc);
}
