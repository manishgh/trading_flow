using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Serializes desired protective-stop revisions in SQLite before any broker PATCH.
/// A higher pending revision supersedes idle lower work, but never revokes an
/// unexpired lease. Commands in one successor chain execute serially.
/// </summary>
public sealed class SqliteProtectiveStopReplacementRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory,
    TimeProvider timeProvider) : IProtectiveStopReplacementRepository
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task<ProtectiveStopReplacementRecord> ReserveAsync(
        ProtectiveStopReplacementReservation reservation,
        CancellationToken cancellationToken = default)
    {
        Validate(reservation);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await OpenWriteContextAsync(cancellationToken);
            await using var transaction = BeginImmediateTransaction(context);
            var existing = await context.ProtectiveStopReplacements
                .SingleOrDefaultAsync(record => record.CommandId == reservation.CommandId, cancellationToken);
            if (existing is not null)
            {
                EnsureExactReplay(existing, reservation);
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var accountId = reservation.AccountId.Trim();
            var brokerOrderId = reservation.BrokerOrderId.Trim();
            var predecessor = await context.ProtectiveStopReplacements
                .AsNoTracking()
                .SingleOrDefaultAsync(record =>
                    record.AccountId == accountId &&
                    record.VerifiedBrokerOrderId == brokerOrderId,
                    cancellationToken);
            if (predecessor is not null &&
                !predecessor.OwnerClientOrderId.Equals(
                    reservation.OwnerClientOrderId.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Broker successor '{brokerOrderId}' belongs to another protective-order owner.");
            }

            var rootBrokerOrderId = predecessor?.RootBrokerOrderId ?? brokerOrderId;
            var active = await context.ProtectiveStopReplacements
                .Where(record =>
                    record.AccountId == accountId &&
                    record.RootBrokerOrderId == rootBrokerOrderId &&
                    record.State != ProtectiveStopReplacementState.Superseded)
                .ToArrayAsync(cancellationToken);
            // SQLite persists decimal values as text, so compare stop prices after materialization.
            // The set is bounded to the replacement history of one broker order.
            var highest = active.OrderByDescending(record => record.StopPrice).FirstOrDefault();
            if (highest is not null && highest.StopPrice >= reservation.StopPrice)
            {
                if (highest.StopPrice == reservation.StopPrice)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return highest;
                }

                throw new InvalidOperationException(
                    $"Protective stop cannot be lowered from {highest.StopPrice} to {reservation.StopPrice}.");
            }

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            foreach (var pending in active.Where(record =>
                         record.State == ProtectiveStopReplacementState.Pending &&
                         record.StopPrice < reservation.StopPrice))
            {
                if (pending.LeaseToken is not null && pending.LeaseExpiresAtUtc is { } expires && expires > now)
                {
                    continue;
                }

                pending.State = ProtectiveStopReplacementState.Superseded;
                pending.LeaseOwner = null;
                pending.LeaseToken = null;
                pending.LeaseExpiresAtUtc = null;
                pending.LastError = "superseded_by_higher_stop";
                pending.Version = checked(pending.Version + 1);
            }

            var command = new ProtectiveStopReplacementRecord
            {
                CommandId = reservation.CommandId,
                AccountId = accountId,
                OwnerClientOrderId = reservation.OwnerClientOrderId.Trim(),
                RootBrokerOrderId = rootBrokerOrderId,
                BrokerOrderId = brokerOrderId,
                ReplacementClientOrderId = reservation.ReplacementClientOrderId.Trim(),
                Symbol = reservation.Symbol.Trim().ToUpperInvariant(),
                StopPrice = reservation.StopPrice,
                Reason = reservation.Reason.Trim(),
                RequestedAtUtc = reservation.RequestedAtUtc.ToUniversalTime(),
                State = ProtectiveStopReplacementState.Pending,
                RunId = reservation.RunId,
                SchemaVersion = reservation.SchemaVersion,
                ConfigHash = reservation.ConfigHash,
                CodeVersion = reservation.CodeVersion,
                Version = 1
            };
            context.ProtectiveStopReplacements.Add(command);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return command;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> ListRecoverableCommandIdsAsync(
        string accountId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeRequired(accountId, nameof(accountId));
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ProtectiveStopReplacements
            .AsNoTracking()
            .Where(record =>
                record.AccountId == normalizedAccountId &&
                record.State == ProtectiveStopReplacementState.Pending &&
                (record.LeaseExpiresAtUtc == null || record.LeaseExpiresAtUtc <= now))
            .OrderBy(record => record.RequestedAtUtc)
            .Select(record => record.CommandId)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ProtectiveStopReplacementLease?> TryAcquireLeaseAsync(
        Guid commandId,
        string accountId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var normalizedAccountId = NormalizeRequired(accountId, nameof(accountId));
        var normalizedOwner = NormalizeRequired(leaseOwner, nameof(leaseOwner));
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await OpenWriteContextAsync(cancellationToken);
            await using var transaction = BeginImmediateTransaction(context);
            var command = await context.ProtectiveStopReplacements
                .SingleOrDefaultAsync(record => record.CommandId == commandId, cancellationToken);
            if (command is null ||
                command.State != ProtectiveStopReplacementState.Pending ||
                !command.AccountId.Equals(normalizedAccountId, StringComparison.Ordinal) ||
                command.LeaseExpiresAtUtc is { } expires && expires > now)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var pendingChain = await context.ProtectiveStopReplacements
                .Where(record =>
                    record.AccountId == normalizedAccountId &&
                    record.RootBrokerOrderId == command.RootBrokerOrderId &&
                    record.State == ProtectiveStopReplacementState.Pending)
                .ToArrayAsync(cancellationToken);
            if (pendingChain.Any(record =>
                    record.CommandId != command.CommandId &&
                    record.LeaseToken is not null &&
                    record.LeaseExpiresAtUtc is { } otherExpiry &&
                    otherExpiry > now))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var highestPending = pendingChain
                .OrderByDescending(record => record.StopPrice)
                .ThenBy(record => record.RequestedAtUtc)
                .First();
            if (highestPending.CommandId != command.CommandId)
            {
                command.State = ProtectiveStopReplacementState.Superseded;
                command.LastError = "superseded_by_higher_stop";
                command.Version = checked(command.Version + 1);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var leaseToken = Guid.NewGuid();
            command.LeaseOwner = normalizedOwner;
            command.LeaseToken = leaseToken;
            command.LeaseExpiresAtUtc = now.Add(leaseDuration);
            command.AttemptCount = checked(command.AttemptCount + 1);
            command.LastAttemptAtUtc = now;
            command.LastError = null;
            command.Version = checked(command.Version + 1);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ProtectiveStopReplacementLease(command, leaseToken, command.LeaseExpiresAtUtc.Value);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task AssertExecutionAuthorityAsync(
        Guid commandId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("Command and lease identity are required.");
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await OpenWriteContextAsync(cancellationToken);
            await using var transaction = BeginImmediateTransaction(context);
            var command = await context.ProtectiveStopReplacements
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.CommandId == commandId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown protective-stop command {commandId:N}.");
            var conflictingLease = await context.ProtectiveStopReplacements
                .AsNoTracking()
                .AnyAsync(record =>
                    record.AccountId == command.AccountId &&
                    record.RootBrokerOrderId == command.RootBrokerOrderId &&
                    record.CommandId != command.CommandId &&
                    record.State == ProtectiveStopReplacementState.Pending &&
                    record.LeaseToken != null &&
                    record.LeaseExpiresAtUtc != null &&
                    record.LeaseExpiresAtUtc > now,
                    cancellationToken);
            if (command.State != ProtectiveStopReplacementState.Pending ||
                command.LeaseToken != leaseToken ||
                command.LeaseExpiresAtUtc is not { } expires ||
                expires <= now ||
                conflictingLease)
            {
                throw new InvalidOperationException(
                    $"Protective-stop command {commandId:N} no longer has exclusive execution authority.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<ProtectiveStopReplacementRecord?> GetVerifiedBySuccessorBrokerOrderIdAsync(
        string accountId,
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeRequired(accountId, nameof(accountId));
        var normalizedBrokerOrderId = NormalizeRequired(brokerOrderId, nameof(brokerOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ProtectiveStopReplacements
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.AccountId == normalizedAccountId &&
                record.State == ProtectiveStopReplacementState.Verified &&
                record.VerifiedBrokerOrderId == normalizedBrokerOrderId,
                cancellationToken);
    }

    public async Task<ProtectiveStopReplacementRecord?> GetVerifiedByReplacementClientOrderIdAsync(
        string replacementClientOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientOrderId = NormalizeRequired(
            replacementClientOrderId,
            nameof(replacementClientOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ProtectiveStopReplacements
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.State == ProtectiveStopReplacementState.Verified &&
                record.ReplacementClientOrderId == normalizedClientOrderId,
                cancellationToken);
    }

    public async Task<string> ResolveCurrentBrokerOrderIdAsync(
        string accountId,
        string rootBrokerOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeRequired(accountId, nameof(accountId));
        var normalizedRootOrderId = NormalizeRequired(rootBrokerOrderId, nameof(rootBrokerOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var verified = await context.ProtectiveStopReplacements
            .AsNoTracking()
            .Where(record =>
                record.AccountId == normalizedAccountId &&
                record.RootBrokerOrderId == normalizedRootOrderId &&
                record.State == ProtectiveStopReplacementState.Verified &&
                record.VerifiedBrokerOrderId != null)
            .ToArrayAsync(cancellationToken);
        return verified
            .OrderByDescending(record => record.VerifiedAtUtc)
            .ThenByDescending(record => record.Version)
            .Select(record => record.VerifiedBrokerOrderId!)
            .FirstOrDefault() ?? normalizedRootOrderId;
    }

    public async Task MarkVerifiedWithLifecycleAsync(
        Guid commandId,
        Guid leaseToken,
        string verifiedBrokerOrderId,
        DateTimeOffset verifiedAtUtc,
        BrokerOrderReplacementTransition? lifecycleTransition,
        CancellationToken cancellationToken = default)
    {
        var brokerOrderId = NormalizeRequired(verifiedBrokerOrderId, nameof(verifiedBrokerOrderId));
        if (verifiedAtUtc == default || verifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Replacement verification requires a UTC timestamp.");
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await OpenWriteContextAsync(cancellationToken);
            await using var transaction = BeginImmediateTransaction(context);
            var command = await context.ProtectiveStopReplacements
                .SingleOrDefaultAsync(record => record.CommandId == commandId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown protective-stop command {commandId:N}.");
            if (command.State != ProtectiveStopReplacementState.Pending || command.LeaseToken != leaseToken)
            {
                throw new InvalidOperationException(
                    $"Protective-stop command {commandId:N} is not owned by lease {leaseToken:N}.");
            }

            if (lifecycleTransition is not null)
            {
                ValidateLifecycleTransition(command, brokerOrderId, lifecycleTransition);
                var current = await context.OrderEvents
                    .Where(record => record.ClientOrderId == command.OwnerClientOrderId)
                    .OrderByDescending(record => record.EventId)
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Protective owner '{command.OwnerClientOrderId}' has no lifecycle state.");
                if (!String.Equals(current.BrokerOrderId, brokerOrderId, StringComparison.Ordinal))
                {
                    if (!String.Equals(
                            current.BrokerOrderId,
                            lifecycleTransition.PredecessorBrokerOrderId,
                            StringComparison.Ordinal) ||
                        OrderStateMachine.IsTerminal(
                            OrderStateMachine.ParseStorageValue(current.NewState)))
                    {
                        throw new InvalidOperationException(
                            $"Protective owner '{command.OwnerClientOrderId}' does not own predecessor order '{lifecycleTransition.PredecessorBrokerOrderId}'.");
                    }

                    context.OrderEvents.Add(new OrderEventRecord
                    {
                        ClientOrderId = command.OwnerClientOrderId,
                        BrokerOrderId = brokerOrderId,
                        PreviousState = current.NewState,
                        NewState = current.NewState,
                        Source = "broker_rest",
                        BrokerTimestampUtc = lifecycleTransition.BrokerTimestampUtc,
                        LocalTimestampUtc = lifecycleTransition.LocalTimestampUtc,
                        FilledQuantity = current.FilledQuantity,
                        FillPrice = current.FillPrice,
                        PayloadJson = lifecycleTransition.PayloadJson,
                        RunId = current.RunId,
                        SchemaVersion = current.SchemaVersion,
                        ConfigHash = current.ConfigHash,
                        CodeVersion = current.CodeVersion
                    });
                }
            }

            command.State = ProtectiveStopReplacementState.Verified;
            command.VerifiedAtUtc = verifiedAtUtc;
            command.VerifiedBrokerOrderId = brokerOrderId;
            command.LastError = null;
            command.LeaseOwner = null;
            command.LeaseToken = null;
            command.LeaseExpiresAtUtc = null;
            command.Version = checked(command.Version + 1);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public Task ReleaseLeaseAsync(
        Guid commandId,
        Guid leaseToken,
        string error,
        CancellationToken cancellationToken = default) =>
        UpdateLeasedCommandAsync(
            commandId,
            leaseToken,
            command => command.LastError = NormalizeError(error),
            cancellationToken);

    private async Task UpdateLeasedCommandAsync(
        Guid commandId,
        Guid leaseToken,
        Action<ProtectiveStopReplacementRecord> update,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("Command and lease identity are required.");
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await OpenWriteContextAsync(cancellationToken);
            await using var transaction = BeginImmediateTransaction(context);
            var command = await context.ProtectiveStopReplacements
                .SingleOrDefaultAsync(record => record.CommandId == commandId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown protective-stop command {commandId:N}.");
            if (command.State != ProtectiveStopReplacementState.Pending || command.LeaseToken != leaseToken)
            {
                throw new InvalidOperationException(
                    $"Protective-stop command {commandId:N} is not owned by lease {leaseToken:N}.");
            }

            update(command);
            command.LeaseOwner = null;
            command.LeaseToken = null;
            command.LeaseExpiresAtUtc = null;
            command.Version = checked(command.Version + 1);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task<TradingFlowDbContext> OpenWriteContextAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        return context;
    }

    private static SqliteTransaction BeginImmediateTransaction(TradingFlowDbContext context)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        return transaction;
    }

    private static void Validate(ProtectiveStopReplacementReservation reservation)
    {
        if (reservation.CommandId == Guid.Empty || reservation.RunId == Guid.Empty ||
            reservation.SchemaVersion <= 0 || reservation.ConfigHash.Length != 64 ||
            String.IsNullOrWhiteSpace(reservation.CodeVersion) ||
            String.IsNullOrWhiteSpace(reservation.AccountId) ||
            String.IsNullOrWhiteSpace(reservation.OwnerClientOrderId) ||
            String.IsNullOrWhiteSpace(reservation.BrokerOrderId) ||
            String.IsNullOrWhiteSpace(reservation.ReplacementClientOrderId) ||
            String.IsNullOrWhiteSpace(reservation.Symbol) ||
            String.IsNullOrWhiteSpace(reservation.Reason) ||
            reservation.StopPrice <= 0m || reservation.RequestedAtUtc == default ||
            reservation.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Protective-stop replacement reservation is incomplete or invalid.");
        }
    }

    private static void ValidateLifecycleTransition(
        ProtectiveStopReplacementRecord command,
        string verifiedBrokerOrderId,
        BrokerOrderReplacementTransition transition)
    {
        if (!transition.OwnerClientOrderId.Equals(command.OwnerClientOrderId, StringComparison.Ordinal) ||
            !transition.SuccessorBrokerOrderId.Equals(verifiedBrokerOrderId, StringComparison.Ordinal) ||
            transition.PredecessorBrokerOrderId.Equals(
                transition.SuccessorBrokerOrderId,
                StringComparison.Ordinal) ||
            transition.BrokerTimestampUtc == default ||
            transition.LocalTimestampUtc == default ||
            transition.BrokerTimestampUtc.Offset != TimeSpan.Zero ||
            transition.LocalTimestampUtc.Offset != TimeSpan.Zero ||
            String.IsNullOrWhiteSpace(transition.PayloadJson))
        {
            throw new InvalidOperationException(
                "Protective replacement lifecycle transition does not match its durable command.");
        }
    }

    private static void EnsureExactReplay(
        ProtectiveStopReplacementRecord existing,
        ProtectiveStopReplacementReservation reservation)
    {
        if (!existing.AccountId.Equals(reservation.AccountId.Trim(), StringComparison.Ordinal) ||
            !existing.OwnerClientOrderId.Equals(reservation.OwnerClientOrderId.Trim(), StringComparison.Ordinal) ||
            !existing.BrokerOrderId.Equals(reservation.BrokerOrderId.Trim(), StringComparison.Ordinal) ||
            !existing.ReplacementClientOrderId.Equals(
                reservation.ReplacementClientOrderId.Trim(),
                StringComparison.Ordinal) ||
            !existing.Symbol.Equals(reservation.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
            existing.StopPrice != reservation.StopPrice ||
            existing.RunId != reservation.RunId ||
            existing.SchemaVersion != reservation.SchemaVersion ||
            !existing.ConfigHash.Equals(reservation.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(reservation.CodeVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Protective-stop command {reservation.CommandId:N} was replayed with different immutable data.");
        }
    }

    private static string NormalizeRequired(string value, string parameterName) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", parameterName);

    private static string NormalizeError(string value)
    {
        var normalized = String.IsNullOrWhiteSpace(value) ? "unknown_replacement_failure" : value.Trim();
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000];
    }
}
