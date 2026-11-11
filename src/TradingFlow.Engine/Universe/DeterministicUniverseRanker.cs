using System.Collections.ObjectModel;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Produces stable ranks after issuer/share-class deduplication. Equal scores are resolved by
/// ordinal security identity, making the result reproducible across processes and input order.
/// By default equal scores go to the lower symbol; <paramref name="tiesByIssuer"/> gives them to the lower issuer
/// identifier instead, which does not change when a company changes its ticker.
/// </summary>
public sealed class DeterministicUniverseRanker(
    IssuerShareClassDeduplicator? deduplicator = null,
    bool tiesByIssuer = false)
{
    private readonly IssuerShareClassDeduplicator deduplicator =
        deduplicator ?? new IssuerShareClassDeduplicator();

    /// <summary>Deduplicates issuers, orders by score then the configured identity tie-break, and keeps the first members.</summary>
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
        var byScore = deduplicated.Retained.OrderByDescending(candidate => candidate.Score);
        var ordered = (tiesByIssuer
                ? byScore.ThenBy(candidate => candidate.Security.IssuerId, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Security.Symbol, StringComparer.Ordinal)
                : byScore.ThenBy(candidate => candidate.Security.Symbol, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Security.IssuerId, StringComparer.Ordinal))
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
