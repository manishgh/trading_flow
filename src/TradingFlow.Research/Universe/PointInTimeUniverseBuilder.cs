using System.Collections.ObjectModel;
using TradingFlow.Domain.Research;

namespace TradingFlow.Research.Universe;

public enum PointInTimeUniverseReadinessFailureCode
{
    UniverseSnapshotReceivedAfterDecision = 1,
    UniverseCandidateReceivedAfterDecision = 2,
    SecuritySnapshotUnavailable = 3,
    SymbolIntervalUnavailable = 4,
    SymbolAmbiguousAtDecision = 5,
    SecurityIdentityMismatch = 6,
    IssuerIdentityUnavailable = 7,
    IssuerIdentityMismatch = 8,
    CorporateActionIdentityUnresolved = 9,
    CorporateActionIdentityMismatch = 10,
    CorporateActionOutcomeUnresolved = 11
}

public sealed record PointInTimeUniverseReadinessFailure
{
    public PointInTimeUniverseReadinessFailure(
        PointInTimeUniverseReadinessFailureCode code,
        string subject,
        string detail)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
        Subject = Require(subject, nameof(subject));
        Detail = Require(detail, nameof(detail));
    }

    public PointInTimeUniverseReadinessFailureCode Code { get; }

    public string Subject { get; }

    public string Detail { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record PointInTimeUniverseReadinessReport
{
    public PointInTimeUniverseReadinessReport(
        IEnumerable<PointInTimeUniverseReadinessFailure> failures)
    {
        Failures = new ReadOnlyCollection<PointInTimeUniverseReadinessFailure>(
            (failures ?? throw new ArgumentNullException(nameof(failures)))
                .OrderBy(failure => failure.Code)
                .ThenBy(failure => failure.Subject, StringComparer.Ordinal)
                .ThenBy(failure => failure.Detail, StringComparer.Ordinal)
                .Distinct()
                .ToArray());
    }

    public bool IsReady => Failures.Count == 0;

    public IReadOnlyList<PointInTimeUniverseReadinessFailure> Failures { get; }
}

public sealed record PointInTimeUniverseCandidate
{
    public PointInTimeUniverseCandidate(
        string symbol,
        decimal score,
        DateTimeOffset providerTimestampUtc,
        DateTimeOffset receivedAtUtc,
        IEnumerable<string> selectionReasons)
    {
        Symbol = Require(symbol, nameof(symbol)).ToUpperInvariant();
        Score = score;
        ProviderTimestampUtc = RequireUtc(providerTimestampUtc, nameof(providerTimestampUtc));
        ReceivedAtUtc = RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (receivedAtUtc < providerTimestampUtc)
        {
            throw new ArgumentException(
                "Candidate receipt cannot precede its provider timestamp.",
                nameof(receivedAtUtc));
        }

        var reasons = (selectionReasons ?? throw new ArgumentNullException(nameof(selectionReasons)))
            .Select(reason => Require(reason, nameof(selectionReasons)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();
        if (reasons.Length == 0)
        {
            throw new ArgumentException(
                "A universe candidate requires at least one selection reason.",
                nameof(selectionReasons));
        }

        SelectionReasons = new ReadOnlyCollection<string>(reasons);
    }

    public string Symbol { get; }

    public decimal Score { get; }

    public DateTimeOffset ProviderTimestampUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public IReadOnlyList<string> SelectionReasons { get; }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }

        return value;
    }
}

public sealed record PointInTimeUniverseBuildRequest
{
    public PointInTimeUniverseBuildRequest(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string provider,
        string universeId,
        string snapshotId,
        string providerQuery,
        string snapshotContentSha256,
        DateOnly effectiveSessionDate,
        DateTimeOffset asOfUtc,
        DateTimeOffset snapshotProviderTimestampUtc,
        DateTimeOffset snapshotReceivedAtUtc,
        IReadOnlyList<PointInTimeUniverseCandidate> candidates,
        IReadOnlyList<SecurityMasterSnapshotEvidenceRow> securitySnapshots,
        IReadOnlyList<SymbolIntervalEvidenceRow> symbolIntervals,
        IReadOnlyList<CorporateActionEvidenceRow> corporateActions,
        IReadOnlyList<EvidenceRowSourceAddress> snapshotSources,
        int? maximumMembers = null)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion;
        RunId = Require(runId, nameof(runId));
        ConfigHash = RequireSha256(configHash, nameof(configHash));
        CodeVersion = Require(codeVersion, nameof(codeVersion));
        DataFeed = Require(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        Provider = Require(provider, nameof(provider)).ToLowerInvariant();
        UniverseId = Require(universeId, nameof(universeId));
        SnapshotId = Require(snapshotId, nameof(snapshotId));
        ProviderQuery = Require(providerQuery, nameof(providerQuery));
        SnapshotContentSha256 = RequireSha256(
            snapshotContentSha256,
            nameof(snapshotContentSha256));
        EffectiveSessionDate = effectiveSessionDate;
        AsOfUtc = RequireUtc(asOfUtc, nameof(asOfUtc));
        SnapshotProviderTimestampUtc = RequireUtc(
            snapshotProviderTimestampUtc,
            nameof(snapshotProviderTimestampUtc));
        SnapshotReceivedAtUtc = RequireUtc(
            snapshotReceivedAtUtc,
            nameof(snapshotReceivedAtUtc));
        if (snapshotReceivedAtUtc < snapshotProviderTimestampUtc)
        {
            throw new ArgumentException(
                "Universe snapshot receipt cannot precede its provider timestamp.",
                nameof(snapshotReceivedAtUtc));
        }

        if (maximumMembers is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMembers));
        }

        MaximumMembers = maximumMembers;
        Candidates = Copy(candidates, nameof(candidates));
        SecuritySnapshots = Copy(securitySnapshots, nameof(securitySnapshots));
        SymbolIntervals = Copy(symbolIntervals, nameof(symbolIntervals));
        CorporateActions = Copy(corporateActions, nameof(corporateActions));
        SnapshotSources = Copy(snapshotSources, nameof(snapshotSources));
        if (!SnapshotSources.Any(source =>
                source.ObservationSha256.Equals(
                    SnapshotContentSha256,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Snapshot lineage must include the exact snapshot content hash.",
                nameof(snapshotSources));
        }
    }

    public int SchemaVersion { get; }

    public string RunId { get; }

    public string ConfigHash { get; }

    public string CodeVersion { get; }

    public string DataFeed { get; }

    public string Provider { get; }

    public string UniverseId { get; }

    public string SnapshotId { get; }

    public string ProviderQuery { get; }

    public string SnapshotContentSha256 { get; }

    public DateOnly EffectiveSessionDate { get; }

    public DateTimeOffset AsOfUtc { get; }

    public DateTimeOffset SnapshotProviderTimestampUtc { get; }

    public DateTimeOffset SnapshotReceivedAtUtc { get; }

    public int? MaximumMembers { get; }

    public IReadOnlyList<PointInTimeUniverseCandidate> Candidates { get; }

    public IReadOnlyList<SecurityMasterSnapshotEvidenceRow> SecuritySnapshots { get; }

    public IReadOnlyList<SymbolIntervalEvidenceRow> SymbolIntervals { get; }

    public IReadOnlyList<CorporateActionEvidenceRow> CorporateActions { get; }

    public IReadOnlyList<EvidenceRowSourceAddress> SnapshotSources { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Any(value => value is null))
        {
            throw new ArgumentException(
                "Evidence inputs cannot contain null values.",
                parameterName);
        }

        return new ReadOnlyCollection<T>(values.ToArray());
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string RequireSha256(string value, string parameterName)
    {
        var normalized = Require(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character =>
                !Char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Value must be a lowercase or uppercase SHA-256 hex digest.",
                parameterName);
        }

        return normalized;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }

        return value;
    }
}

