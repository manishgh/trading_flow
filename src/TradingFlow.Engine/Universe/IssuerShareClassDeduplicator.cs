using System.Collections.ObjectModel;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Retains at most one listed share class per issuer. The highest score wins; ties use
/// ordinal symbol, share-class, and security-key ordering so input order cannot affect output.
/// </summary>
public sealed class IssuerShareClassDeduplicator
{
    public IssuerShareClassDeduplicationResult Deduplicate(
        IEnumerable<UniverseRankingCandidate> candidates)
    {
        var candidateArray = (candidates ?? throw new ArgumentNullException(nameof(candidates))).ToArray();
        if (candidateArray.Any(candidate => candidate is null))
        {
            throw new ArgumentException("Universe candidates cannot contain null values.", nameof(candidates));
        }

        var retained = new List<UniverseRankingCandidate>();
        var exclusions = new List<UniverseCandidateExclusion>();

        foreach (var issuerGroup in candidateArray
                     .GroupBy(candidate => candidate.Security.IssuerId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = issuerGroup
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Security.Symbol, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Security.ShareClassId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Security.SecurityKey, StringComparer.Ordinal)
                .ToArray();

            var winner = ordered[0];
            retained.Add(winner);
            foreach (var duplicate in ordered.Skip(1))
            {
                exclusions.Add(new UniverseCandidateExclusion(
                    duplicate,
                    "duplicate_issuer_share_class",
                    winner.Security));
            }
        }

        var orderedRetained = retained
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Security.Symbol, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Security.IssuerId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Security.ShareClassId, StringComparer.Ordinal)
            .ToArray();
        var orderedExclusions = exclusions
            .OrderBy(exclusion => exclusion.Candidate.Security.Symbol, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.Candidate.Security.IssuerId, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.Candidate.Security.ShareClassId, StringComparer.Ordinal)
            .ToArray();

        return new IssuerShareClassDeduplicationResult(orderedRetained, orderedExclusions);
    }
}

public sealed class IssuerShareClassDeduplicationResult
{
    public IssuerShareClassDeduplicationResult(
        IEnumerable<UniverseRankingCandidate> retained,
        IEnumerable<UniverseCandidateExclusion> exclusions)
    {
        Retained = new ReadOnlyCollection<UniverseRankingCandidate>(retained.ToArray());
        Exclusions = new ReadOnlyCollection<UniverseCandidateExclusion>(exclusions.ToArray());
    }

    public IReadOnlyList<UniverseRankingCandidate> Retained { get; }

    public IReadOnlyList<UniverseCandidateExclusion> Exclusions { get; }
}
