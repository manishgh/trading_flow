using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Jobs;
using TradingFlow.Data.Context;

namespace TradingFlow.Data.Jobs;

public class SqliteJobRepository : IJobRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteJobRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task SaveJobAsync(PersistedJob job, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Jobs.FindAsync(new object[] { job.Id }, cancellationToken);
        if (existing == null)
        {
            context.Jobs.Add(job);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(job);
        }
        await context.SaveChangesAsync(cancellationToken);
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

    public async Task UpdateJobStatusAsync(Guid id, string status, string? errorMessage, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.FindAsync(new object[] { id }, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            if (errorMessage != null)
            {
                job.ErrorMessage = errorMessage;
            }
            if (status == "running" && job.StartedAt == null)
            {
                job.StartedAt = DateTimeOffset.UtcNow;
            }
            else if (status is "completed" or "failed" or "cancelled")
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
            await context.SaveChangesAsync(cancellationToken);
        }
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
}
