using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Engine.Universe;

/// <summary>
/// Fails strategy promotion closed unless every required research session has timely provider
/// evidence, a complete membership ledger, valid snapshot references, deterministic contiguous
/// ranks, and no duplicate issuer exposure.
/// </summary>
public sealed class UniversePromotionEligibilityValidator
{
    public UniversePromotionEligibility Validate(UniversePromotionEvidence? evidence)
    {
        if (evidence is null)
        {
            return Ineligible("point_in_time_universe_evidence_missing");
        }

        var failures = new List<string>();
        if (evidence.RequiredSessions.Count == 0)
        {
            failures.Add("required_research_sessions_missing");
        }

        var duplicateSessions = evidence.RequiredSessions
            .GroupBy(session => session.SessionDate)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(date => date)
            .ToArray();
        failures.AddRange(duplicateSessions.Select(date => $"duplicate_required_session:{date:yyyy-MM-dd}"));

        if (evidence.Ledger is null)
        {
            failures.Add("point_in_time_universe_ledger_missing");
            return BuildResult(failures);
        }

        foreach (var requiredSession in evidence.RequiredSessions
                     .GroupBy(session => session.SessionDate)
                     .Select(group => group.First())
                     .OrderBy(session => session.SessionDate))
        {
            ValidateSession(evidence.Ledger, requiredSession, failures);
        }

        return BuildResult(failures);
    }

    private static void ValidateSession(
        UniverseMembershipLedger ledger,
        UniversePromotionSessionEvidence requiredSession,
        ICollection<string> failures)
    {
        var sessionLabel = requiredSession.SessionDate.ToString("yyyy-MM-dd");
        var timelySnapshots = ledger.Snapshots
            .Where(snapshot =>
                snapshot.EffectiveSessionDate == requiredSession.SessionDate &&
                snapshot.ObservedAtUtc <= requiredSession.DecisionCutoffUtc)
            .ToArray();
        if (timelySnapshots.Length == 0)
        {
            failures.Add($"timely_provider_snapshot_missing:{sessionLabel}");
        }

        var decisions = ledger.GetSessionDecisions(requiredSession.SessionDate);
        if (decisions.Count != requiredSession.ExpectedDecisionCount)
        {
            failures.Add(
                $"membership_decision_count_mismatch:{sessionLabel}:expected={requiredSession.ExpectedDecisionCount}:actual={decisions.Count}");
        }

        foreach (var decision in decisions)
        {
            if (!ledger.TryGetSnapshot(decision.SourceSnapshot, out var snapshot))
            {
                failures.Add($"membership_snapshot_reference_missing:{sessionLabel}:{decision.Security.Symbol}");
                continue;
            }

            if (snapshot.EffectiveSessionDate != requiredSession.SessionDate)
            {
                failures.Add($"membership_snapshot_session_mismatch:{sessionLabel}:{decision.Security.Symbol}");
            }

            if (snapshot.ObservedAtUtc > requiredSession.DecisionCutoffUtc)
            {
                failures.Add($"membership_snapshot_observed_after_cutoff:{sessionLabel}:{decision.Security.Symbol}");
            }
        }

        var activeMembers = decisions.Where(decision => decision.IsMember).ToArray();
        var actualRanks = activeMembers
            .Select(decision => decision.Rank!.Value)
            .OrderBy(rank => rank)
            .ToArray();
        var expectedRanks = Enumerable.Range(1, activeMembers.Length).ToArray();
        if (!actualRanks.SequenceEqual(expectedRanks))
        {
            failures.Add($"active_member_ranks_not_contiguous:{sessionLabel}");
        }

        var duplicateIssuers = activeMembers
            .GroupBy(decision => decision.Security.IssuerId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(issuerId => issuerId, StringComparer.Ordinal)
            .ToArray();
        foreach (var issuerId in duplicateIssuers)
        {
            failures.Add($"duplicate_issuer_membership:{sessionLabel}:{issuerId}");
        }
    }

    private static UniversePromotionEligibility BuildResult(IEnumerable<string> failures)
    {
        var failureArray = failures.Distinct(StringComparer.Ordinal).ToArray();
        return failureArray.Length == 0
            ? new UniversePromotionEligibility(true, Array.Empty<string>())
            : new UniversePromotionEligibility(false, failureArray);
    }

    private static UniversePromotionEligibility Ineligible(string failure) =>
        new(false, new[] { failure });
}
