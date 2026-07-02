using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Domain.Jobs;

public interface IJobRepository
{
    Task SaveJobAsync(PersistedJob job, CancellationToken cancellationToken);
    Task<PersistedJob?> GetJobAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedJob>> GetAllJobsAsync(CancellationToken cancellationToken);
    Task UpdateJobStatusAsync(Guid id, string status, string? errorMessage, CancellationToken cancellationToken);
    Task PruneTerminalJobsOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
