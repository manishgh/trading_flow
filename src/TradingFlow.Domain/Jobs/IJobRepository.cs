using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Jobs;

public interface IJobRepository
{
    Task<PersistedJob?> GetJobAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedJob>> GetAllJobsAsync(CancellationToken cancellationToken);
    Task PruneTerminalJobsOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed record DurableJobLease(
    PersistedJob Job,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc);

/// <summary>
/// Durable queue authority for long-running backtest and paper work. A worker may
/// mutate a claimed job only while it owns the matching lease token.
/// </summary>
public interface IDurableJobRepository : IJobRepository
{
    Task<bool> TryEnqueueAsync(
        PersistedJob job,
        int maximumNonTerminalJobs,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedJob>> GetJobsAsync(
        string jobType,
        CancellationToken cancellationToken);

    Task<bool> RequestCancellationAsync(
        Guid id,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken);

    Task<DurableJobLease?> TryAcquireNextAsync(
        string jobType,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<DurableJobLease?> TryAcquireAsync(
        Guid id,
        string ownerId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewLeaseAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> SaveSnapshotAsync(
        Guid id,
        Guid leaseToken,
        string snapshotJson,
        DateTimeOffset heartbeatAtUtc,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? snapshotJson,
        string? resultReference,
        string? errorMessage,
        DateTimeOffset finishedAtUtc,
        CancellationToken cancellationToken);

    Task ReleaseLeaseAsync(
        Guid id,
        Guid leaseToken,
        CancellationToken cancellationToken);
}