public sealed record PointInTimeUniverseBuildResult
{
    public PointInTimeUniverseBuildResult(
        IReadOnlyList<UniverseMembershipEvidenceRow> memberships,
        PointInTimeUniverseReadinessReport readiness)
    {
        Memberships = new ReadOnlyCollection<UniverseMembershipEvidenceRow>(
            (memberships ?? throw new ArgumentNullException(nameof(memberships))).ToArray());
        Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        if (!Readiness.IsReady && Memberships.Count != 0)
        {
            throw new ArgumentException(
                "A blocked point-in-time universe cannot expose partial membership.");
        }
    }

    public IReadOnlyList<UniverseMembershipEvidenceRow> Memberships { get; }

    public PointInTimeUniverseReadinessReport Readiness { get; }
}

/// <summary>
/// Builds a universe only from evidence knowable at the requested decision timestamp.
/// A single unresolved identity or applicable action blocks publication of all membership rows.
/// </summary>
public sealed class PointInTimeUniverseBuilder
{
    private static readonly IReadOnlySet<CorporateActionEvidenceType> NonTerminalActions =
        new HashSet<CorporateActionEvidenceType>
        {
            CorporateActionEvidenceType.ForwardSplit,
            CorporateActionEvidenceType.StockDividend,
            CorporateActionEvidenceType.CashDividend,
            CorporateActionEvidenceType.SpinOff,
            CorporateActionEvidenceType.RightsDistribution,
            CorporateActionEvidenceType.PartialCall
        };

