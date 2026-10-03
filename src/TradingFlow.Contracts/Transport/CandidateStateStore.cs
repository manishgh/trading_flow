using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

/// <summary>Applies one run's server projection without inventing readiness or allowing a late prior selection to overwrite it.</summary>
public sealed class CandidateStateStore
{
    private readonly Dictionary<Guid, CandidateStateResponse> candidates = new();
    public Guid RunId { get; private set; }
    public long Generation { get; private set; }
    public long Cursor { get; private set; }
    public IReadOnlyCollection<CandidateStateResponse> Candidates => candidates.Values;
    public long Select(Guid runId) { RunId = runId; Generation++; Cursor = 0; candidates.Clear(); return Generation; }
    public bool Replace(long generation, CandidateRunResponse snapshot)
    {
        if (generation != Generation || snapshot.CandidateRunId != RunId || snapshot.LatestEventSequence < Cursor) return false;
        candidates.Clear();
        foreach (var item in snapshot.Candidates) candidates[item.CandidateId] = item;
        Cursor = snapshot.LatestEventSequence;
        return true;
    }
    public bool Apply(long generation, EventStreamEnvelope<CandidateChangedEvent> item)
    {
        if (generation != Generation || item.Payload.CandidateRunId != RunId || item.EventSequence <= Cursor) return false;
        if (candidates.TryGetValue(item.Payload.Candidate.CandidateId, out var previous) && previous.Version > item.Payload.Candidate.Version) return false;
        candidates[item.Payload.Candidate.CandidateId] = item.Payload.Candidate;
        Cursor = item.EventSequence;
        return true;
    }
}
