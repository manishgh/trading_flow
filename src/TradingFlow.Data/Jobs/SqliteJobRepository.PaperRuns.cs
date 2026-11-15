using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Jobs;

public sealed partial class SqliteJobRepository
{
    private static readonly JsonSerializerOptions PaperAttemptJson = new(JsonSerializerDefaults.Web);

    // Preflight the frozen run identity. Startup handles a build change under the paper lease.
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
    // lease. A rebuild records the executing build and stops entries in the same transaction.
    public async Task<PaperRunStart?> StartPaperRunAsync(
        ProductionRun requested, Guid leaseToken, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        var leaseAttempt = await context.Jobs
            .Where(job => job.Id == requested.RunId && job.JobType == "paper" && job.LeaseToken == leaseToken &&
                job.Status == "running" && job.CancellationRequestedAtUtc == null && job.LeaseExpiresAtUtcTicks > now.UtcTicks)
            .Select(job => (int?)job.AttemptCount)
            .SingleOrDefaultAsync(cancellationToken);
        if (leaseAttempt is null)
        {
            return null;
        }

        var run = await context.ProductionRuns.SingleOrDefaultAsync(item => item.RunId == requested.RunId, cancellationToken);
        if (run is null)
        {
            run = requested;
            context.ProductionRuns.Add(run);
        }
        else
        {
            RequireSamePaperRun(run, requested);
        }

        // Repair a legacy health latch before the running row can authorize this attempt.
        run.PaperEntriesStoppedAtUtc ??= requested.PaperEntriesStoppedAtUtc;
        var continuation = await RegisterExitOnlyAttemptAsync(
            context, run, requested.CodeVersion, leaseToken, leaseAttempt.Value, now, cancellationToken);
        run.Status = "running";
        run.FinishedAtUtc = null;
        run.PaperJobLeaseToken = leaseToken;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PaperRunStart(run, continuation);
    }

    // Preserve exit-only mode even if a later lease runs the original build or lost its job snapshot.
    private static async Task<PaperExitOnlyContinuation?> RegisterExitOnlyAttemptAsync(
        TradingFlowDbContext context, ProductionRun run, string executingBuild, Guid leaseToken, int leaseAttempt,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changedBuild = !String.Equals(run.CodeVersion, executingBuild, StringComparison.Ordinal);
        if (!changedBuild && run.PaperEntriesStoppedAtUtc is null)
        {
            return null;
        }

        var stream = PaperExitOnlyContinuation.StreamName(run.RunId);
        var priorJson = await context.ApplicationEvents
            .Where(record => record.StreamName == stream && record.EventType == PaperExitOnlyContinuation.EventType)
            .OrderByDescending(record => record.EventSequence)
            .Select(record => record.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (!changedBuild && priorJson is null)
        {
            return null;
        }

        // A repeated registration of the same lease must identify the same executing build.
        if (run.PaperJobLeaseToken == leaseToken)
        {
            var prior = priorJson is null ? null : JsonSerializer.Deserialize<PaperExitOnlyContinuation>(priorJson, PaperAttemptJson);
            if (prior is { } && prior.LeaseAttempt == leaseAttempt &&
                String.Equals(prior.ExecutingCodeVersion, executingBuild, StringComparison.Ordinal))
            {
                return prior;
            }

            throw new InvalidOperationException($"Paper run {run.RunId:N} cannot change the executing build within a lease.");
        }

        if (leaseAttempt < 2)
        {
            throw new InvalidOperationException($"Paper run {run.RunId:N} requires a recovered job lease to continue after a rebuild.");
        }

        run.PaperEntriesStoppedAtUtc ??= now;
        var continuation = new PaperExitOnlyContinuation(
            run.RunId, leaseAttempt, run.CodeVersion, executingBuild, now, run.PaperEntriesStoppedAtUtc.Value);
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = stream,
            EventType = PaperExitOnlyContinuation.EventType,
            OccurredAtUtc = now,
            PayloadJson = JsonSerializer.Serialize(continuation, PaperAttemptJson)
        });
        return continuation;
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

    // The original paper start and frozen provenance must match before considering the executing build.
    private static void RequireSamePaperRun(ProductionRun existing, ProductionRun requested)
    {
        ProductionRunPersistence.RequireSameProvenance(existing, requested, requireSameBuild: false);
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
