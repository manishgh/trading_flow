using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteOrderIntentRepository
{
    private static readonly JsonSerializerOptions paperEvidenceJson = new(JsonSerializerDefaults.Web);

    // Only job-owned paper entries cross this fence. The writer transaction excludes
    // concurrent lease changes, entry stops and durable source snapshot updates.
    private static async Task RequirePaperEntryDispatchAsync(
        TradingFlowDbContext context, OrderIntentRecord intent, CancellationToken cancellationToken)
    {
        var ownership = await (
                from job in context.Jobs
                join run in context.ProductionRuns on job.Id equals run.RunId
                where job.Id == intent.RunId && job.JobType == "paper"
                select new
                {
                    job.Status,
                    job.LeaseToken,
                    job.LeaseExpiresAtUtcTicks,
                    job.CancellationRequestedAtUtc,
                    run.PaperJobLeaseToken,
                    run.PaperEntriesStoppedAtUtc
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (ownership is null)
        {
            return;
        }

        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        if (ownership.Status != "running" || ownership.CancellationRequestedAtUtc is not null ||
            ownership.LeaseToken is null || ownership.LeaseToken != ownership.PaperJobLeaseToken ||
            ownership.LeaseExpiresAtUtcTicks is null || ownership.LeaseExpiresAtUtcTicks <= now.UtcTicks)
        {
            throw new PaperEntryDispatchBlockedException(intent.IntentId, "paper_job_lease_inactive",
                "The owning paper job no longer has an active, uncancelled execution lease.");
        }

        if (ownership.PaperEntriesStoppedAtUtc is { } stopped)
        {
            throw new PaperEntryDispatchBlockedException(intent.IntentId, "paper_pipeline_entries_stopped",
                $"The owning run stopped new entries at {stopped:O} after repeated pipeline failures.");
        }

        if (intent.Kind == OrderIntentKind.StrategyEntry)
        {
            await RequireCurrentPaperDiscoveryAsync(context, intent, now, cancellationToken);
        }
    }

    // Recheck the exact aggregate version used by the persisted Triggered
    // decision. A new version requires a decision using that new evidence.
    private static async Task RequireCurrentPaperDiscoveryAsync(
        TradingFlowDbContext context, OrderIntentRecord intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var evidence = await (
                from transition in context.CandidateTransitions
                join candidate in context.Candidates on transition.CandidateId equals candidate.CandidateId
                where candidate.CandidateId == intent.CandidateId && candidate.RunId == intent.RunId &&
                      transition.Sequence == intent.CandidateTriggeredVersion &&
                      transition.NewState == StrategyCandidateState.Triggered
                select new { transition.EvidenceJson, candidate.ExpiresAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (evidence is { } expiring && expiring.ExpiresAtUtc <= now)
        {
            throw new OrderDispatchExpiredException(intent.IntentId, expiring.ExpiresAtUtc);
        }

        var discovery = ReadPaperDiscoveryEvidence(evidence?.EvidenceJson);
        if (discovery is not null)
        {
            var sources = await context.DiscoverySourceMemberships.Where(source =>
                    source.IsActive && source.Aggregate != null && source.Aggregate.IsActive &&
                    source.AggregateId == discovery.AggregateId && source.Aggregate.ScopeId == intent.RunId &&
                    source.Aggregate.Symbol == intent.Symbol && source.Aggregate.Horizon == "swing" &&
                    source.Aggregate.Version == discovery.AggregateVersion &&
                    source.LatestSnapshot != null && !source.LatestSnapshot.IsDiagnostic)
                .Select(source => new { source.LastObservedAtUtc, source.ExpiresAtUtc })
                .ToArrayAsync(cancellationToken);
            // SQLite's DateTimeOffset converter does not support ordered LINQ comparisons.
            if (sources.Any(source => source.LastObservedAtUtc <= now && source.ExpiresAtUtc > now))
            {
                return;
            }
        }

        throw new PaperEntryDispatchBlockedException(intent.IntentId, "paper_discovery_stale_before_broker_post",
            "The discovery membership that triggered the candidate expired, changed, or was removed.");
    }

    // Missing or unreadable legacy proof cannot supply current broker authority.
    private static PaperDiscoveryEvidence? ReadPaperDiscoveryEvidence(string? evidenceJson)
    {
        if (evidenceJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PaperDecisionAudit>(evidenceJson, paperEvidenceJson)?.Discovery;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PaperDecisionAudit(PaperDiscoveryEvidence? Discovery);
    private sealed record PaperDiscoveryEvidence(Guid AggregateId, long AggregateVersion);
}
