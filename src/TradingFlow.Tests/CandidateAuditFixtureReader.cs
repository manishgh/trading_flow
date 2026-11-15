using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

internal static class CandidateAuditFixtureReader
{
    /// <summary>Projects verified, hydrated observations into the candidate and transition groups used by parity assertions.</summary>
    internal static async Task<IReadOnlyList<BacktestCandidateDecisionAudit>> ReadAsync(BacktestResult result)
    {
        var manifestPath = Assert.IsType<string>(result.ArtifactManifestPath);
        var manifest = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath);
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var publishedPath = BacktestArtifactIntegrity.ResolveArtifactPath(root,
            manifest.Artifacts.Single(item => item.Kind == "candidate_decisions").Path);
        Assert.Equal(publishedPath, Path.GetFullPath(Assert.IsType<string>(result.CandidateDecisionAuditPath)),
            ignoreCase: OperatingSystem.IsWindows());
        var observations = new List<CandidateAuditReader.Observation>();
        await foreach (var observation in CandidateAuditReader.ReadAsync(root, manifest))
            observations.Add(observation);
        return observations.GroupBy(item => item.Candidate.CandidateId)
            .Select(group => new BacktestCandidateDecisionAudit(group.First().Candidate,
                group.Select(item => item.Transition).OfType<CandidateTransitionRecord>().ToArray()))
            .ToArray();
    }
}
