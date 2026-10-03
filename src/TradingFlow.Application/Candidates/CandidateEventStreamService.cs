using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Application.Candidates;

public sealed record CandidateEventBatch(
    long FloorSequence,
    long LatestSequence,
    bool RequiresResync,
    IReadOnlyList<EventStreamEnvelope<CandidateChangedEvent>> Events);

public interface ICandidateEventStreamService
{
    Task<CandidateEventBatch> ReadAsync(
        Guid runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Replays durable candidate invalidations using the same current projection as polling.
/// The immutable decision history remains available in the audit. The service never reevaluates strategy rules and therefore cannot
/// diverge from the state returned by the polling projection.
/// </summary>
public sealed class CandidateEventStreamService(IApplicationEventRepository events, ICandidateQueryService queries) : ICandidateEventStreamService
{
    public async Task<CandidateEventBatch> ReadAsync(
        Guid runId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        var streamName = $"candidate-runs/{runId:N}";
        var bounds = await events.GetBoundsAsync(streamName, cancellationToken);
        var requiresResync = afterSequence > bounds.Latest || (afterSequence > 0 && bounds.Floor > 0 && afterSequence < bounds.Floor - 1);
        if (requiresResync)
        {
            return new CandidateEventBatch(bounds.Floor, bounds.Latest, true, []);
        }

        var records = await events.ListAsync(streamName, afterSequence, Math.Clamp(limit, 1, 100), cancellationToken);
        var projection = records.Count == 0 ? null : await queries.GetRunAsync(runId, cancellationToken);
        var envelopes = new List<EventStreamEnvelope<CandidateChangedEvent>>(records.Count);
        foreach (var item in records)
        {
            CandidateRecord? candidate;
            try
            {
                candidate = JsonSerializer.Deserialize<CandidateRecord>(item.PayloadJson);
            }
            catch (JsonException)
            {
                candidate = null;
            }

            var current = candidate is null ? null : projection?.Candidates.SingleOrDefault(item => item.CandidateId == candidate.CandidateId);
            if (candidate is null || candidate.RunId != runId || current is null)
            {
                return new CandidateEventBatch(bounds.Floor, bounds.Latest, true, []);
            }

            envelopes.Add(new EventStreamEnvelope<CandidateChangedEvent>(
                ContractVersions.VersionOne,
                streamName,
                item.EventSequence,
                item.EventType,
                item.OccurredAtUtc,
                new CandidateChangedEvent(
                    runId,
                    current)));
        }

        return new CandidateEventBatch(bounds.Floor, Math.Max(bounds.Latest, records.LastOrDefault()?.EventSequence ?? 0), false, envelopes);
    }
}
