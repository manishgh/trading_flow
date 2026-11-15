using System.Text.Json;
using TradingFlow.Domain.Optimization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Backtesting.Artifacts;

public static class OptimizationPublication
{
    /// <summary>Reads the winning summary's receipts from the worker that verified every variant before publication.</summary>
    public static async Task<ResearchPublicationEvidence> ReadRecordedAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        var summary = JsonSerializer.Deserialize<OptimizationResult>(bytes)
            ?? throw new InvalidDataException("The optimization summary is empty.");
        return new(path, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Outcomes(summary));
    }

    /// <summary>Rechecks all exact variant artifacts on explicit replay without evaluating any permutation.</summary>
    public static async Task VerifyRankedAndUnrankedResultsAsync(OptimizationResult result, CancellationToken token)
    {
        _ = Outcomes(result);
        foreach (var permutation in result.FrozenPermutationEvidence!)
        {
            if (ResearchStartIdentity.HashFile(permutation.ResultPath) != permutation.ResultSha256)
                throw new InvalidDataException("A frozen optimization permutation result differs from its recorded SHA-256.");
            await BacktestResearchPublication.VerifyAsync(permutation.ResultPath, token, expected: permutation.Publication);
        }
    }

    /// <summary>Projects every declared permutation with exact result hashes, including results omitted from TopRuns.</summary>
    public static IReadOnlyList<ResearchVariantOutcome> Outcomes(OptimizationResult result)
    {
        var permutationEvidence = result.FrozenPermutationEvidence;
        if (permutationEvidence is null || permutationEvidence.Count != result.TotalPermutations ||
            permutationEvidence.Select(item => item.VariantKey).Distinct(StringComparer.Ordinal).Count() != result.TotalPermutations)
            throw new InvalidDataException("The optimization summary does not retain evidence for every frozen permutation.");
        return permutationEvidence.Select(item => new ResearchVariantOutcome(item.VariantKey, result.Metric, item.MetricValue,
            item.ResultSha256, item.Publication.ManifestSha256)).ToArray();
    }
}
