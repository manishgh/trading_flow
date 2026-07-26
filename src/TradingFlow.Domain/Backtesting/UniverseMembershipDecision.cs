using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Auditable decision for one security on one trading session.
/// Included members must have a positive deterministic rank; excluded candidates remain in
/// the ledger with reasons so absence from the universe is explainable.
/// </summary>
public sealed record UniverseMembershipDecision
{
    public UniverseMembershipDecision(
        DateOnly sessionDate,
        UniverseSecurityIdentity security,
        bool isMember,
        int? rank,
        decimal? score,
        IEnumerable<string> reasons,
        UniverseSnapshotReference sourceSnapshot)
    {
        Security = security ?? throw new ArgumentNullException(nameof(security));
        SourceSnapshot = sourceSnapshot ?? throw new ArgumentNullException(nameof(sourceSnapshot));
        if (sourceSnapshot.EffectiveSessionDate != sessionDate)
        {
            throw new ArgumentException(
                "A membership decision must reference a snapshot effective for the same session.",
                nameof(sourceSnapshot));
        }

        if (isMember && (rank is null || rank <= 0))
        {
            throw new ArgumentException("Included universe members require a positive rank.", nameof(rank));
        }

        if (!isMember && rank is not null)
        {
            throw new ArgumentException("Excluded universe candidates cannot have an active-member rank.", nameof(rank));
        }

        var normalizedReasons = (reasons ?? throw new ArgumentNullException(nameof(reasons)))
            .Select(reason =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(reason);
                return reason.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedReasons.Length == 0)
        {
            throw new ArgumentException("At least one membership reason is required.", nameof(reasons));
        }

        SessionDate = sessionDate;
        IsMember = isMember;
        Rank = rank;
        Score = score;
        Reasons = new ReadOnlyCollection<string>(normalizedReasons);
    }

    public DateOnly SessionDate { get; }

    public UniverseSecurityIdentity Security { get; }

    public bool IsMember { get; }

    public int? Rank { get; }

    public decimal? Score { get; }

    public IReadOnlyList<string> Reasons { get; }

    public UniverseSnapshotReference SourceSnapshot { get; }
}
