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
public sealed class SqliteOrderIntentRepository : IOrderIntentRepository, IOrderDispatchRepository
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

    public async Task<ProductionRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Run ID is required.", nameof(runId));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ProductionRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.RunId == runId, cancellationToken);
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

    public async Task<IReadOnlyList<OrderIntentRecord>> ListByRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Run ID is required.", nameof(runId));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var intents = await context.OrderIntents
            .AsNoTracking()
            .Where(record => record.RunId == runId)
            .ToArrayAsync(cancellationToken);

        return intents
            .OrderBy(record => record.CreatedAtUtc)
            .ThenBy(record => record.IntentId)
            .ToArray();
    }

    public async Task<PortfolioRiskReservationRecord?> GetRiskReservationByIntentIdAsync(
        Guid intentId,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty)
        {
            throw new ArgumentException("Intent ID is required.", nameof(intentId));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PortfolioRiskReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.IntentId == intentId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListRecoverableIntentIdsAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Dispatch account ID is required.", nameof(accountId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var recoverable = await (
                from intent in context.OrderIntents.AsNoTracking()
                join orderEvent in context.OrderEvents.AsNoTracking()
                    on intent.ClientOrderId equals orderEvent.ClientOrderId
                join run in context.ProductionRuns.AsNoTracking()
                    on intent.RunId equals run.RunId
                where intent.AccountId == normalizedAccountId &&
                      latestEventIds.Contains(orderEvent.EventId) &&
                      (orderEvent.NewState == "SUBMITTED" ||
                       orderEvent.NewState == "INTENT" &&
                       (intent.Kind == OrderIntentKind.ProtectiveStop ||
                        intent.Kind == OrderIntentKind.PositionExit ||
                        run.Status == "running"))
                select new { intent.IntentId, intent.CreatedAtUtc })
            .ToArrayAsync(cancellationToken);
        return recoverable
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.IntentId)
            .Select(item => item.IntentId)
            .ToArray();
    }

    public async Task<IReadOnlyList<OrderIntentRecord>> ListUnpreparedPositionExitsAsync(
        string accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Account ID is required.", nameof(accountId));
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var recoverable = await (
                from intent in context.OrderIntents.AsNoTracking()
                join orderEvent in context.OrderEvents.AsNoTracking()
                    on intent.ClientOrderId equals orderEvent.ClientOrderId
                where intent.AccountId == normalizedAccountId &&
                      intent.Kind == OrderIntentKind.PositionExit &&
                      latestEventIds.Contains(orderEvent.EventId) &&
                      orderEvent.NewState == "INTENT"
                select intent)
            .ToArrayAsync(cancellationToken);
        return recoverable
            .OrderBy(intent => intent.CreatedAtUtc)
            .ThenBy(intent => intent.IntentId)
            .Take(limit)
            .ToArray();
    }

    public async Task<OrderDispatchLease?> TryAcquireDispatchLeaseAsync(
        Guid intentId,
        string accountId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty || String.IsNullOrWhiteSpace(accountId) ||
            String.IsNullOrWhiteSpace(ownerId) || leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Dispatch requires intent, account, owner, and a positive lease duration.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        context.Database.UseTransaction(transaction);
        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var intent = await context.OrderIntents.SingleOrDefaultAsync(
            record => record.IntentId == intentId,
            cancellationToken);
        if (intent is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (!intent.AccountId.Equals(accountId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Intent {intentId:N} belongs to a different broker account.");
        }

        var stateValue = await context.OrderEvents
            .Where(record => record.ClientOrderId == intent.ClientOrderId)
            .OrderByDescending(record => record.EventId)
            .Select(record => record.NewState)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Intent {intentId:N} has no persisted lifecycle event.");
        var state = OrderStateMachine.ParseStorageValue(stateValue);
        var runStatus = await context.ProductionRuns
            .Where(record => record.RunId == intent.RunId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        if (state is not (OrderState.Intent or OrderState.Submitted) ||
            state == OrderState.Intent &&
            intent.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry &&
            !runStatus.Equals("running", StringComparison.Ordinal) ||
            intent.DispatchLeaseExpiresAtUtc is { } currentExpiry && currentExpiry > nowUtc)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var token = Guid.NewGuid();
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        intent.DispatchLeaseOwner = ownerId.Trim();
        intent.DispatchLeaseToken = token;
        intent.DispatchLeaseExpiresAtUtc = leaseExpiresAtUtc;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OrderDispatchLease(intent, state, token, leaseExpiresAtUtc);
    }

    public async Task<OrderIntentRecord> RecordDispatchAttemptAsync(
        Guid intentId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("Dispatch intent and lease token are required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        context.Database.UseTransaction(transaction);
        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var intent = await context.OrderIntents.SingleOrDefaultAsync(
            record => record.IntentId == intentId,
            cancellationToken)
            ?? throw new InvalidOperationException($"Unknown dispatch intent {intentId:N}.");
        if (intent.DispatchLeaseToken != leaseToken ||
            intent.DispatchLeaseExpiresAtUtc is null ||
            intent.DispatchLeaseExpiresAtUtc <= nowUtc)
        {
            throw new OrderDispatchLeaseLostException(intentId);
        }

        var stateValue = await context.OrderEvents
            .Where(record => record.ClientOrderId == intent.ClientOrderId)
            .OrderByDescending(record => record.EventId)
            .Select(record => record.NewState)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Intent {intentId:N} has no persisted lifecycle event.");
        var state = OrderStateMachine.ParseStorageValue(stateValue);
        if (state is not (OrderState.Intent or OrderState.Submitted))
        {
            throw new OrderDispatchLeaseLostException(intentId);
        }

        var runStatus = await context.ProductionRuns
            .Where(record => record.RunId == intent.RunId)
            .Select(record => record.Status)
            .SingleAsync(cancellationToken);
        if (intent.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry &&
            !runStatus.Equals("running", StringComparison.Ordinal))
        {
            throw intent.DispatchAttemptCount > 0
                ? new OrderDispatchAdoptionRequiredException(intentId)
                : new OrderDispatchRunInactiveException(intentId);
        }

        if (intent.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry &&
            intent.DispatchExpiresAtUtc is { } dispatchExpiry && dispatchExpiry <= nowUtc)
        {
            throw intent.DispatchAttemptCount > 0
                ? new OrderDispatchAdoptionRequiredException(intentId)
                : new OrderDispatchExpiredException(intentId, dispatchExpiry);
        }

        if (intent.Kind == OrderIntentKind.ProtectiveStop &&
            intent.PositionGenerationEventId is { } positionGenerationEventId &&
            await HasActivePositionExitAsync(
                context,
                intent.AccountId,
                intent.Symbol,
                positionGenerationEventId,
                cancellationToken))
        {
            throw new PositionExitInProgressException(
                intent.AccountId,
                intent.Symbol,
                positionGenerationEventId);
        }

        intent.DispatchAttemptCount = checked(intent.DispatchAttemptCount + 1);
        intent.LastDispatchAttemptAtUtc = nowUtc;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return intent;
    }

    public async Task<bool> TryExpireUnattemptedUnleasedPositionExitAsync(
        Guid intentId,
        string reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty || String.IsNullOrWhiteSpace(reason) ||
            occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Position-exit expiry requires intent identity, reason, and a UTC timestamp.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        context.Database.UseTransaction(transaction);
        var intent = await context.OrderIntents.SingleOrDefaultAsync(
            record => record.IntentId == intentId,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Unknown position-exit intent {intentId:N}.");
        if (intent.Kind != OrderIntentKind.PositionExit)
        {
            throw new InvalidOperationException(
                $"Intent {intentId:N} is not a position exit.");
        }

        var current = await context.OrderEvents
            .Where(record => record.ClientOrderId == intent.ClientOrderId)
            .OrderByDescending(record => record.EventId)
            .FirstOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException(
                $"Position-exit intent {intentId:N} has no lifecycle event.");
        var state = OrderStateMachine.ParseStorageValue(current.NewState);
        if (OrderStateMachine.IsTerminal(state))
        {
            await transaction.CommitAsync(cancellationToken);
            return state is OrderState.Rejected or OrderState.Canceled or OrderState.Expired;
        }

        var nowUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var hasActiveLease = intent.DispatchLeaseToken is not null &&
            intent.DispatchLeaseExpiresAtUtc is { } leaseExpiry &&
            leaseExpiry > nowUtc;
        if (intent.DispatchAttemptCount != 0 || hasActiveLease ||
            state is not (OrderState.Intent or OrderState.Submitted))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var eventTime = occurredAtUtc < current.LocalTimestampUtc
            ? current.LocalTimestampUtc
            : occurredAtUtc;
        context.OrderEvents.Add(new OrderEventRecord
        {
            ClientOrderId = intent.ClientOrderId,
            BrokerOrderId = current.BrokerOrderId,
            PreviousState = state.ToStorageValue(),
            NewState = OrderState.Expired.ToStorageValue(),
            Source = "engine",
            BrokerTimestampUtc = current.BrokerTimestampUtc,
            LocalTimestampUtc = eventTime,
            FilledQuantity = current.FilledQuantity,
            FillPrice = current.FillPrice,
            PayloadJson = JsonSerializer.Serialize(new
            {
                action = "position_exit_expired_for_protection_restore",
                reason = reason.Trim()
            }),
            RunId = intent.RunId,
            SchemaVersion = intent.SchemaVersion,
            ConfigHash = intent.ConfigHash,
            CodeVersion = intent.CodeVersion
        });
        intent.DispatchLeaseOwner = null;
        intent.DispatchLeaseToken = null;
        intent.DispatchLeaseExpiresAtUtc = null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseDispatchLeaseAsync(
        Guid intentId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        if (intentId == Guid.Empty || leaseToken == Guid.Empty)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.OrderIntents
            .Where(record =>
                record.IntentId == intentId && record.DispatchLeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.DispatchLeaseOwner, (string?)null)
                    .SetProperty(record => record.DispatchLeaseToken, (Guid?)null)
                    .SetProperty(record => record.DispatchLeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken);
    }

    public async Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Order-intent account ID is required.", nameof(accountId));
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
                where intent.AccountId == normalizedAccountId &&
                      intent.Symbol == normalized &&
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

    public async Task<IReadOnlyList<ActiveOrderIntent>> ListActiveProtectiveForSymbolAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Order-intent account ID is required.", nameof(accountId));
        var normalizedSymbol = !String.IsNullOrWhiteSpace(symbol)
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
                where intent.AccountId == normalizedAccountId &&
                      intent.Symbol == normalizedSymbol &&
                      intent.Kind == OrderIntentKind.ProtectiveStop &&
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

    public async Task<bool> HasActivePositionExitAsync(
        string accountId,
        string symbol,
        long positionGenerationEventId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Order-intent account ID is required.", nameof(accountId));
        var normalizedSymbol = !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Order-intent symbol is required.", nameof(symbol));
        if (positionGenerationEventId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionGenerationEventId));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await HasActivePositionExitAsync(
            context,
            normalizedAccountId,
            normalizedSymbol,
            positionGenerationEventId,
            cancellationToken);
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
                EnsureSameRun(existingRun, run);
                if (reservation.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry &&
                    !existingRun.Status.Equals("running", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Cannot submit an entry intent for run {run.RunId:N} in state '{existingRun.Status}'.");
                }
            }

            if (reservation.Kind == OrderIntentKind.PositionExit)
            {
                await ReservePositionExitQuantityAsync(
                    context,
                    reservation,
                    cancellationToken);
            }
            else if (reservation.Kind == OrderIntentKind.ProtectiveStop &&
                     reservation.PositionGenerationEventId is { } protectiveGeneration &&
                     await HasActivePositionExitAsync(
                         context,
                         reservation.AccountId.Trim(),
                         reservation.Symbol.Trim().ToUpperInvariant(),
                         protectiveGeneration,
                         cancellationToken))
            {
                throw new PositionExitInProgressException(
                    reservation.AccountId.Trim(),
                    reservation.Symbol.Trim().ToUpperInvariant(),
                    protectiveGeneration);
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
            if (reservation.Kind == OrderIntentKind.OperatorEntry)
            {
                await ReservePortfolioRiskAsync(
                    context,
                    run,
                    reservation,
                    clientOrderId,
                    serverNowUtc,
                    cancellationToken);
            }

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

                await ReservePortfolioRiskAsync(
                    context,
                    run,
                    reservation,
                    clientOrderId,
                    serverNowUtc,
                    cancellationToken);

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
                AccountId = reservation.AccountId.Trim(),
                StrategyId = reservation.StrategyId,
                Symbol = reservation.Symbol,
                Side = reservation.Side,
                OrderType = reservation.OrderType,
                TimeInForce = reservation.TimeInForce,
                RequestedQuantity = reservation.RequestedQuantity,
                LimitPrice = reservation.LimitPrice,
                StopPrice = reservation.StopPrice,
                PositionGenerationEventId = reservation.PositionGenerationEventId,
                SessionDate = reservation.SessionDate,
                SequenceNumber = sequenceNumber,
                CreatedAtUtc = serverNowUtc,
                DispatchExpiresAtUtc = reservation.DispatchExpiresAtUtc?.ToUniversalTime(),
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
        catch (PortfolioRiskReservationRejectedException exception)
        {
            await PersistRiskRejectionAsync(
                run,
                reservation,
                exception,
                cancellationToken);
            throw;
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
            String.IsNullOrWhiteSpace(reservation.AccountId) ||
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

        if (reservation.Kind == OrderIntentKind.PositionExit &&
            reservation.PositionGenerationEventId is not > 0)
        {
            throw new InvalidOperationException(
                "Position-exit intents require a positive position-generation event ID.");
        }

        if (reservation.Kind is not (OrderIntentKind.PositionExit or OrderIntentKind.ProtectiveStop) &&
            reservation.PositionGenerationEventId is not null)
        {
            throw new InvalidOperationException(
                "Only position-exit and protective-stop intents may carry a position-generation event ID.");
        }

        var entryKind = reservation.Kind is OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry;
        if (entryKind != (reservation.PortfolioRisk is not null) ||
            entryKind != (reservation.DispatchExpiresAtUtc is not null))
        {
            throw new InvalidOperationException(
                "Entry intents require portfolio risk and dispatch expiry; protective and exit intents must carry neither.");
        }

        if (reservation.DispatchExpiresAtUtc is { } dispatchExpiry &&
            (dispatchExpiry.Offset != TimeSpan.Zero || dispatchExpiry <= reservation.CreatedAtUtc))
        {
            throw new InvalidOperationException("Entry dispatch expiry must be a future UTC timestamp.");
        }

        if (reservation.PortfolioRisk is { } risk)
        {
            if (!reservation.AccountId.Equals(risk.AccountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Order intent and portfolio-risk reservation must own the same account.");
            }

            ValidatePortfolioRisk(reservation, risk);
        }
    }

    private static void ValidatePortfolioRisk(
        OrderIntentReservation reservation,
        PortfolioRiskReservationRequest risk)
    {
        if (String.IsNullOrWhiteSpace(risk.AccountId) ||
            risk.Horizon is not ("day" or "swing") ||
            risk.AccountEquity <= 0m ||
            risk.AvailableBuyingPower < 0m ||
            risk.BrokerGrossExposure < 0m ||
            risk.ProposedNotional <= 0m ||
            risk.PlannedRisk <= 0m ||
            risk.MaxGrossExposure <= 0m ||
            risk.MaxPortfolioRisk <= 0m ||
            risk.MaxPositions <= 0 ||
            risk.AccountSnapshotRequestedAtUtc == default ||
            risk.AccountSnapshotRequestedAtUtc.Offset != TimeSpan.Zero ||
            risk.AccountSnapshotObservedAtUtc == default ||
            risk.AccountSnapshotObservedAtUtc.Offset != TimeSpan.Zero ||
            risk.AccountSnapshotObservedAtUtc < risk.AccountSnapshotRequestedAtUtc ||
            risk.BrokerPositionSymbols is null ||
            risk.BrokerPositionSymbols.Any(String.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Portfolio risk reservation data is incomplete or invalid.");
        }

        var expectedNotional = reservation.RequestedQuantity *
            (reservation.LimitPrice ?? throw new InvalidOperationException(
                "Entry risk reservation requires a limit price."));
        if (risk.ProposedNotional != expectedNotional)
        {
            throw new InvalidOperationException(
                "Portfolio risk notional does not match the immutable order intent.");
        }
    }

    private static async Task ReservePortfolioRiskAsync(
        TradingFlowDbContext context,
        ProductionRun run,
        OrderIntentReservation reservation,
        string clientOrderId,
        DateTimeOffset serverNowUtc,
        CancellationToken cancellationToken)
    {
        var requested = reservation.PortfolioRisk
            ?? throw new InvalidOperationException("Entry intent is missing portfolio risk data.");
        var accountId = requested.AccountId.Trim();
        var symbol = reservation.Symbol.Trim().ToUpperInvariant();
        var activeReservations = await context.PortfolioRiskReservations
            .Where(record =>
                record.AccountId == accountId &&
                record.State != PortfolioRiskReservationState.Released)
            .ToArrayAsync(cancellationToken);
        if (activeReservations.Any(record => record.Symbol == symbol))
        {
            throw RiskRejected(
                reservation.IntentId,
                "symbol_already_reserved",
                $"Account {accountId} already has active TradingFlow ownership for {symbol}.");
        }

        var latestPositionIds = context.PositionEvents
            .Where(record => record.AccountId == accountId)
            .GroupBy(record => new { record.AccountId, record.Symbol })
            .Select(group => group.Max(record => record.PositionEventId));
        var localOpenPositions = await context.PositionEvents
            .AsNoTracking()
            .Where(record => latestPositionIds.Contains(record.PositionEventId) && record.QuantityAfter != 0m)
            .Select(record => record.Symbol)
            .ToArrayAsync(cancellationToken);
        if (localOpenPositions.Contains(symbol, StringComparer.Ordinal))
        {
            throw RiskRejected(
                reservation.IntentId,
                "symbol_position_already_open",
                $"The authoritative local position ledger already has an open position for {symbol}.");
        }

        var brokerSymbols = requested.BrokerPositionSymbols
            .Select(value => value.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var localBuyingPower = activeReservations
            .Where(record =>
                record.BrokerAcceptedAtUtc is null ||
                record.BrokerAcceptedAtUtc > requested.AccountSnapshotRequestedAtUtc)
            .Sum(record => record.ReservedBuyingPower);
        decimal localGrossExposure = 0m;
        decimal localNetExposure = 0m;
        foreach (var active in activeReservations)
        {
            var pendingNotional = active.PendingQuantity * active.EntryPrice;
            var unreflectedFillNotional = !brokerSymbols.Contains(active.Symbol)
                ? active.OpenPositionQuantity * active.EntryPrice
                : 0m;
            var unreflectedNotional = pendingNotional + unreflectedFillNotional;
            localGrossExposure += unreflectedNotional;
            localNetExposure += Math.Sign(active.ReservedNetExposure) * unreflectedNotional;
        }

        var availableAfterLocalReservations = requested.AvailableBuyingPower - localBuyingPower;
        if (availableAfterLocalReservations < requested.ProposedNotional)
        {
            throw RiskRejected(
                reservation.IntentId,
                "buying_power_reserved",
                $"Available buying power {requested.AvailableBuyingPower:F2} minus local reservations " +
                $"{localBuyingPower:F2} cannot fund {requested.ProposedNotional:F2}.");
        }

        var projectedGrossExposure = requested.BrokerGrossExposure +
            localGrossExposure + requested.ProposedNotional;
        if (projectedGrossExposure > requested.MaxGrossExposure)
        {
            throw RiskRejected(
                reservation.IntentId,
                "gross_exposure_limit",
                $"Projected gross exposure {projectedGrossExposure:F2} exceeds " +
                $"{requested.MaxGrossExposure:F2}.");
        }

        var activeHeat = activeReservations.Sum(record => record.ReservedPortfolioRisk);
        var projectedHeat = activeHeat + requested.PlannedRisk;
        if (projectedHeat > requested.MaxPortfolioRisk)
        {
            throw RiskRejected(
                reservation.IntentId,
                "portfolio_heat_limit",
                $"Projected portfolio heat {projectedHeat:F2} exceeds {requested.MaxPortfolioRisk:F2}.");
        }

        var occupiedSymbols = brokerSymbols
            .Concat(localOpenPositions)
            .Concat(activeReservations.Select(record => record.Symbol))
            .ToHashSet(StringComparer.Ordinal);
        occupiedSymbols.Add(symbol);
        if (occupiedSymbols.Count > requested.MaxPositions)
        {
            throw RiskRejected(
                reservation.IntentId,
                "position_slot_limit",
                $"Projected position count {occupiedSymbols.Count} exceeds {requested.MaxPositions}.");
        }

        var signedNotional = reservation.Side.Equals("sell", StringComparison.Ordinal)
            ? -requested.ProposedNotional
            : requested.ProposedNotional;
        context.PortfolioRiskReservations.Add(new PortfolioRiskReservationRecord
        {
            ReservationId = Guid.NewGuid(),
            IntentId = reservation.IntentId,
            ClientOrderId = clientOrderId,
            AccountId = accountId,
            Symbol = symbol,
            Horizon = requested.Horizon,
            State = PortfolioRiskReservationState.PendingBrokerSubmission,
            RequestedQuantity = reservation.RequestedQuantity,
            PendingQuantity = reservation.RequestedQuantity,
            CumulativeFilledQuantity = 0m,
            OpenPositionQuantity = 0m,
            EntryPrice = reservation.LimitPrice!.Value,
            RiskPerShare = requested.PlannedRisk / reservation.RequestedQuantity,
            ReservedBuyingPower = requested.ProposedNotional,
            ReservedGrossExposure = requested.ProposedNotional,
            ReservedNetExposure = signedNotional,
            ReservedPortfolioRisk = requested.PlannedRisk,
            ReservedPositionSlots = 1,
            AccountEquityAtReservation = requested.AccountEquity,
            BrokerBuyingPowerAtReservation = requested.AvailableBuyingPower,
            BrokerGrossExposureAtReservation = requested.BrokerGrossExposure,
            BrokerNetExposureAtReservation = requested.BrokerNetExposure,
            BrokerPositionCountAtReservation = brokerSymbols.Count,
            MaxGrossExposure = requested.MaxGrossExposure,
            MaxPortfolioRisk = requested.MaxPortfolioRisk,
            MaxPositions = requested.MaxPositions,
            AccountSnapshotRequestedAtUtc = requested.AccountSnapshotRequestedAtUtc,
            AccountSnapshotObservedAtUtc = requested.AccountSnapshotObservedAtUtc,
            ReservedAtUtc = serverNowUtc,
            StateChangedAtUtc = serverNowUtc,
            Version = 1,
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
        context.RiskEvents.Add(new RiskEventRecord
        {
            EventType = "portfolio_capacity_reserved",
            Severity = "information",
            Symbol = symbol,
            ObservedValue = projectedHeat,
            LimitValue = requested.MaxPortfolioRisk,
            OccurredAtUtc = serverNowUtc,
            DetailsJson = JsonSerializer.Serialize(new
            {
                reservation.IntentId,
                clientOrderId,
                accountId,
                requested.Horizon,
                requested.ProposedNotional,
                requested.PlannedRisk,
                localBuyingPower,
                projectedGrossExposure,
                projectedNetExposure = requested.BrokerNetExposure + localNetExposure + signedNotional,
                projectedHeat,
                projectedPositionCount = occupiedSymbols.Count
            }),
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });
    }

    private static async Task ReservePositionExitQuantityAsync(
        TradingFlowDbContext context,
        OrderIntentReservation reservation,
        CancellationToken cancellationToken)
    {
        var accountId = reservation.AccountId.Trim();
        var symbol = reservation.Symbol.Trim().ToUpperInvariant();
        var generation = reservation.PositionGenerationEventId
            ?? throw new InvalidOperationException(
                "Position-exit reservation has no position generation.");
        var position = await context.PositionEvents
            .AsNoTracking()
            .Where(record => record.AccountId == accountId && record.Symbol == symbol)
            .OrderByDescending(record => record.PositionEventId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new PositionExitReservationRejectedException(
                reservation.IntentId, accountId, symbol, generation,
                reservation.RequestedQuantity, 0m);
        var openQuantity = Math.Abs(position.QuantityAfter);
        var expectedExitSide = position.QuantityAfter > 0m ? "sell" : "buy";
        if (position.PositionGenerationEventId != generation ||
            openQuantity <= 0m ||
            !reservation.Side.Equals(expectedExitSide, StringComparison.OrdinalIgnoreCase))
        {
            throw new PositionExitReservationRejectedException(
                reservation.IntentId, accountId, symbol, generation,
                reservation.RequestedQuantity, 0m);
        }

        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var activeExitReservations = await (
                from intent in context.OrderIntents.AsNoTracking()
                join orderEvent in context.OrderEvents.AsNoTracking()
                    on intent.ClientOrderId equals orderEvent.ClientOrderId
                where intent.AccountId == accountId &&
                      intent.Symbol == symbol &&
                      intent.Kind == OrderIntentKind.PositionExit &&
                      intent.PositionGenerationEventId == generation &&
                      latestEventIds.Contains(orderEvent.EventId)
                select new
                {
                    intent.RequestedQuantity,
                    orderEvent.FilledQuantity,
                    orderEvent.NewState
                })
            .ToArrayAsync(cancellationToken);
        var reservedQuantity = activeExitReservations
            .Where(item => !OrderStateMachine.IsTerminal(
                OrderStateMachine.ParseStorageValue(item.NewState)))
            .Sum(item => Math.Max(
                0m,
                item.RequestedQuantity - (item.FilledQuantity ?? 0m)));
        var availableQuantity = Math.Max(0m, openQuantity - reservedQuantity);
        if (reservation.RequestedQuantity > availableQuantity)
        {
            throw new PositionExitReservationRejectedException(
                reservation.IntentId,
                accountId,
                symbol,
                generation,
                reservation.RequestedQuantity,
                availableQuantity);
        }
    }

    private static async Task<bool> HasActivePositionExitAsync(
        TradingFlowDbContext context,
        string accountId,
        string symbol,
        long positionGenerationEventId,
        CancellationToken cancellationToken)
    {
        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var states = await (
                from intent in context.OrderIntents.AsNoTracking()
                join orderEvent in context.OrderEvents.AsNoTracking()
                    on intent.ClientOrderId equals orderEvent.ClientOrderId
                where intent.AccountId == accountId &&
                      intent.Symbol == symbol &&
                      intent.Kind == OrderIntentKind.PositionExit &&
                      intent.PositionGenerationEventId == positionGenerationEventId &&
                      latestEventIds.Contains(orderEvent.EventId)
                select orderEvent.NewState)
            .ToArrayAsync(cancellationToken);
        return states.Any(state => !OrderStateMachine.IsTerminal(
            OrderStateMachine.ParseStorageValue(state)));
    }

    private async Task PersistRiskRejectionAsync(
        ProductionRun run,
        OrderIntentReservation reservation,
        PortfolioRiskReservationRejectedException exception,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        context.Database.UseTransaction(transaction);
        var occurredAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        await ProductionRunPersistence.EnsureAsync(context, run, cancellationToken);
        context.RiskEvents.Add(new RiskEventRecord
        {
            EventType = exception.ReasonCode,
            Severity = "warning",
            Symbol = reservation.Symbol,
            OccurredAtUtc = occurredAtUtc,
            DetailsJson = JsonSerializer.Serialize(new
            {
                reservation.IntentId,
                exception.ReasonCode,
                exception.Message
            }),
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion
        });

        if (reservation.Kind == OrderIntentKind.StrategyEntry)
        {
            var candidateId = reservation.CandidateId!.Value;
            var expectedVersion = reservation.CandidateExpectedVersion!.Value;
            var blockedVersion = checked(expectedVersion + 1);
            var affected = await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE candidates
                SET state = {StrategyCandidateState.RiskBlocked.ToString()},
                    version = {blockedVersion},
                    revalidated_at_utc = {occurredAtUtc}
                WHERE candidate_id = {candidateId}
                  AND run_id = {run.RunId}
                  AND state = {StrategyCandidateState.Triggered.ToString()}
                  AND version = {expectedVersion}
                  AND semantic_decision_sha256 = {reservation.CandidateSemanticDecisionSha256};
                """,
                cancellationToken);
            if (affected == 1)
            {
                context.CandidateTransitions.Add(new CandidateTransitionRecord
                {
                    CandidateId = candidateId,
                    Sequence = blockedVersion,
                    PreviousState = StrategyCandidateState.Triggered,
                    NewState = StrategyCandidateState.RiskBlocked,
                    OccurredAtUtc = occurredAtUtc,
                    ReasonCode = exception.ReasonCode,
                    Source = "portfolio_risk_reservation",
                    SemanticDecisionSha256 = reservation.CandidateSemanticDecisionSha256!,
                    EvidenceJson = JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        reservation.IntentId,
                        exception.ReasonCode,
                        exception.Message,
                        occurredAtUtc
                    }),
                    RunId = run.RunId,
                    SchemaVersion = run.SchemaVersion,
                    ConfigHash = run.ConfigHash,
                    CodeVersion = run.CodeVersion
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static PortfolioRiskReservationRejectedException RiskRejected(
        Guid intentId,
        string reasonCode,
        string message) => new(intentId, reasonCode, message);

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
            !existing.AccountId.Equals(requested.AccountId, StringComparison.Ordinal) ||
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
            existing.PositionGenerationEventId != requested.PositionGenerationEventId ||
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
            !existing.AccountId.Equals(requested.AccountId, StringComparison.Ordinal) ||
            !existing.StrategyId.Equals(requested.StrategyId, StringComparison.Ordinal) ||
            !existing.Symbol.Equals(requested.Symbol, StringComparison.Ordinal) ||
            !existing.Side.Equals(requested.Side, StringComparison.Ordinal) ||
            !existing.OrderType.Equals(requested.OrderType, StringComparison.Ordinal) ||
            !existing.TimeInForce.Equals(requested.TimeInForce, StringComparison.Ordinal) ||
            existing.RequestedQuantity != requested.RequestedQuantity ||
            existing.LimitPrice != requested.LimitPrice ||
            existing.StopPrice != requested.StopPrice ||
            existing.PositionGenerationEventId != requested.PositionGenerationEventId ||
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
