using System.Collections.ObjectModel;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Provider-independent candidate scored by a point-in-time universe policy.
/// </summary>
public sealed record UniverseRankingCandidate
{
    public UniverseRankingCandidate(
        UniverseSecurityIdentity security,
        decimal score,
        IEnumerable<string> reasons)
    {
        Security = security ?? throw new ArgumentNullException(nameof(security));
        Score = score;
        var reasonArray = (reasons ?? throw new ArgumentNullException(nameof(reasons)))
            .Select(reason =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(reason);
                return reason.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (reasonArray.Length == 0)
        {
            throw new ArgumentException("At least one ranking reason is required.", nameof(reasons));
        }

        Reasons = new ReadOnlyCollection<string>(reasonArray);
    }

    public UniverseSecurityIdentity Security { get; }

    public decimal Score { get; }

    public IReadOnlyList<string> Reasons { get; }
}

public sealed record RankedUniverseCandidate(
    UniverseRankingCandidate Candidate,
    int Rank);

public sealed record UniverseCandidateExclusion(
    UniverseRankingCandidate Candidate,
    string Reason,
    UniverseSecurityIdentity? RetainedSecurity = null);