    public PointInTimeUniverseBuildResult Build(PointInTimeUniverseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failures = new List<PointInTimeUniverseReadinessFailure>();

        if (request.SnapshotReceivedAtUtc > request.AsOfUtc)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.UniverseSnapshotReceivedAfterDecision,
                request.SnapshotId,
                "The universe snapshot was not available at the decision timestamp."));
        }

        var duplicateCandidateSymbols = request.Candidates
            .GroupBy(candidate => candidate.Symbol, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in duplicateCandidateSymbols)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SymbolAmbiguousAtDecision,
                symbol,
                "The provider snapshot contains the symbol more than once."));
        }

        var resolutions = new List<CandidateResolution>();
        foreach (var candidate in request.Candidates
                     .OrderBy(value => value.Symbol, StringComparer.Ordinal)
                     .ThenByDescending(value => value.Score))
        {
            if (candidate.ReceivedAtUtc > request.AsOfUtc ||
                candidate.ProviderTimestampUtc > request.AsOfUtc)
            {
                failures.Add(Failure(
                    PointInTimeUniverseReadinessFailureCode.UniverseCandidateReceivedAfterDecision,
                    candidate.Symbol,
                    "The provider candidate was not knowable at the decision timestamp."));
                continue;
            }

            if (duplicateCandidateSymbols.Contains(candidate.Symbol))
            {
                continue;
            }

            var resolution = ResolveCandidate(request, candidate, failures);
            if (resolution is not null)
            {
                resolutions.Add(resolution);
            }
        }

        var readiness = new PointInTimeUniverseReadinessReport(failures);
        if (!readiness.IsReady)
        {
            return new PointInTimeUniverseBuildResult([], readiness);
        }

        var decisions = RankAndDeduplicate(resolutions, request.MaximumMembers);
        var memberships = decisions
            .OrderBy(decision => decision.Included ? 0 : 1)
            .ThenBy(decision => decision.Rank ?? Int32.MaxValue)
            .ThenBy(decision => decision.Resolution.Candidate.Symbol, StringComparer.Ordinal)
            .Select(decision => ToMembership(request, decision))
            .ToArray();

        return new PointInTimeUniverseBuildResult(memberships, readiness);
    }

    private static CandidateResolution? ResolveCandidate(
        PointInTimeUniverseBuildRequest request,
        PointInTimeUniverseCandidate candidate,
        ICollection<PointInTimeUniverseReadinessFailure> failures)
    {
        var intervals = request.SymbolIntervals
            .Where(row =>
                row.Symbol.Equals(candidate.Symbol, StringComparison.Ordinal) &&
                row.ObservedAtUtc <= request.AsOfUtc)
            .ToArray();
        if (intervals.Length == 0)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SymbolIntervalUnavailable,
                candidate.Symbol,
                "No symbol interval was available at the decision timestamp."));
            return null;
        }

        var latestIntervalTime = intervals.Max(row => row.ObservedAtUtc);
        var latestIntervals = intervals
            .Where(row => row.ObservedAtUtc == latestIntervalTime)
            .OrderBy(row => row.SecurityId, StringComparer.Ordinal)
            .ToArray();
        if (latestIntervals.Any(row => row.ReadinessFailures.Contains(
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot)) ||
            latestIntervals.Select(row => row.SecurityId).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SymbolAmbiguousAtDecision,
                candidate.Symbol,
                "The latest knowable symbol interval maps to multiple securities."));
            return null;
        }

        var interval = latestIntervals[0];
        var snapshots = request.SecuritySnapshots
            .Where(row =>
                row.SecurityId.Equals(interval.SecurityId, StringComparison.Ordinal) &&
                row.ObservedAtUtc <= request.AsOfUtc)
            .ToArray();
        if (snapshots.Length == 0)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SecuritySnapshotUnavailable,
                candidate.Symbol,
                "No security-master snapshot was available at the decision timestamp."));
            return null;
        }

        var latestSnapshotTime = snapshots.Max(row => row.ObservedAtUtc);
        var latestSnapshots = snapshots
            .Where(row => row.ObservedAtUtc == latestSnapshotTime)
            .OrderBy(row => row.Symbol, StringComparer.Ordinal)
            .ToArray();
        if (latestSnapshots.Length != 1)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SecurityIdentityMismatch,
                candidate.Symbol,
                "The latest security-master evidence is not unique."));
            return null;
        }

        var security = latestSnapshots[0];
        if (!security.Symbol.Equals(candidate.Symbol, StringComparison.Ordinal) ||
            !security.SecurityId.Equals(interval.SecurityId, StringComparison.Ordinal))
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SecurityIdentityMismatch,
                candidate.Symbol,
                "The latest symbol interval and security snapshot do not identify the same listing."));
            return null;
        }

        if (security.ReadinessFailures.Contains(
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot))
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.SymbolAmbiguousAtDecision,
                candidate.Symbol,
                "The security-master snapshot marks this symbol as ambiguous."));
            return null;
        }

        if (security.IssuerId is null || interval.IssuerId is null)
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.IssuerIdentityUnavailable,
                candidate.Symbol,
                "Issuer identity is absent; the builder will not invent one."));
            return null;
        }

        if (!security.IssuerId.Equals(interval.IssuerId, StringComparison.Ordinal))
        {
            failures.Add(Failure(
                PointInTimeUniverseReadinessFailureCode.IssuerIdentityMismatch,
                candidate.Symbol,
                "Security-master and symbol-interval issuer identities differ."));
            return null;
        }

        if (!security.Status.Equals("active", StringComparison.Ordinal))
        {
            return new CandidateResolution(
                candidate,
                security,
                interval,
                TerminalReason: $"listing_status_{security.Status}",
                ApplicableActions: []);
        }

        var actionResolution = ResolveActions(
            request,
            candidate,
            security,
            failures);
        if (!actionResolution.IsResolved)
        {
            return null;
        }

        return new CandidateResolution(
            candidate,
            security,
            interval,
            actionResolution.TerminalReason,
            actionResolution.ApplicableActions);
    }

    private static ActionResolution ResolveActions(
        PointInTimeUniverseBuildRequest request,
        PointInTimeUniverseCandidate candidate,
        SecurityMasterSnapshotEvidenceRow security,
        ICollection<PointInTimeUniverseReadinessFailure> failures)
    {
        var initialFailureCount = failures.Count;
        var applicable = request.CorporateActions
            .Where(action =>
                action.AvailabilityTimestampUtc <= request.AsOfUtc &&
                EffectiveDate(action) <= request.EffectiveSessionDate &&
                ReferencesCandidate(action, candidate.Symbol, security.SecurityId))
            .OrderBy(action => EffectiveDate(action))
            .ThenBy(action => action.ActionType)
            .ThenBy(action => action.ProviderActionId, StringComparer.Ordinal)
            .ToArray();

        string? terminalReason = null;
        foreach (var action in applicable)
        {
            var role = SymbolRole(action, candidate.Symbol);
            var actionIdentityTargetsCandidate =
                role is SymbolActionRole.Primary or
                    SymbolActionRole.Old or
                    SymbolActionRole.Source or
                    SymbolActionRole.Acquiree ||
                action.PrimarySymbol?.Equals(
                    candidate.Symbol,
                    StringComparison.Ordinal) == true;
            if (actionIdentityTargetsCandidate &&
                action.SecurityId is not null &&
                !action.SecurityId.Equals(security.SecurityId, StringComparison.Ordinal))
            {
                failures.Add(Failure(
                    PointInTimeUniverseReadinessFailureCode.CorporateActionIdentityMismatch,
                    $"{candidate.Symbol}:{action.ProviderActionId}",
                    "The corporate action security identity conflicts with the resolved candidate."));
                continue;
            }

            if (actionIdentityTargetsCandidate &&
                action.IssuerId is not null &&
                !action.IssuerId.Equals(security.IssuerId, StringComparison.Ordinal))
            {
                failures.Add(Failure(
                    PointInTimeUniverseReadinessFailureCode.CorporateActionIdentityMismatch,
                    $"{candidate.Symbol}:{action.ProviderActionId}",
                    "The corporate action issuer identity conflicts with the resolved candidate."));
                continue;
            }

            if (action.SecurityId is null &&
                role is SymbolActionRole.None)
            {
                failures.Add(Failure(
                    PointInTimeUniverseReadinessFailureCode.CorporateActionIdentityUnresolved,
                    $"{candidate.Symbol}:{action.ProviderActionId}",
                    "The applicable corporate action cannot be tied to the exact candidate security."));
                continue;
            }

            var outcome = ResolveOutcome(
                action,
                candidate.Symbol,
                action.SecurityId?.Equals(
                    security.SecurityId,
                    StringComparison.Ordinal) == true);
            if (outcome == ActionOutcome.Unresolved)
            {
                failures.Add(Failure(
                    PointInTimeUniverseReadinessFailureCode.CorporateActionOutcomeUnresolved,
                    $"{candidate.Symbol}:{action.ProviderActionId}",
                    $"Outcome for {action.ActionType} is not deterministic from available evidence."));
            }
            else if (outcome == ActionOutcome.Terminal)
            {
                terminalReason = $"terminal_{ToReason(action.ActionType)}";
            }
        }

        return failures.Count == initialFailureCount
            ? new ActionResolution(true, terminalReason, applicable)
            : new ActionResolution(false, null, []);
    }

    private static IReadOnlyList<MembershipDecision> RankAndDeduplicate(
        IReadOnlyList<CandidateResolution> resolutions,
        int? maximumMembers)
    {
        var active = resolutions
            .Where(resolution => resolution.TerminalReason is null)
            .ToArray();
        var decisions = resolutions
            .Where(resolution => resolution.TerminalReason is not null)
            .Select(resolution => new MembershipDecision(
                resolution,
                false,
                null,
                resolution.TerminalReason!))
            .ToList();

        var retained = new List<CandidateResolution>();
        foreach (var group in active
                     .GroupBy(resolution => resolution.Security.IssuerId!, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(resolution => resolution.Candidate.Score)
                .ThenBy(resolution => resolution.Candidate.Symbol, StringComparer.Ordinal)
                .ThenBy(resolution => resolution.Security.SecurityId, StringComparer.Ordinal)
                .ToArray();
            retained.Add(ordered[0]);
            decisions.AddRange(ordered.Skip(1).Select(resolution =>
                new MembershipDecision(
                    resolution,
                    false,
                    null,
                    $"duplicate_issuer_share_class_retained_{ordered[0].Candidate.Symbol}")));
        }

        var ranked = retained
            .OrderByDescending(resolution => resolution.Candidate.Score)
            .ThenBy(resolution => resolution.Candidate.Symbol, StringComparer.Ordinal)
            .ThenBy(resolution => resolution.Security.SecurityId, StringComparer.Ordinal)
            .ToArray();
        var selectedCount = maximumMembers is null
            ? ranked.Length
            : Math.Min(maximumMembers.Value, ranked.Length);
        decisions.AddRange(ranked.Take(selectedCount).Select((resolution, index) =>
            new MembershipDecision(
                resolution,
                true,
                index + 1,
                String.Join(",", resolution.Candidate.SelectionReasons))));
        decisions.AddRange(ranked.Skip(selectedCount).Select(resolution =>
            new MembershipDecision(resolution, false, null, "outside_member_limit")));

        return decisions;
    }

    private static UniverseMembershipEvidenceRow ToMembership(
        PointInTimeUniverseBuildRequest request,
        MembershipDecision decision)
    {
        var sources = request.SnapshotSources
            .Concat(decision.Resolution.Security.Sources)
            .Concat(decision.Resolution.Interval.Sources)
            .Concat(decision.Resolution.ApplicableActions.SelectMany(action => action.Sources))
            .GroupBy(source => (source.ObservationId, source.ObservationSha256))
            .Select(group => group.First())
            .OrderBy(source => source.ObservationId, StringComparer.Ordinal)
            .ThenBy(source => source.ObservationSha256, StringComparer.Ordinal)
            .ToArray();

        return new UniverseMembershipEvidenceRow(
            request.SchemaVersion,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.DataFeed,
            request.Provider,
            request.UniverseId,
            request.SnapshotId,
            request.ProviderQuery,
            request.SnapshotContentSha256,
            request.EffectiveSessionDate,
            decision.Resolution.Security.SecurityId,
            decision.Resolution.Security.IssuerId!,
            decision.Resolution.Candidate.Symbol,
            request.AsOfUtc,
            decision.Included,
            decision.Rank,
            decision.SelectionReason,
            decision.Resolution.Candidate.ProviderTimestampUtc,
            decision.Resolution.Candidate.ReceivedAtUtc,
            sources);
    }

    private static DateOnly EffectiveDate(CorporateActionEvidenceRow action) =>
        action.EffectiveDate ?? action.ExDate ?? action.ProcessDate;

    private static bool ReferencesCandidate(
        CorporateActionEvidenceRow action,
        string symbol,
        string securityId) =>
        action.SecurityId?.Equals(securityId, StringComparison.Ordinal) == true ||
        ActionSymbols(action).Contains(symbol, StringComparer.Ordinal);

    private static ActionOutcome ResolveOutcome(
        CorporateActionEvidenceRow action,
        string candidateSymbol,
        bool exactSecurityIdentity)
    {
        if (NonTerminalActions.Contains(action.ActionType))
        {
            return ActionOutcome.NonTerminal;
        }

        var role = SymbolRole(action, candidateSymbol);
        return action.ActionType switch
        {
            CorporateActionEvidenceType.ReverseSplit or
            CorporateActionEvidenceType.UnitSplit or
            CorporateActionEvidenceType.NameChange =>
                ResolveSymbolReplacementOutcome(action, role),
            CorporateActionEvidenceType.CashMerger =>
                exactSecurityIdentity ||
                role is SymbolActionRole.Primary or SymbolActionRole.Acquiree
                    ? ActionOutcome.Terminal
                    : ActionOutcome.Unresolved,
            CorporateActionEvidenceType.StockMerger or
            CorporateActionEvidenceType.StockAndCashMerger =>
                role switch
                {
                    SymbolActionRole.Acquiree => ActionOutcome.Terminal,
                    SymbolActionRole.Acquirer => ActionOutcome.NonTerminal,
                    _ => ActionOutcome.Unresolved
                },
            CorporateActionEvidenceType.Redemption or
            CorporateActionEvidenceType.WorthlessRemoval =>
                exactSecurityIdentity || role is SymbolActionRole.Primary
                    ? ActionOutcome.Terminal
                    : ActionOutcome.Unresolved,
            CorporateActionEvidenceType.Reorganization => ActionOutcome.Unresolved,
            _ => ActionOutcome.NonTerminal
        };
    }

    private static ActionOutcome ResolveSymbolReplacementOutcome(
        CorporateActionEvidenceRow action,
        SymbolActionRole role)
    {
        var oldSymbol = Field(action, "old_symbol") ?? Field(action, "symbol");
        var newSymbol = Field(action, "new_symbol");
        if (newSymbol is null || oldSymbol is null ||
            oldSymbol.Equals(newSymbol, StringComparison.Ordinal))
        {
            return ActionOutcome.NonTerminal;
        }

        return role switch
        {
            SymbolActionRole.Old or SymbolActionRole.Primary => ActionOutcome.Terminal,
            SymbolActionRole.New => ActionOutcome.NonTerminal,
            _ => ActionOutcome.Unresolved
        };
    }

    private static SymbolActionRole SymbolRole(
        CorporateActionEvidenceRow action,
        string symbol)
    {
        if (Matches(action, symbol, "acquiree_symbol"))
        {
            return SymbolActionRole.Acquiree;
        }

        if (Matches(action, symbol, "acquirer_symbol"))
        {
            return SymbolActionRole.Acquirer;
        }

        if (Matches(action, symbol, "old_symbol"))
        {
            return SymbolActionRole.Old;
        }

        if (Matches(action, symbol, "new_symbol"))
        {
            return SymbolActionRole.New;
        }

        if (Matches(action, symbol, "source_symbol"))
        {
            return SymbolActionRole.Source;
        }

        return action.PrimarySymbol?.Equals(symbol, StringComparison.Ordinal) == true ||
               Matches(action, symbol, "symbol")
            ? SymbolActionRole.Primary
            : SymbolActionRole.None;
    }

    private static bool Matches(
        CorporateActionEvidenceRow action,
        string symbol,
        string fieldName) =>
        Field(action, fieldName)?.Equals(symbol, StringComparison.Ordinal) == true;

    private static string? Field(
        CorporateActionEvidenceRow action,
        string fieldName) =>
        action.ProviderFields.TryGetValue(fieldName, out var value) &&
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim().Trim('"').ToUpperInvariant()
            : null;

    private static IReadOnlyList<string> ActionSymbols(CorporateActionEvidenceRow action) =>
        action.ProviderFields
            .Where(pair => pair.Key.EndsWith("symbol", StringComparison.Ordinal))
            .Select(pair => pair.Value.Trim().Trim('"').ToUpperInvariant())
            .Append(action.PrimarySymbol ?? String.Empty)
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();

    private static string ToReason(CorporateActionEvidenceType actionType) =>
        String.Concat(actionType.ToString().Select((character, index) =>
            Char.IsUpper(character) && index > 0
                ? $"_{Char.ToLowerInvariant(character)}"
                : Char.ToLowerInvariant(character).ToString()));

    private static PointInTimeUniverseReadinessFailure Failure(
        PointInTimeUniverseReadinessFailureCode code,
        string subject,
        string detail) =>
        new(code, subject, detail);

    private sealed record CandidateResolution(
        PointInTimeUniverseCandidate Candidate,
        SecurityMasterSnapshotEvidenceRow Security,
        SymbolIntervalEvidenceRow Interval,
        string? TerminalReason,
        IReadOnlyList<CorporateActionEvidenceRow> ApplicableActions);

    private sealed record MembershipDecision(
        CandidateResolution Resolution,
        bool Included,
        int? Rank,
        string SelectionReason);

    private sealed record ActionResolution(
        bool IsResolved,
        string? TerminalReason,
        IReadOnlyList<CorporateActionEvidenceRow> ApplicableActions);

    private enum ActionOutcome
    {
        NonTerminal,
        Terminal,
        Unresolved
    }

    private enum SymbolActionRole
    {
        None,
        Primary,
        Old,
        New,
        Source,
        Acquiree,
        Acquirer
    }
}
