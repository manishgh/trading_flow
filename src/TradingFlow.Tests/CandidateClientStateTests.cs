using TradingFlow.Contracts.Transport;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Tests;

public sealed class CandidateClientStateTests
{
    [Fact]
    public void LatePreviousRunAndOlderSnapshotCannotReplaceCurrentSelection()
    {
        var store = new CandidateStateStore();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var oldGeneration = store.Select(first);
        var generation = store.Select(second);
        Assert.False(store.Replace(oldGeneration, Snapshot(first, 20)));
        Assert.True(store.Replace(generation, Snapshot(second, 30)));
        Assert.False(store.Replace(generation, Snapshot(second, 29)));
        Assert.Equal(30, store.Cursor);
    }

    [Fact]
    public void ExecutionInvalidationChangesReadinessEvenWhenCandidateVersionDoesNotChange()
    {
        var store = new CandidateStateStore();
        var run = Guid.NewGuid(); var generation = store.Select(run);
        var initial = Snapshot(run, 1);
        Assert.True(store.Replace(generation, initial));
        var candidate = initial.Candidates[0] with { Readiness = "in_trade" };
        Assert.True(store.Apply(generation, new(ContractVersions.VersionOne, $"candidate-runs/{run:N}", 2, "candidate.changed",
            DateTimeOffset.UtcNow, new(run, candidate))));
        Assert.Equal("in_trade", Assert.Single(store.Candidates).Readiness);
        Assert.False(store.Apply(generation, new(ContractVersions.VersionOne, $"candidate-runs/{run:N}", 1, "candidate.changed",
            DateTimeOffset.UtcNow, new(run, initial.Candidates[0]))));
    }

    private static CandidateRunResponse Snapshot(Guid run, long cursor) => new(ContractVersions.VersionOne, run,
        new("paper_shadow", Guid.NewGuid(), null, []), "running", DateTimeOffset.UtcNow, null, null, cursor,
        [new(Guid.NewGuid(), 1, "MSFT", new("swing", "1.0.0", new string('a',64)), "consumed", "consumed", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), null, null, null, [])]);
}
