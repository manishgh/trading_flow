using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Stores the current reconciliation mismatch and its explicit operator acknowledgement.
/// Repeated identical mismatches are idempotent; a changed diff supersedes the prior snapshot.
/// </summary>
public sealed class SqliteReconciliationRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IReconciliationRepository
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task<ReconciliationRecord> RecordAsync(
        ReconciliationWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var latest = await context.Reconciliations
                .FromSqlRaw(
                    "SELECT * FROM reconciliations " +
                    "ORDER BY completed_at_utc DESC, started_at_utc DESC LIMIT 1")
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            var outstanding = await context.Reconciliations
                .SingleOrDefaultAsync(record => record.RequiresAcknowledgement, cancellationToken);
            if (request.RequiresAcknowledgement && outstanding is not null)
            {
                if (outstanding.DiffHash.Equals(request.DiffHash, StringComparison.Ordinal))
                {
                    return outstanding;
                }

                outstanding.RequiresAcknowledgement = false;
                outstanding.Status = "superseded";
            }
            else if (request.RequiresAcknowledgement &&
                     latest?.Status == "acknowledged" &&
                     latest.DiffHash.Equals(request.DiffHash, StringComparison.Ordinal))
            {
                return latest;
            }

            await ProductionRunPersistence.EnsureAsync(context, request.Run, cancellationToken);
            var record = new ReconciliationRecord
            {
                ReconciliationId = request.ReconciliationId,
                StartedAtUtc = request.StartedAtUtc.ToUniversalTime(),
                CompletedAtUtc = request.CompletedAtUtc.ToUniversalTime(),
                Status = request.Status.Trim().ToLowerInvariant(),
                BrokerSnapshotJson = request.BrokerSnapshotJson,
                LocalSnapshotJson = request.LocalSnapshotJson,
                DiffJson = request.DiffJson,
                DiffHash = request.DiffHash,
                RequiresAcknowledgement = request.RequiresAcknowledgement,
                RunId = request.Run.RunId,
                SchemaVersion = request.Run.SchemaVersion,
                ConfigHash = request.Run.ConfigHash,
                CodeVersion = request.Run.CodeVersion
            };
            context.Reconciliations.Add(record);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return record;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<ReconciliationRecord?> GetOutstandingAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Reconciliations
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.RequiresAcknowledgement, cancellationToken);
    }

    public async Task<ReconciliationRecord> AcknowledgeAsync(
        ReconciliationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        var actor = Require(acknowledgement.Actor, nameof(acknowledgement.Actor), 120);
        var reason = Require(acknowledgement.Reason, nameof(acknowledgement.Reason), 1000);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var record = await context.Reconciliations.SingleOrDefaultAsync(
                item => item.ReconciliationId == acknowledgement.ReconciliationId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Reconciliation {acknowledgement.ReconciliationId:N} does not exist.");
            if (!record.RequiresAcknowledgement)
            {
                throw new InvalidOperationException(
                    $"Reconciliation {acknowledgement.ReconciliationId:N} is not awaiting acknowledgement.");
            }

            record.RequiresAcknowledgement = false;
            record.Status = "acknowledged";
            record.AcknowledgedAtUtc = acknowledgement.AcknowledgedAtUtc.ToUniversalTime();
            record.AcknowledgedBy = actor;
            record.AcknowledgementReason = reason;
            await context.SaveChangesAsync(cancellationToken);
            return record;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static void Validate(ReconciliationWriteRequest request)
    {
        if (request.ReconciliationId == Guid.Empty || request.DiffHash.Length != 64 ||
            String.IsNullOrWhiteSpace(request.Status) ||
            String.IsNullOrWhiteSpace(request.BrokerSnapshotJson) ||
            String.IsNullOrWhiteSpace(request.LocalSnapshotJson) ||
            String.IsNullOrWhiteSpace(request.DiffJson))
        {
            throw new InvalidOperationException("Reconciliation identity, snapshots, diff, and hash are required.");
        }
    }

    private static string Require(string value, string name, int maximumLength)
    {
        var normalized = !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", name);
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(name, $"Value exceeds {maximumLength} characters.");
        }

        return normalized;
    }
}
