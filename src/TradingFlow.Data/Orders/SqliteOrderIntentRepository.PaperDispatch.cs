using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteOrderIntentRepository
{
    private static readonly JsonSerializerOptions paperEvidenceJson = new(JsonSerializerDefaults.Web);

    // Only job-owned paper entries cross this fence at reservation and first POST. The writer transaction excludes
    // concurrent lease changes, entry stops and durable source snapshot updates.
    private static async Task RequirePaperEntryAuthorityAsync(
        TradingFlowDbContext context, PaperEntryAuthority entry, bool beforePost, CancellationToken cancellationToken)
    {
        var ownership = await (
                from job in context.Jobs
                join owner in context.ProductionRuns on job.Id equals owner.RunId into runs
                from run in runs.DefaultIfEmpty()
                where job.Id == entry.RunId && job.JobType == "paper"
                select new
                {
                    job.Status,
                    job.LeaseToken,
                    job.LeaseExpiresAtUtcTicks,
                    job.CancellationRequestedAtUtc,
                    job.SnapshotJson,
                    PaperJobLeaseToken = run == null ? null : run.PaperJobLeaseToken,
                    PaperEntriesStoppedAtUtc = run == null ? null : run.PaperEntriesStoppedAtUtc
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
            throw new PaperEntryDispatchBlockedException(entry.IntentId, "paper_job_lease_inactive",
                "The owning paper job no longer has an active, uncancelled execution lease.");
        }

        if (ownership.PaperEntriesStoppedAtUtc is { } stopped)
        {
            var stream = PaperExitOnlyContinuation.StreamName(entry.RunId);
            if (await context.ApplicationEvents.AnyAsync(record => record.StreamName == stream &&
                    record.EventType == PaperExitOnlyContinuation.EventType, cancellationToken))
            {
                throw new PaperEntryDispatchBlockedException(entry.IntentId, PaperExitOnlyContinuation.EventType,
                    PaperExitOnlyContinuation.Reason);
            }

            throw new PaperEntryDispatchBlockedException(entry.IntentId, "paper_pipeline_entries_stopped",
                $"The owning run stopped new entries at {stopped:O} after repeated pipeline failures.");
        }

        if (entry.Kind == OrderIntentKind.StrategyEntry)
        {
            await RequireCurrentPaperDiscoveryAsync(context, entry, ownership.SnapshotJson, now, beforePost, cancellationToken);
        }
    }

    // Recheck the exact aggregate version used by the persisted Triggered
    // decision. A new version requires a decision using that new evidence.
    private static async Task RequireCurrentPaperDiscoveryAsync(
        TradingFlowDbContext context, PaperEntryAuthority entry, string? snapshotJson, DateTimeOffset now,
        bool beforePost, CancellationToken cancellationToken)
    {
        var evidence = await (
                from transition in context.CandidateTransitions
                join candidate in context.Candidates on transition.CandidateId equals candidate.CandidateId
                where candidate.CandidateId == entry.CandidateId && candidate.RunId == entry.RunId &&
                      candidate.Symbol == entry.Symbol && transition.RunId == entry.RunId &&
                      transition.Sequence == entry.CandidateTriggeredVersion &&
                      transition.NewState == StrategyCandidateState.Triggered
                select new { transition.EvidenceJson, candidate.ExpiresAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (evidence is { } expiring && expiring.ExpiresAtUtc <= now)
        {
            throw new OrderDispatchExpiredException(entry.IntentId, expiring.ExpiresAtUtc);
        }

        var stage = beforePost ? "before_broker_post" : "before_reservation";
        var refreshHealth = ReadPaperDiscoveryHealth(entry.IntentId, snapshotJson, stage);
        if (refreshHealth?.SessionFailure is not null)
        {
            throw new PaperEntryDispatchBlockedException(entry.IntentId, $"paper_discovery_refresh_failed_{stage}",
                "The paper run could not refresh its discovery membership.");
        }

        var discovery = ReadPaperDiscoveryEvidence(evidence?.EvidenceJson);
        if (discovery is not null)
        {
            var sources = await context.DiscoverySourceMemberships.Where(source =>
                    source.IsActive && source.Aggregate != null && source.Aggregate.IsActive &&
                    source.AggregateId == discovery.AggregateId && source.Aggregate.ScopeId == entry.RunId &&
                    source.Aggregate.Symbol == entry.Symbol && source.Aggregate.Horizon == "swing" &&
                    source.Aggregate.Version == discovery.AggregateVersion &&
                    source.LatestSnapshot != null && !source.LatestSnapshot.IsDiagnostic)
                .Select(source => new { source.SourceKind, source.SourceKey, source.LastObservedAtUtc, source.ExpiresAtUtc })
                .ToArrayAsync(cancellationToken);
            var failedSources = refreshHealth?.FailedSources.Select(source => (source.SourceKind, source.SourceKey)).ToHashSet() ?? new();
            // SQLite's DateTimeOffset converter does not support ordered LINQ comparisons.
            // Older proof omitted source identities. It cannot claim a healthy alternative during degradation.
            var qualifyingSources = sources.Where(source =>
                source.LastObservedAtUtc <= now && source.ExpiresAtUtc > now &&
                (discovery.Sources is not null
                    ? discovery.Sources.Contains($"{source.SourceKind}:{source.SourceKey}", StringComparer.Ordinal)
                    : failedSources.Count == 0)).ToArray();
            if (qualifyingSources.Any(source => !failedSources.Contains((source.SourceKind, source.SourceKey))))
            {
                return;
            }

            if (qualifyingSources.Length > 0 || discovery.Sources is null && failedSources.Count > 0)
            {
                throw new PaperEntryDispatchBlockedException(entry.IntentId, $"paper_discovery_source_refresh_failed_{stage}",
                    "No currently healthy discovery source supports the recorded candidate decision.");
            }
        }

        throw new PaperEntryDispatchBlockedException(entry.IntentId, $"paper_discovery_stale_{stage}",
            "The discovery membership that triggered the candidate expired, changed, or was removed.");
    }

    // Read the source restrictions from the same owned job row as the run lease,
    // within the transaction that reserves risk or records the first submission.
    private static DiscoveryRefreshHealth? ReadPaperDiscoveryHealth(Guid intentId, string? snapshotJson, string stage)
    {
        if (snapshotJson is null)
        {
            return null;
        }

        try
        {
            var health = JsonSerializer.Deserialize<PaperDispatchJobSnapshot>(snapshotJson, paperEvidenceJson)?.PaperPipelineHealth?.Discovery;
            if (health is { FailedSources: null })
            {
                throw new JsonException("Paper discovery health lacks its failed-source list.");
            }
            return health;
        }
        catch (JsonException)
        {
            throw new PaperEntryDispatchBlockedException(intentId, $"paper_discovery_health_unavailable_{stage}",
                "The owning paper job's discovery health cannot be read.");
        }
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

    private sealed record PaperDispatchJobSnapshot(PaperDispatchPipelineHealth? PaperPipelineHealth);
    private sealed record PaperDispatchPipelineHealth(DiscoveryRefreshHealth? Discovery);
    private sealed record PaperDecisionAudit(PaperDiscoveryEvidence? Discovery);
    private sealed record PaperDiscoveryEvidence(Guid AggregateId, long AggregateVersion, IReadOnlyList<string>? Sources);
    private readonly record struct PaperEntryAuthority(
        Guid IntentId, Guid RunId, OrderIntentKind Kind, Guid? CandidateId, int? CandidateTriggeredVersion, string Symbol);
}
