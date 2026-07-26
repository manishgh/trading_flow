using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// One research session that must be fully represented in the provenance ledger before a
/// strategy result is eligible for promotion.
/// </summary>
public sealed record UniversePromotionSessionEvidence
{
    public UniversePromotionSessionEvidence(
        DateOnly sessionDate,
        DateTimeOffset decisionCutoffUtc,
        int expectedDecisionCount)
    {
        UniverseProviderSnapshot.EnsureUtc(decisionCutoffUtc, nameof(decisionCutoffUtc));
        if (expectedDecisionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedDecisionCount),
                "Expected decision count must be positive.");
        }

        SessionDate = sessionDate;
        DecisionCutoffUtc = decisionCutoffUtc;
        ExpectedDecisionCount = expectedDecisionCount;
    }

    public DateOnly SessionDate { get; }

    public DateTimeOffset DecisionCutoffUtc { get; }

    public int ExpectedDecisionCount { get; }
}

/// <summary>
/// Complete universe evidence supplied to the promotion validator.
/// Ledger is nullable intentionally: missing provenance must produce an ineligible result,
/// not a null-reference failure or an implicit pass.
/// </summary>
public sealed class UniversePromotionEvidence
{
    public UniversePromotionEvidence(
        string researchRunId,
        IEnumerable<UniversePromotionSessionEvidence> requiredSessions,
        UniverseMembershipLedger? ledger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(researchRunId);
        ResearchRunId = researchRunId.Trim();
        var sessions = (requiredSessions ?? throw new ArgumentNullException(nameof(requiredSessions))).ToArray();
        RequiredSessions = new ReadOnlyCollection<UniversePromotionSessionEvidence>(sessions);
        Ledger = ledger;
    }

    public string ResearchRunId { get; }

    public IReadOnlyList<UniversePromotionSessionEvidence> RequiredSessions { get; }

    public UniverseMembershipLedger? Ledger { get; }
}

public sealed record UniversePromotionEligibility
{
    public UniversePromotionEligibility(bool isEligible, IEnumerable<string> failures)
    {
        var failureArray = (failures ?? throw new ArgumentNullException(nameof(failures)))
            .Select(failure =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(failure);
                return failure.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(failure => failure, StringComparer.Ordinal)
            .ToArray();

        if (isEligible && failureArray.Length > 0)
        {
            throw new ArgumentException("Eligible promotion evidence cannot contain failures.", nameof(failures));
        }

        if (!isEligible && failureArray.Length == 0)
        {
            throw new ArgumentException("Ineligible promotion evidence must explain why it failed.", nameof(failures));
        }

        IsEligible = isEligible;
        Failures = new ReadOnlyCollection<string>(failureArray);
    }

    public bool IsEligible { get; }

    public IReadOnlyList<string> Failures { get; }
}
