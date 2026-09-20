using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Jobs;
using TradingFlow.Data.Context;

namespace TradingFlow.Data.Jobs;

public sealed class SqliteJobRepository : IDurableJobRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteJobRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> TryEnqueueAsync(
        PersistedJob job,
        int maximumNonTerminalJobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.JobType);
        if (maximumNonTerminalJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNonTerminalJobs));
        }

        if (!job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A newly enqueued durable job must have queued status.", nameof(job));
        }

        if (job.CreatedAtUtcTicks == 0)
        {
            job.CreatedAtUtcTicks = job.CreatedAt.UtcTicks;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);
        await context.Database.UseTransactionAsync(transaction, cancellationToken);
        try
        {
            var nonTerminalCount = await context.Jobs.CountAsync(candidate =>
                candidate.JobType == job.JobType &&
                candidate.Status != "completed" &&
                candidate.Status != "failed" &&
                candidate.Status != "cancelled" &&
                candidate.Status != "interrupted",
                cancellationToken);
            if (nonTerminalCount >= maximumNonTerminalJobs)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            context.Jobs.Add(job);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PersistedJob?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Jobs.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedJob>> GetAllJobsAsync(CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Jobs.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedJob>> GetJobsAsync(
        string jobType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Jobs
            .AsNoTracking()
            .Where(job => job.JobType == jobType)
            .OrderByDescending(job => job.CreatedAtUtcTicks)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> RequestCancellationAsync(
        Guid id,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var cancelledBeforeDispatch = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "Status" = 'cancelled',
                "CancellationRequestedAtUtc" = {requestedAtUtc},
                "FinishedAt" = {requestedAtUtc},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAtUtc" = NULL,
                "LeaseExpiresAtUtcTicks" = NULL
            WHERE "Id" = {id}
              AND "Status" = 'queued'
              AND ("LeaseToken" IS NULL OR "LeaseExpiresAtUtcTicks" <=
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER));
            """, cancellationToken);
        if (cancelledBeforeDispatch == 1)
        {
            return true;
        }

        var cancellationRequested = await context.Jobs
            .Where(job => job.Id == id &&
                job.Status != "completed" &&
                job.Status != "failed" &&
                job.Status != "cancelled" &&
                job.Status != "interrupted")
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "cancelling")
                .SetProperty(job => job.CancellationRequestedAtUtc, requestedAtUtc),
                cancellationToken);
        return cancellationRequested == 1;
    }

    public async Task<DurableJobLease?> TryAcquireNextAsync(
        string jobType,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ValidateLease(ownerId, leaseDuration);

        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var candidateIds = await context.Jobs
            .AsNoTracking()
            .Where(job => job.JobType == jobType &&
                (job.Status == "queued" || job.Status == "running" || job.Status == "cancelling"))
            .OrderBy(job => job.CreatedAtUtcTicks)
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken);

        // The durable queue is bounded, so it is cheap to let the atomic claim skip
        // jobs whose leases are still live. Lease eligibility itself is evaluated
        // by SQLite's clock in TryAcquireCoreAsync, never by a worker's local clock.
        foreach (var candidateId in candidateIds)
        {
            var lease = await TryAcquireCoreAsync(
                context,
                candidateId,
                ownerId,
                nowUtc,
                leaseDuration,
                cancellationToken);
            if (lease is not null)
            {
                return lease;
            }
        }

        return null;
    }

    public async Task<DurableJobLease?> TryAcquireAsync(
        Guid id,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLease(ownerId, leaseDuration);
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await TryAcquireCoreAsync(context, id, ownerId, nowUtc, leaseDuration, cancellationToken);
    }

    public async Task<bool> RenewLeaseAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "LeaseExpiresAtUtc" = {leaseExpiresAtUtc},
                "LeaseExpiresAtUtcTicks" =
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) + {leaseDuration.Ticks},
                "HeartbeatAtUtc" = {nowUtc}
            WHERE "Id" = {id}
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAtUtcTicks" >
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER)
              AND ("Status" = 'running' OR "Status" = 'cancelling');
            """, cancellationToken);
        return updated == 1;
    }

    public async Task<bool> SaveSnapshotAsync(
        Guid id,
        Guid leaseToken,
        string snapshotJson,
        DateTimeOffset heartbeatAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "SnapshotJson" = {snapshotJson},
                "HeartbeatAtUtc" = {heartbeatAtUtc}
            WHERE "Id" = {id}
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAtUtcTicks" >
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER)
              AND ("Status" = 'running' OR "Status" = 'cancelling');
            """, cancellationToken);
        return updated == 1;
    }

    public async Task<bool> CompleteAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? snapshotJson,
        string? resultReference,
        string? errorMessage,
        DateTimeOffset finishedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!IsTerminal(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "A durable job can only complete in a terminal state.");
        }

        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var requireNoCancellation = status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "Status" = {status},
                "SnapshotJson" = {snapshotJson},
                "ResultReference" = {resultReference},
                "ErrorMessage" = {errorMessage},
                "FinishedAt" = {finishedAtUtc},
                "HeartbeatAtUtc" = {finishedAtUtc},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAtUtc" = NULL,
                "LeaseExpiresAtUtcTicks" = NULL
            WHERE "Id" = {id}
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAtUtcTicks" >
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER)
              AND ({requireNoCancellation} = 0 OR "CancellationRequestedAtUtc" IS NULL);
            """, cancellationToken);
        return updated == 1;
    }

    public async Task ReleaseLeaseAsync(
        Guid id,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Jobs
            .Where(job => job.Id == id && job.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtcTicks, (long?)null),
                cancellationToken);
    }

    public async Task PruneTerminalJobsOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await context.Jobs.ToArrayAsync(cancellationToken);
        var staleJobs = jobs
            .Where(job => IsTerminal(job.Status) && (job.FinishedAt ?? job.CreatedAt) < cutoff)
            .ToArray();
        if (staleJobs.Length == 0)
        {
            return;
        }

        context.Jobs.RemoveRange(staleJobs);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsTerminal(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DurableJobLease?> TryAcquireCoreAsync(
        TradingFlowDbContext context,
        Guid id,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();
        var databaseNowTicks = await context.Database
            .SqlQueryRaw<long>(
                "SELECT CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) AS \"Value\"")
            .SingleAsync(cancellationToken);
        var databaseNowUtc = new DateTimeOffset(databaseNowTicks, TimeSpan.Zero);
        var leaseExpiresAtUtc = databaseNowUtc.Add(leaseDuration);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Jobs"
            SET "Status" = CASE
                    WHEN "CancellationRequestedAtUtc" IS NULL THEN 'running'
                    ELSE 'cancelling'
                END,
                "LeaseOwner" = {ownerId.Trim()},
                "LeaseToken" = {leaseToken},
                "LeaseExpiresAtUtc" = {leaseExpiresAtUtc},
                "LeaseExpiresAtUtcTicks" =
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER) + {leaseDuration.Ticks},
                "HeartbeatAtUtc" = {databaseNowUtc},
                "StartedAt" = COALESCE("StartedAt", {databaseNowUtc}),
                "AttemptCount" = "AttemptCount" + 1
            WHERE "Id" = {id}
              AND ("Status" = 'queued' OR "Status" = 'running' OR "Status" = 'cancelling')
              AND ("LeaseToken" IS NULL OR "LeaseExpiresAtUtcTicks" <=
                    CAST((julianday('now') - 1721425.5) * 864000000000 AS INTEGER));
            """, cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        var job = await context.Jobs.AsNoTracking().SingleAsync(candidate => candidate.Id == id, cancellationToken);
        return new DurableJobLease(job, leaseToken, job.LeaseExpiresAtUtc ?? leaseExpiresAtUtc);
    }

    private static void ValidateLease(string ownerId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }
}
