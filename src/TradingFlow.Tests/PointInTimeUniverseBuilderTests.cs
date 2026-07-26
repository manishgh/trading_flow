using TradingFlow.Domain.Research;
using TradingFlow.Research.Universe;

namespace TradingFlow.Tests;

public sealed class PointInTimeUniverseBuilderTests
{
    private static readonly DateOnly SessionDate = new(2026, 7, 24);
    private static readonly DateTimeOffset DecisionUtc =
        new(2026, 7, 24, 13, 25, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SnapshotProviderUtc = DecisionUtc.AddMinutes(-10);
    private static readonly DateTimeOffset SnapshotReceivedUtc = DecisionUtc.AddMinutes(-9);
    private static readonly DateTimeOffset IdentityObservedUtc = DecisionUtc.AddDays(-1);

    [Fact]
    public void Build_BlocksSnapshotReceivedAfterDecisionWithoutPartialMembership()
    {
        var result = Build(Request(
            [Candidate("AAPL", 100m)],
            [Security("AAPL", "security-aapl", "issuer-apple")],
            [Interval("AAPL", "security-aapl", "issuer-apple")],
            snapshotReceivedAtUtc: DecisionUtc.AddSeconds(1)));

        Assert.False(result.Readiness.IsReady);
        Assert.Empty(result.Memberships);
        AssertFailure(
            result,
            PointInTimeUniverseReadinessFailureCode.UniverseSnapshotReceivedAfterDecision);
    }

    [Fact]
    public void Build_CurrentSnapshotCannotEstablishHistoricalMembershipBeforeReceipt()
    {
        var afterDecision = DecisionUtc.AddMinutes(1);
        var result = Build(Request(
            [Candidate("AAPL", 100m)],
            [Security(
                "AAPL",
                "security-aapl",
                "issuer-apple",
                observedAtUtc: afterDecision)],
            [Interval(
                "AAPL",
                "security-aapl",
                "issuer-apple",
                observedAtUtc: afterDecision)]));

        Assert.False(result.Readiness.IsReady);
        Assert.Empty(result.Memberships);
        AssertFailure(
            result,
            PointInTimeUniverseReadinessFailureCode.SymbolIntervalUnavailable);
    }

    [Fact]
    public void Build_BlocksAmbiguousSymbolAtDecision()
    {
        var result = Build(Request(
            [Candidate("TEST", 100m)],
            [
                Security("TEST", "security-one", "issuer-one", ambiguous: true),
                Security("TEST", "security-two", "issuer-two", ambiguous: true)
            ],
            [
                Interval("TEST", "security-one", "issuer-one", ambiguous: true),
                Interval("TEST", "security-two", "issuer-two", ambiguous: true)
            ]));

        Assert.False(result.Readiness.IsReady);
        Assert.Empty(result.Memberships);
        AssertFailure(
            result,
            PointInTimeUniverseReadinessFailureCode.SymbolAmbiguousAtDecision);
    }

    [Fact]
    public void Build_BlocksMissingIssuerWithoutInventingIdentity()
    {
        var result = Build(Request(
            [Candidate("AAPL", 100m)],
            [Security("AAPL", "security-aapl", issuerId: null)],
            [Interval("AAPL", "security-aapl", issuerId: null)]));

        Assert.False(result.Readiness.IsReady);
        Assert.Empty(result.Memberships);
        AssertFailure(
            result,
            PointInTimeUniverseReadinessFailureCode.IssuerIdentityUnavailable);
    }

    [Fact]
    public void Build_IgnoresFutureReceivedTerminalActionWithoutLeakingIt()
    {
        var baseline = Request(
            [Candidate("AAPL", 100m)],
            [Security("AAPL", "security-aapl", "issuer-apple")],
            [Interval("AAPL", "security-aapl", "issuer-apple")]);
        var withFutureAction = Request(
            baseline.Candidates,
            baseline.SecuritySnapshots,
            baseline.SymbolIntervals,
            [
                Action(
                    CorporateActionEvidenceType.WorthlessRemoval,
                    "future-worthless",
                    "AAPL",
                    DecisionUtc.AddMinutes(1),
                    fields: new Dictionary<string, string> { ["symbol"] = "AAPL" })
            ]);

        var withoutFuture = Build(baseline);
        var withFuture = Build(withFutureAction);

        Assert.True(withFuture.Readiness.IsReady);
        Assert.Equal(
            Project(withoutFuture.Memberships),
            Project(withFuture.Memberships));
        Assert.True(Assert.Single(withFuture.Memberships).Included);
    }

    [Fact]
    public void Build_ReconcilesSplitAsNonTerminalUsingOnlyAvailableAction()
    {
        var action = Action(
            CorporateActionEvidenceType.ForwardSplit,
            "split-1",
            "AAPL",
            DecisionUtc.AddHours(-1),
            securityId: "security-aapl",
            issuerId: "issuer-apple",
            fields: new Dictionary<string, string> { ["symbol"] = "AAPL" });

        var result = Build(Request(
            [Candidate("AAPL", 100m)],
            [Security("AAPL", "security-aapl", "issuer-apple")],
            [Interval("AAPL", "security-aapl", "issuer-apple")],
            [action]));

        var membership = Assert.Single(result.Memberships);
        Assert.True(result.Readiness.IsReady);
        Assert.True(membership.Included);
        Assert.Contains(
            membership.Sources,
            source => source.ObservationId == action.Sources[0].ObservationId);
    }

    [Fact]
    public void Build_ReconcilesNameChangeWithoutTreatingNewSymbolAsTerminal()
    {
        var result = Build(Request(
            [
                Candidate("OLD", 90m),
                Candidate("NEW", 100m)
            ],
            [
                Security("OLD", "security-old", "issuer-old"),
                Security("NEW", "security-new", "issuer-new")
            ],
            [
                Interval("OLD", "security-old", "issuer-old"),
                Interval("NEW", "security-new", "issuer-new")
            ],
            [
                Action(
                    CorporateActionEvidenceType.NameChange,
                    "name-change-1",
                    "OLD",
                    DecisionUtc.AddHours(-1),
                    securityId: "security-old",
                    issuerId: "issuer-old",
                    fields: new Dictionary<string, string>
                    {
                        ["old_symbol"] = "OLD",
                        ["new_symbol"] = "NEW"
                    })
            ]));

        Assert.True(result.Readiness.IsReady);
        Assert.False(result.Memberships.Single(row => row.Symbol == "OLD").Included);
        Assert.Equal(
            "terminal_name_change",
            result.Memberships.Single(row => row.Symbol == "OLD").SelectionReason);
        Assert.True(result.Memberships.Single(row => row.Symbol == "NEW").Included);
    }

    [Fact]
    public void Build_ReconcilesMergerAcquireeAsTerminalAndAcquirerAsActive()
    {
        var result = Build(Request(
            [
                Candidate("TARGET", 100m),
                Candidate("BUYER", 90m)
            ],
            [
                Security("TARGET", "security-target", "issuer-target"),
                Security("BUYER", "security-buyer", "issuer-buyer")
            ],
            [
                Interval("TARGET", "security-target", "issuer-target"),
                Interval("BUYER", "security-buyer", "issuer-buyer")
            ],
            [
                Action(
                    CorporateActionEvidenceType.StockMerger,
                    "merger-1",
                    "TARGET",
                    DecisionUtc.AddHours(-1),
                    securityId: "security-target",
                    issuerId: "issuer-target",
                    fields: new Dictionary<string, string>
                    {
                        ["acquiree_symbol"] = "TARGET",
                        ["acquirer_symbol"] = "BUYER"
                    })
            ]));

        Assert.True(result.Readiness.IsReady);
        var target = result.Memberships.Single(row => row.Symbol == "TARGET");
        var buyer = result.Memberships.Single(row => row.Symbol == "BUYER");
        Assert.False(target.Included);
        Assert.Equal("terminal_stock_merger", target.SelectionReason);
        Assert.True(buyer.Included);
        Assert.Equal(1, buyer.Rank);
    }

    [Fact]
    public void Build_ReconcilesSpinoffWithoutTerminatingEitherSecurity()
    {
        var result = Build(Request(
            [
                Candidate("PARENT", 100m),
                Candidate("CHILD", 90m)
            ],
            [
                Security("PARENT", "security-parent", "issuer-parent"),
                Security("CHILD", "security-child", "issuer-child")
            ],
            [
                Interval("PARENT", "security-parent", "issuer-parent"),
                Interval("CHILD", "security-child", "issuer-child")
            ],
            [
                Action(
                    CorporateActionEvidenceType.SpinOff,
                    "spinoff-1",
                    "PARENT",
                    DecisionUtc.AddHours(-1),
                    fields: new Dictionary<string, string>
                    {
                        ["source_symbol"] = "PARENT",
                        ["new_symbol"] = "CHILD"
                    })
            ]));

        Assert.True(result.Readiness.IsReady);
        Assert.All(result.Memberships, row => Assert.True(row.Included));
        Assert.Equal(
            new[] { "PARENT", "CHILD" },
            result.Memberships.OrderBy(row => row.Rank).Select(row => row.Symbol));
    }

    [Fact]
    public void Build_ReconcilesWorthlessRemovalAsDeterministicExclusion()
    {
        var result = Build(Request(
            [Candidate("GONE", 100m)],
            [Security("GONE", "security-gone", "issuer-gone")],
            [Interval("GONE", "security-gone", "issuer-gone")],
            [
                Action(
                    CorporateActionEvidenceType.WorthlessRemoval,
                    "worthless-1",
                    "GONE",
                    DecisionUtc.AddHours(-1),
                    securityId: "security-gone",
                    issuerId: "issuer-gone",
                    fields: new Dictionary<string, string> { ["symbol"] = "GONE" })
            ]));

        var membership = Assert.Single(result.Memberships);
        Assert.True(result.Readiness.IsReady);
        Assert.False(membership.Included);
        Assert.Null(membership.Rank);
        Assert.Equal("terminal_worthless_removal", membership.SelectionReason);
    }

    [Fact]
    public void Build_BlocksApplicableActionWithUnresolvedOutcome()
    {
        var result = Build(Request(
            [Candidate("TEST", 100m)],
            [Security("TEST", "security-test", "issuer-test")],
            [Interval("TEST", "security-test", "issuer-test")],
            [
                Action(
                    CorporateActionEvidenceType.Reorganization,
                    "reorganization-1",
                    "TEST",
                    DecisionUtc.AddHours(-1),
                    fields: new Dictionary<string, string> { ["symbol"] = "TEST" })
            ]));

        Assert.False(result.Readiness.IsReady);
        Assert.Empty(result.Memberships);
        AssertFailure(
            result,
            PointInTimeUniverseReadinessFailureCode.CorporateActionOutcomeUnresolved);
    }

    [Fact]
    public void Build_DeduplicatesIssuerShareClassesAndUsesDeterministicTies()
    {
        var result = Build(Request(
            [
                Candidate("GOOGL", 90m),
                Candidate("MSFT", 90m),
                Candidate("GOOG", 90m),
                Candidate("AAPL", 95m)
            ],
            [
                Security("GOOGL", "security-googl", "issuer-google"),
                Security("MSFT", "security-msft", "issuer-microsoft"),
                Security("GOOG", "security-goog", "issuer-google"),
                Security("AAPL", "security-aapl", "issuer-apple")
            ],
            [
                Interval("GOOGL", "security-googl", "issuer-google"),
                Interval("MSFT", "security-msft", "issuer-microsoft"),
                Interval("GOOG", "security-goog", "issuer-google"),
                Interval("AAPL", "security-aapl", "issuer-apple")
            ]));

        Assert.True(result.Readiness.IsReady);
        Assert.Equal(
            new[] { "AAPL", "GOOG", "MSFT" },
            result.Memberships
                .Where(row => row.Included)
                .OrderBy(row => row.Rank)
                .Select(row => row.Symbol));
        var duplicate = result.Memberships.Single(row => row.Symbol == "GOOGL");
        Assert.False(duplicate.Included);
        Assert.Equal(
            "duplicate_issuer_share_class_retained_GOOG",
            duplicate.SelectionReason);
    }

    [Fact]
    public void Build_IsByteProjectionStableAcrossInputOrder()
    {
        var candidates = new[]
        {
            Candidate("MSFT", 90m),
            Candidate("AAPL", 95m),
            Candidate("GOOG", 90m)
        };
        var securities = new[]
        {
            Security("MSFT", "security-msft", "issuer-microsoft"),
            Security("AAPL", "security-aapl", "issuer-apple"),
            Security("GOOG", "security-goog", "issuer-google")
        };
        var intervals = new[]
        {
            Interval("MSFT", "security-msft", "issuer-microsoft"),
            Interval("AAPL", "security-aapl", "issuer-apple"),
            Interval("GOOG", "security-goog", "issuer-google")
        };
        var actions = new[]
        {
            Action(
                CorporateActionEvidenceType.ForwardSplit,
                "split-aapl",
                "AAPL",
                DecisionUtc.AddHours(-1),
                fields: new Dictionary<string, string> { ["symbol"] = "AAPL" }),
            Action(
                CorporateActionEvidenceType.CashDividend,
                "dividend-msft",
                "MSFT",
                DecisionUtc.AddHours(-2),
                fields: new Dictionary<string, string> { ["symbol"] = "MSFT" })
        };

        var forward = Build(Request(candidates, securities, intervals, actions));
        var reverse = Build(Request(
            candidates.Reverse().ToArray(),
            securities.Reverse().ToArray(),
            intervals.Reverse().ToArray(),
            actions.Reverse().ToArray()));

        Assert.True(forward.Readiness.IsReady);
        Assert.True(reverse.Readiness.IsReady);
        Assert.Equal(Project(forward.Memberships), Project(reverse.Memberships));
    }

    private static PointInTimeUniverseBuildResult Build(
        PointInTimeUniverseBuildRequest request) =>
        new PointInTimeUniverseBuilder().Build(request);

    private static PointInTimeUniverseBuildRequest Request(
        IReadOnlyList<PointInTimeUniverseCandidate> candidates,
        IReadOnlyList<SecurityMasterSnapshotEvidenceRow> securities,
        IReadOnlyList<SymbolIntervalEvidenceRow> intervals,
        IReadOnlyList<CorporateActionEvidenceRow>? actions = null,
        DateTimeOffset? snapshotReceivedAtUtc = null) =>
        new(
            1,
            "point-in-time-universe-run",
            Hash('c'),
            "commit-point-in-time-universe",
            "sip",
            "finviz",
            "us-equities",
            "snapshot-2026-07-24",
            "v=111&f=cap_mega",
            Hash('a'),
            SessionDate,
            DecisionUtc,
            SnapshotProviderUtc,
            snapshotReceivedAtUtc ?? SnapshotReceivedUtc,
            candidates,
            securities,
            intervals,
            actions ?? [],
            [new EvidenceRowSourceAddress("universe-snapshot", Hash('a'))]);

    private static PointInTimeUniverseCandidate Candidate(
        string symbol,
        decimal score) =>
        new(
            symbol,
            score,
            SnapshotProviderUtc,
            SnapshotReceivedUtc,
            ["provider_screen", "point_in_time_score"]);

    private static SecurityMasterSnapshotEvidenceRow Security(
        string symbol,
        string securityId,
        string? issuerId,
        DateTimeOffset? observedAtUtc = null,
        bool ambiguous = false,
        string status = "active") =>
        new(
            1,
            "identity-run",
            Hash('c'),
            "commit-identity",
            "sip",
            "alpaca",
            securityId,
            issuerId,
            symbol,
            $"{symbol} Incorporated",
            "NASDAQ",
            "us_equity",
            "USD",
            status,
            tradable: true,
            marginable: true,
            shortable: true,
            easyToBorrow: true,
            fractionable: true,
            borrowStatus: "easy_to_borrow",
            maintenanceMarginRequirement: 30m,
            marginRequirementLong: 30m,
            marginRequirementShort: 30m,
            attributes: [],
            observedAtUtc ?? IdentityObservedUtc,
            ambiguous,
            [new EvidenceRowSourceAddress($"security-{securityId}", Hash('b'))]);

    private static SymbolIntervalEvidenceRow Interval(
        string symbol,
        string securityId,
        string? issuerId,
        DateTimeOffset? observedAtUtc = null,
        bool ambiguous = false) =>
        new(
            1,
            "identity-run",
            Hash('c'),
            "commit-identity",
            "sip",
            "alpaca",
            securityId,
            issuerId,
            symbol,
            observedAtUtc ?? IdentityObservedUtc,
            ambiguous,
            [new EvidenceRowSourceAddress($"interval-{securityId}", Hash('d'))]);

    private static CorporateActionEvidenceRow Action(
        CorporateActionEvidenceType type,
        string actionId,
        string primarySymbol,
        DateTimeOffset receivedAtUtc,
        string? securityId = null,
        string? issuerId = null,
        IReadOnlyDictionary<string, string>? fields = null) =>
        new(
            1,
            "action-run",
            Hash('c'),
            "commit-action",
            "sip",
            "alpaca",
            type,
            actionId,
            securityId,
            issuerId,
            primarySymbol,
            null,
            SessionDate.AddDays(-1),
            SessionDate.AddDays(-1),
            null,
            null,
            null,
            fields ?? new Dictionary<string, string> { ["symbol"] = primarySymbol },
            receivedAtUtc,
            [new EvidenceRowSourceAddress($"action-{actionId}", Hash('e'))]);

    private static void AssertFailure(
        PointInTimeUniverseBuildResult result,
        PointInTimeUniverseReadinessFailureCode code) =>
        Assert.Contains(result.Readiness.Failures, failure => failure.Code == code);

    private static IReadOnlyList<string> Project(
        IReadOnlyList<UniverseMembershipEvidenceRow> rows) =>
        rows.Select(row => String.Join(
                "|",
                row.Symbol,
                row.SecurityId,
                row.IssuerId,
                row.Included,
                row.Rank?.ToString() ?? "-",
                row.SelectionReason,
                String.Join(
                    ",",
                    row.Sources.Select(source =>
                        $"{source.ObservationId}:{source.ObservationSha256}"))))
            .ToArray();

    private static string Hash(char value) => new(value, 64);
}
