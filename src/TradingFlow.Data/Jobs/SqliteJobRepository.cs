using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;
using TradingFlow.Data.Context;

namespace TradingFlow.Data.Jobs;

public sealed partial class SqliteJobRepository : IDurableJobRepository, IPaperRunLifecycleRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteJobRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>Uses the same atomic admission rule for a single job and a cohort comparison.</summary>
    public Task<bool> TryEnqueueAsync(
        PersistedJob job,
        int maximumNonTerminalJobs,
        CancellationToken cancellationToken) =>
        TryEnqueueBatchAsync([job], maximumNonTerminalJobs, cancellationToken);

    /// <summary>Checks capacity and inserts the entire matrix while holding SQLite's write transaction.</summary>
    public async Task<bool> TryEnqueueBatchAsync(
        IReadOnlyList<PersistedJob> jobs,
        int maximumNonTerminalJobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0 || jobs.Any(job => job is null || String.IsNullOrWhiteSpace(job.JobType)) ||
            jobs.Select(job => job.Id).Distinct().Count() != jobs.Count || jobs.Select(job => job.JobType).Distinct().Count() != 1)
            throw new ArgumentException("Select distinct jobs of one kind for atomic admission.", nameof(jobs));
        if (maximumNonTerminalJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNonTerminalJobs));
        }

        if (jobs.Any(job => !job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("New durable jobs must have queued status.", nameof(jobs));
        }

        foreach (var job in jobs)
        {
            if (job.CreatedAtUtcTicks == 0) job.CreatedAtUtcTicks = job.CreatedAt.UtcTicks;
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
            var ids = jobs.Select(job => job.Id).ToArray();
            if (await context.Jobs.AnyAsync(candidate => ids.Contains(candidate.Id), cancellationToken))
                return false;
            var jobType = jobs[0].JobType;
            var nonTerminalCount = await context.Jobs.CountAsync(candidate =>
                candidate.JobType == jobType &&
                candidate.Status != "completed" &&
                candidate.Status != "failed" &&
                candidate.Status != "cancelled" &&
                candidate.Status != "interrupted",
                cancellationToken);
            if (jobs.Count > maximumNonTerminalJobs - nonTerminalCount)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            context.Jobs.AddRange(jobs);
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

    /// <summary>
    /// Projects only Id and RequestJson first to locate jobs for <paramref name="planId"/> without reading unrelated
    /// multi-megabyte SnapshotJson payloads, then fetches full PersistedJob rows only for matching IDs.
    /// </summary>
    public async Task<IReadOnlyList<PersistedJob>> GetJobsForPlanAsync(
        string jobType,
        long planId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        if (planId <= 0) return [];
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await context.Jobs
            .AsNoTracking()
            .Where(job => job.JobType == jobType)
            .Select(job => new { job.Id, job.RequestJson })
            .ToArrayAsync(cancellationToken);

        var matchingIds = candidates
            .Where(item => TryExtractPlanId(item.RequestJson) == planId)
            .Select(item => item.Id)
            .ToArray();
        if (matchingIds.Length == 0) return [];

        return await context.Jobs
            .AsNoTracking()
            .Where(job => matchingIds.Contains(job.Id))
            .OrderByDescending(job => job.CreatedAtUtcTicks)
            .ToArrayAsync(cancellationToken);
    }

    // Extracts workflow.cohort.planId via JsonDocument without allocating full file evidence lists or throwing on malformed legacy JSON.
    internal static long? TryExtractPlanId(string? requestJson)
    {
        if (String.IsNullOrWhiteSpace(requestJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetPropertyCaseInsensitive(root, "workflow", out var workflow) || workflow.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetPropertyCaseInsensitive(workflow, "cohort", out var cohort) || cohort.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetPropertyCaseInsensitive(cohort, "planId", out var planIdElement) || planIdElement.ValueKind != JsonValueKind.Number)
                return null;
            return planIdElement.TryGetInt64(out var planId) ? planId : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads a named JSON object property case-insensitively so camelCase and PascalCase durable payloads both match.
    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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

    // Retain the snapshot only while this worker owns a live job lease.
    public async Task<bool> SaveSnapshotAsync(
        Guid id,
        Guid leaseToken,
        string snapshotJson,
        DateTimeOffset heartbeatAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await SaveSnapshotCoreAsync(context, id, leaseToken, snapshotJson, heartbeatAtUtc, cancellationToken);
    }

    // Reuse one publication fence for normal progress and the atomic paper entry stop.
    private static async Task<bool> SaveSnapshotCoreAsync(
        TradingFlowDbContext context, Guid id, Guid leaseToken, string snapshotJson, DateTimeOffset heartbeatAtUtc,
        CancellationToken cancellationToken)
    {
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

    // Publish terminal status and close the paper run under the same lease fence and transaction.
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
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
        if (updated == 1)
        {
            await ClosePaperRunAsync(context, id, status, finishedAtUtc, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }

    // Release only this attempt and close its run after the handler has drained.
    public async Task ReleaseLeaseAsync(
        Guid id,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var updated = await context.Jobs
            .Where(job => job.Id == id && job.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtcTicks, (long?)null),
                cancellationToken);
        if (updated == 1)
        {
            var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
            await ClosePaperRunAsync(context, id, "stopped", now, cancellationToken, onlyRunning: true);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsTerminal(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    // Atomic takeover first closes a prior paper attempt whose lease is absent or expired.
    private static async Task<DurableJobLease?> TryAcquireCoreAsync(
        TradingFlowDbContext context,
        Guid id,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
        if (job.AttemptCount > 1)
        {
            await ClosePaperRunAsync(context, id, "interrupted", databaseNowUtc, cancellationToken, onlyRunning: true);
        }
        await transaction.CommitAsync(cancellationToken);
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
