using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Immutable point-in-time ledger containing provider snapshots and every daily membership
/// decision derived from them. A missing or dangling snapshot is rejected at construction.
/// </summary>
public sealed class UniverseMembershipLedger
{
    private readonly IReadOnlyDictionary<UniverseSnapshotReference, UniverseProviderSnapshot> snapshotsByReference;
    private readonly IReadOnlyDictionary<(DateOnly SessionDate, string Symbol), UniverseMembershipDecision> decisionsBySessionAndSymbol;

    public UniverseMembershipLedger(
        IEnumerable<UniverseProviderSnapshot> snapshots,
        IEnumerable<UniverseMembershipDecision> decisions)
    {
        var snapshotArray = (snapshots ?? throw new ArgumentNullException(nameof(snapshots))).ToArray();
        var snapshotMap = new Dictionary<UniverseSnapshotReference, UniverseProviderSnapshot>();
        foreach (var snapshot in snapshotArray)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!snapshotMap.TryAdd(snapshot.Reference, snapshot))
            {
                throw new ArgumentException(
                    $"Duplicate universe snapshot '{snapshot.Reference.ProviderName}' for {snapshot.Reference.EffectiveSessionDate}.",
                    nameof(snapshots));
            }
        }

        var decisionArray = (decisions ?? throw new ArgumentNullException(nameof(decisions))).ToArray();
        var decisionMap = new Dictionary<(DateOnly SessionDate, string Symbol), UniverseMembershipDecision>();
        foreach (var decision in decisionArray)
        {
            ArgumentNullException.ThrowIfNull(decision);
            if (!snapshotMap.ContainsKey(decision.SourceSnapshot))
            {
                throw new ArgumentException(
                    $"Membership decision for {decision.Security.Symbol} references an unknown provider snapshot.",
                    nameof(decisions));
            }

            var key = (decision.SessionDate, decision.Security.Symbol.ToUpperInvariant());
            if (!decisionMap.TryAdd(key, decision))
            {
                throw new ArgumentException(
                    $"Duplicate membership decision for {decision.Security.Symbol} on {decision.SessionDate}.",
                    nameof(decisions));
            }
        }

        Snapshots = new ReadOnlyCollection<UniverseProviderSnapshot>(snapshotArray);
        Decisions = new ReadOnlyCollection<UniverseMembershipDecision>(decisionArray);
        snapshotsByReference = new ReadOnlyDictionary<UniverseSnapshotReference, UniverseProviderSnapshot>(snapshotMap);
        decisionsBySessionAndSymbol =
            new ReadOnlyDictionary<(DateOnly SessionDate, string Symbol), UniverseMembershipDecision>(decisionMap);
    }

    public IReadOnlyList<UniverseProviderSnapshot> Snapshots { get; }

    public IReadOnlyList<UniverseMembershipDecision> Decisions { get; }

    public bool TryGetSnapshot(
        UniverseSnapshotReference reference,
        out UniverseProviderSnapshot snapshot) =>
        snapshotsByReference.TryGetValue(reference, out snapshot!);

    public IReadOnlyList<UniverseMembershipDecision> GetSessionDecisions(DateOnly sessionDate) =>
        Decisions
            .Where(decision => decision.SessionDate == sessionDate)
            .OrderBy(decision => decision.Rank ?? int.MaxValue)
            .ThenBy(decision => decision.Security.Symbol, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<UniverseMembershipDecision> GetActiveMembers(DateOnly sessionDate) =>
        GetSessionDecisions(sessionDate)
            .Where(decision => decision.IsMember)
            .ToArray();

    public bool TryGetDecision(
        DateOnly sessionDate,
        string symbol,
        out UniverseMembershipDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return decisionsBySessionAndSymbol.TryGetValue(
            (sessionDate, symbol.Trim().ToUpperInvariant()),
            out decision!);
    }
}
