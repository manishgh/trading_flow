using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Web.Services;

/// <summary>
/// Shared publication-marker check for the backtest, optimization and paper job services. Each projection refresh
/// (about once a second) asks it to confirm every completed job's marker. A stored result path recorded before the
/// data folder moved is read from the configured data folder. A failure is logged once per job and again only when its
/// reason changes; a later success is logged once. Retrying cannot recreate a missing file, so repeating the same
/// warning every refresh only fills the log. Memory holds one short entry per currently failing job.
/// </summary>
public sealed class DurableResultMarkers(string jobLabel, ILogger logger, ProjectPaths? paths)
{
    private readonly ConcurrentDictionary<Guid, string> failures = new();

    /// <summary>Confirms or writes the marker for one job; never throws except for caller cancellation.</summary>
    public async Task PublishAsync(PersistedJob job, CancellationToken cancellationToken)
    {
        var located = Locate(job.ResultReference);
        try
        {
            await DurableJobResultPublication.PublishIfCompletedAsync(job, located, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure(job.Id, located, exception);
            return;
        }

        if (failures.TryRemove(job.Id, out _))
            logger.LogInformation(
                "Durable {JobLabel} {JobId} result marker check no longer fails at {ResultReference}.",
                jobLabel, job.Id, located);
    }

    /// <summary>Where a stored result reference is read today; unchanged when no path mapping is configured.</summary>
    public string? Locate(string? storedReference) =>
        storedReference is null || paths is null ? storedReference : paths.LocateStored(storedReference);

    // Logs only a new or changed reason for this job, so an unchanged missing file is reported once per process. The
    // reason is the exception type and the located path, never the message: a failed marker write names a temporary
    // file with a new random identifier on every attempt, which would otherwise count as a new reason each refresh.
    private void RecordFailure(Guid jobId, string? located, Exception exception)
    {
        var reason = $"{exception.GetType().FullName} 0x{exception.HResult:X8} at {located}";
        // Exactly one caller wins the add or the swap, so a refresh and a terminal publication failing together log once.
        while (!failures.TryAdd(jobId, reason))
        {
            if (!failures.TryGetValue(jobId, out var previous)) continue;
            if (previous == reason) return;
            if (failures.TryUpdate(jobId, reason, previous)) break;
        }

        logger.LogWarning(
            exception,
            "Durable {JobLabel} {JobId} result marker could not be confirmed at {ResultReference}. The job record stays visible. Retrying cannot recreate a missing file; later refreshes retry quietly and log again only if the reason changes.",
            jobLabel, jobId, located);
    }
}
