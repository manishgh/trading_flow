using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Jobs;

public sealed partial class SqliteJobRepository
{
    // Preflight a resumed run without changing its original build, configuration or timestamps.
    public async Task ValidatePaperRunAsync(ProductionRun requested, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ProductionRuns.AsNoTracking()
            .SingleOrDefaultAsync(run => run.RunId == requested.RunId, cancellationToken);
        if (existing is not null)
        {
            RequireSamePaperRun(existing, requested);
        }
    }

    // Publish the running row only while the worker still owns a live paper job
    // lease. A compatible restart reopens status without rewriting provenance.
    public async Task<ProductionRun?> StartPaperRunAsync(
        ProductionRun requested, Guid leaseToken, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        var ownsRun = await context.Jobs.AnyAsync(job =>
            job.Id == requested.RunId && job.JobType == "paper" && job.LeaseToken == leaseToken &&
            job.Status == "running" && job.CancellationRequestedAtUtc == null && job.LeaseExpiresAtUtcTicks > now.UtcTicks,
            cancellationToken);
        if (!ownsRun)
        {
            return null;
        }

        await ProductionRunPersistence.EnsureAsync(context, requested, cancellationToken);
        var run = context.ProductionRuns.Local.Single(item => item.RunId == requested.RunId);
        RequireSamePaperRun(run, requested);
        run.Status = "running";
        run.FinishedAtUtc = null;
        run.PaperJobLeaseToken = leaseToken;
        // Repair a legacy latch found in the saved snapshot before the running
        // row can authorize this attempt, and never clear an existing latch.
        run.PaperEntriesStoppedAtUtc ??= requested.PaperEntriesStoppedAtUtc;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    // Commit the stop marker and its visible health snapshot together under the
    // original paper lease. Later attempts preserve the first triggering time.
    public async Task<bool> SavePaperEntryStopSnapshotAsync(
        Guid runId, Guid leaseToken, string snapshotJson, DateTimeOffset stoppedAtUtc,
        DateTimeOffset heartbeatAtUtc, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await SaveSnapshotCoreAsync(context, runId, leaseToken, snapshotJson, heartbeatAtUtc, cancellationToken))
        {
            return false;
        }

        var updated = await context.ProductionRuns
            .Where(run => run.RunId == runId && run.Status == "running" && run.PaperJobLeaseToken == leaseToken)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.PaperEntriesStoppedAtUtc,
                run => run.PaperEntriesStoppedAtUtc ?? stoppedAtUtc), cancellationToken);
        if (updated != 1)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // The original paper start is immutable along with the shared provenance fields.
    private static void RequireSamePaperRun(ProductionRun existing, ProductionRun requested)
    {
        ProductionRunPersistence.RequireSameProvenance(existing, requested);
        if (existing.StartedAtUtc != requested.StartedAtUtc)
        {
            throw new InvalidOperationException(
                $"Paper run {existing.RunId:N} already exists with different provenance. " +
                $"Original start {existing.StartedAtUtc:O}; requested start {requested.StartedAtUtc:O}.");
        }
    }

    // The durable job identifies an exact strategy-run ID, even for old rows
    // written before paper attempts registered their run at startup.
    private static Task<int> ClosePaperRunAsync(
        TradingFlowDbContext context, Guid id, string status, DateTimeOffset time, CancellationToken cancellationToken,
        bool onlyRunning = false) =>
        context.ProductionRuns.Where(run =>
                run.RunId == id && (!onlyRunning || run.Status == "running") &&
                context.Jobs.Any(job => job.Id == id && job.JobType == "paper"))
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, status)
                .SetProperty(run => run.FinishedAtUtc, time)
                .SetProperty(run => run.PaperJobLeaseToken, (Guid?)null), cancellationToken);
}
