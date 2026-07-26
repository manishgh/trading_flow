using System.Collections.ObjectModel;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Produces stable ranks after issuer/share-class deduplication. Equal scores are resolved by
/// ordinal security identity, making the result reproducible across processes and input order.
/// </summary>
public sealed class DeterministicUniverseRanker(
    IssuerShareClassDeduplicator? deduplicator = null)
{
    private readonly IssuerShareClassDeduplicator deduplicator =
        deduplicator ?? new IssuerShareClassDeduplicator();

    public DeterministicUniverseRanking Rank(
        IEnumerable<UniverseRankingCandidate> candidates,
        int? maximumMembers = null)
    {
        if (maximumMembers is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMembers),
                "Maximum members must be positive when specified.");
        }

        var deduplicated = deduplicator.Deduplicate(candidates);
        var ordered = deduplicated.Retained
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Security.Symbol, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Security.IssuerId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Security.ShareClassId, StringComparer.Ordinal)
            .ToArray();

        var selectedCount = maximumMembers is null
            ? ordered.Length
            : Math.Min(maximumMembers.Value, ordered.Length);
        var ranked = ordered
            .Take(selectedCount)
            .Select((candidate, index) => new RankedUniverseCandidate(candidate, index + 1))
            .ToArray();
        var exclusions = deduplicated.Exclusions
            .Concat(ordered
                .Skip(selectedCount)
                .Select(candidate => new UniverseCandidateExclusion(candidate, "outside_member_limit")))
            .OrderBy(exclusion => exclusion.Candidate.Security.Symbol, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.Reason, StringComparer.Ordinal)
            .ToArray();

        return new DeterministicUniverseRanking(ranked, exclusions);
    }
}

public sealed class DeterministicUniverseRanking
{
    public DeterministicUniverseRanking(
        IEnumerable<RankedUniverseCandidate> ranked,
        IEnumerable<UniverseCandidateExclusion> exclusions)
    {
        Ranked = new ReadOnlyCollection<RankedUniverseCandidate>(ranked.ToArray());
        Exclusions = new ReadOnlyCollection<UniverseCandidateExclusion>(exclusions.ToArray());
    }

    public IReadOnlyList<RankedUniverseCandidate> Ranked { get; }

    public IReadOnlyList<UniverseCandidateExclusion> Exclusions { get; }
}
