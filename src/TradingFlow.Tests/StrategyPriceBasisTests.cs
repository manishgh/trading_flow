using System.Collections.Immutable;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class StrategyPriceBasisTests
{
    /// <summary>Actual bars, snapshots and evidence must use the basis bound to the hash-verified admission timing profile.</summary>
    [Theory]
    [InlineData("completed-bar-next-slice-market-exits-v3", "all", true)]
    [InlineData("completed-bar-next-slice-market-exits-v3", "raw", false)]
    [InlineData("completed-bar-precommitted-terminal-open-v4", "raw", true)]
    [InlineData("completed-bar-precommitted-terminal-open-v4", "all", false)]
    [InlineData("completed-bar-precommitted-terminal-owned-v5", "raw", true)]
    [InlineData("completed-bar-precommitted-terminal-owned-v5", "all", false)]
    public void ExactAdmission_RequiresMatchingActualPriceBasis(string policy, string basis, bool accepted)
    {
        var request = StrategyDecisionKernelTests.CreateRequest();
        request = request with { AdmissionProfile = request.AdmissionProfile with { ExecutionTimingPolicy = policy } };
        request = StrategyDecisionKernelTests.RebindStrategy(request, request.Strategy);
        request = request with {
            BarsByTimeframe = request.BarsByTimeframe.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value.Select(bar => bar with { AdjustmentPolicy = basis }).ToImmutableArray()),
            SnapshotsByTimeframe = request.SnapshotsByTimeframe.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value.Select(snapshot => snapshot with { AdjustmentPolicy = basis }).ToImmutableArray()),
            Candidate = request.Candidate with { MarketState = request.Candidate.MarketState with { AdjustmentPolicy = basis } }
        };
        if (accepted)
            Assert.Equal(TradingFlow.Domain.Strategies.StrategyCandidateState.Triggered, new StrategyDecisionKernel().Evaluate(request).State);
        else
            Assert.Contains("Authoritative warm market evidence", Assert.Throws<InvalidOperationException>(() => new StrategyDecisionKernel().Evaluate(request)).Message);
    }
}
