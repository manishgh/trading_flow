using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

public sealed class PointInTimeUniverseProvenanceTests
{
    private static readonly DateOnly SessionDate = new(2026, 7, 24);
    private static readonly DateTimeOffset DecisionCutoffUtc =
        new(2026, 7, 24, 13, 25, 0, TimeSpan.Zero);

    [Fact]
    public void ProviderSnapshot_RequiresUtcObservationAndSha256ContentHash()
    {
        Assert.Throws<ArgumentException>(() => new UniverseProviderSnapshot(
            "finviz",
            new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.FromHours(2)),
            SessionDate,
            "v=111",
            Hash('a')));

        Assert.Throws<ArgumentException>(() => new UniverseProviderSnapshot(
            "finviz",
            DecisionCutoffUtc,
            SessionDate,
            "v=111",
            "not-a-sha256-hash"));
    }

    [Fact]
    public void MembershipLedger_DefensivelyCopiesInputsAndRejectsDanglingSnapshotReferences()
    {
        var snapshot = Snapshot("finviz", Hash('a'), DecisionCutoffUtc.AddMinutes(-5));
        var reasons = new List<string> { "passed_liquidity_gate" };
        var decision = new UniverseMembershipDecision(
            SessionDate,
            Security("AAPL", "issuer-apple", "common"),
            true,
            1,
            100m,
            reasons,
            snapshot.Reference);
        var snapshots = new List<UniverseProviderSnapshot> { snapshot };
        var decisions = new List<UniverseMembershipDecision> { decision };

        var ledger = new UniverseMembershipLedger(snapshots, decisions);
        snapshots.Clear();
        decisions.Clear();
        reasons.Clear();

        Assert.Single(ledger.Snapshots);
        Assert.Single(ledger.Decisions);
        Assert.Equal("passed_liquidity_gate", Assert.Single(ledger.Decisions[0].Reasons));

        var unknownReference = new UniverseSnapshotReference("alpaca", SessionDate, Hash('b'));
        var danglingDecision = new UniverseMembershipDecision(
            SessionDate,
            Security("MSFT", "issuer-microsoft", "common"),
            false,
            null,
            50m,
            new[] { "below_rank_cutoff" },
            unknownReference);

        Assert.Throws<ArgumentException>(() =>
            new UniverseMembershipLedger(new[] { snapshot }, new[] { danglingDecision }));
    }

    [Fact]
    public void Ranker_IsInputOrderIndependentAndDeduplicatesIssuerShareClasses()
    {
        var candidates = new[]
        {
            Candidate("GOOGL", "issuer-google", "class-c", 90m),
            Candidate("MSFT", "issuer-microsoft", "common", 90m),
            Candidate("AAPL", "issuer-apple", "common", 95m),
            Candidate("GOOG", "issuer-google", "class-a", 90m)
        };
        var ranker = new DeterministicUniverseRanker();

        var forward = ranker.Rank(candidates);
        var reverse = ranker.Rank(candidates.Reverse());

        Assert.Equal(
            new[] { "AAPL", "GOOG", "MSFT" },
            forward.Ranked.Select(item => item.Candidate.Security.Symbol));
        Assert.Equal(
            forward.Ranked.Select(item => (item.Candidate.Security.Symbol, item.Rank)),
            reverse.Ranked.Select(item => (item.Candidate.Security.Symbol, item.Rank)));
        var duplicate = Assert.Single(forward.Exclusions);
        Assert.Equal("GOOGL", duplicate.Candidate.Security.Symbol);
        Assert.Equal("GOOG", duplicate.RetainedSecurity!.Symbol);
        Assert.Equal("duplicate_issuer_share_class", duplicate.Reason);
    }

    [Fact]
    public void Ranker_PrefersHigherScoredShareClassAndAppliesMemberLimitDeterministically()
    {
        var ranking = new DeterministicUniverseRanker().Rank(
            new[]
            {
                Candidate("GOOG", "issuer-google", "class-a", 80m),
                Candidate("GOOGL", "issuer-google", "class-c", 91m),
                Candidate("AAPL", "issuer-apple", "common", 95m),
                Candidate("MSFT", "issuer-microsoft", "common", 90m)
            },
            maximumMembers: 2);

        Assert.Equal(
            new[] { "AAPL", "GOOGL" },
            ranking.Ranked.Select(item => item.Candidate.Security.Symbol));
        Assert.Contains(ranking.Exclusions, exclusion =>
            exclusion.Candidate.Security.Symbol == "GOOG" &&
            exclusion.Reason == "duplicate_issuer_share_class");
        Assert.Contains(ranking.Exclusions, exclusion =>
            exclusion.Candidate.Security.Symbol == "MSFT" &&
            exclusion.Reason == "outside_member_limit");
    }

    [Fact]
    public void PromotionValidator_FailsClosedWhenEvidenceOrLedgerIsMissing()
    {
        var validator = new UniversePromotionEligibilityValidator();

        var missingEvidence = validator.Validate(null);
        Assert.False(missingEvidence.IsEligible);
        Assert.Contains("point_in_time_universe_evidence_missing", missingEvidence.Failures);

        var missingLedger = validator.Validate(new UniversePromotionEvidence(
            "research-run",
            new[] { RequiredSession(expectedDecisionCount: 1) },
            ledger: null));
        Assert.False(missingLedger.IsEligible);
        Assert.Contains("point_in_time_universe_ledger_missing", missingLedger.Failures);
    }

    [Fact]
    public void PromotionValidator_AcceptsCompleteTimelyPointInTimeLedger()
    {
        var snapshot = Snapshot("finviz", Hash('a'), DecisionCutoffUtc.AddMinutes(-5));
        var decisions = new[]
        {
            Included("AAPL", "issuer-apple", 1, 95m, snapshot.Reference),
            Included("MSFT", "issuer-microsoft", 2, 90m, snapshot.Reference),
            Excluded("GOOG", "issuer-google", 88m, snapshot.Reference)
        };
        var evidence = new UniversePromotionEvidence(
            "research-run",
            new[] { RequiredSession(decisions.Length) },
            new UniverseMembershipLedger(new[] { snapshot }, decisions));

        var result = new UniversePromotionEligibilityValidator().Validate(evidence);

        Assert.True(result.IsEligible);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void PromotionValidator_RejectsLateSnapshotAndIncompleteDecisionLedger()
    {
        var lateSnapshot = Snapshot("finviz", Hash('a'), DecisionCutoffUtc.AddSeconds(1));
        var decision = Included("AAPL", "issuer-apple", 1, 95m, lateSnapshot.Reference);
        var evidence = new UniversePromotionEvidence(
            "research-run",
            new[] { RequiredSession(expectedDecisionCount: 2) },
            new UniverseMembershipLedger(new[] { lateSnapshot }, new[] { decision }));

        var result = new UniversePromotionEligibilityValidator().Validate(evidence);

        Assert.False(result.IsEligible);
        Assert.Contains($"timely_provider_snapshot_missing:{SessionDate:yyyy-MM-dd}", result.Failures);
        Assert.Contains(result.Failures, failure => failure.StartsWith(
            $"membership_decision_count_mismatch:{SessionDate:yyyy-MM-dd}",
            StringComparison.Ordinal));
        Assert.Contains(
            $"membership_snapshot_observed_after_cutoff:{SessionDate:yyyy-MM-dd}:AAPL",
            result.Failures);
    }

    [Fact]
    public void PromotionValidator_RejectsRankGapsAndDuplicateIssuerMembership()
    {
        var snapshot = Snapshot("finviz", Hash('a'), DecisionCutoffUtc.AddMinutes(-5));
        var decisions = new[]
        {
            Included("GOOG", "issuer-google", 1, 95m, snapshot.Reference),
            Included("GOOGL", "issuer-google", 3, 90m, snapshot.Reference)
        };
        var evidence = new UniversePromotionEvidence(
            "research-run",
            new[] { RequiredSession(decisions.Length) },
            new UniverseMembershipLedger(new[] { snapshot }, decisions));

        var result = new UniversePromotionEligibilityValidator().Validate(evidence);

        Assert.False(result.IsEligible);
        Assert.Contains($"active_member_ranks_not_contiguous:{SessionDate:yyyy-MM-dd}", result.Failures);
        Assert.Contains($"duplicate_issuer_membership:{SessionDate:yyyy-MM-dd}:issuer-google", result.Failures);
    }

    private static UniverseProviderSnapshot Snapshot(
        string provider,
        string hash,
        DateTimeOffset observedAtUtc) =>
        new(provider, observedAtUtc, SessionDate, "v=111&f=cap_mega", hash);

    private static UniversePromotionSessionEvidence RequiredSession(int expectedDecisionCount) =>
        new(SessionDate, DecisionCutoffUtc, expectedDecisionCount);

    private static UniverseSecurityIdentity Security(
        string symbol,
        string issuerId,
        string shareClassId) =>
        new(symbol, issuerId, shareClassId);

    private static UniverseRankingCandidate Candidate(
        string symbol,
        string issuerId,
        string shareClassId,
        decimal score) =>
        new(Security(symbol, issuerId, shareClassId), score, new[] { "point_in_time_score" });

    private static UniverseMembershipDecision Included(
        string symbol,
        string issuerId,
        int rank,
        decimal score,
        UniverseSnapshotReference snapshot) =>
        new(
            SessionDate,
            Security(symbol, issuerId, "common"),
            true,
            rank,
            score,
            new[] { "selected_by_rank" },
            snapshot);

    private static UniverseMembershipDecision Excluded(
        string symbol,
        string issuerId,
        decimal score,
        UniverseSnapshotReference snapshot) =>
        new(
            SessionDate,
            Security(symbol, issuerId, "common"),
            false,
            null,
            score,
            new[] { "outside_member_limit" },
            snapshot);

    private static string Hash(char value) => new(value, 64);
}
