using TradingFlow.Domain.Strategies;
using System.Collections.Immutable;
using System.Text.Json;

namespace TradingFlow.Engine.Strategies;

/// <summary>Applies the common evidence gate before the fixed publication-presence veto, without changing strict news.</summary>
public static class HistoricalNewsPresenceEvaluation
{
    // Evaluate both arms through the same evidence test, then optionally apply the fixed presence veto.
    public static (string? RejectionReason, ImmutableArray<StrategyDecisionRule> Rules) Evaluate(StrategyDecisionRequest request)
    {
        if (HistoricalCompanyNewsDiagnosticPolicy.IsTreatment(request.Strategy.EntryRules.HistoricalNewsDiagnostic))
            return HistoricalCompanyNewsAssessmentEvaluation.Evaluate(request);
        var unknown = EvidenceUnknownReason(request);
        var rules = ImmutableArray.CreateBuilder<StrategyDecisionRule>();
        rules.Add(new("qualifier", "historical_news_evidence_checked", unknown is null,
            HistoricalNewsDiagnosticPolicy.AdmissionPolicyId,
            JsonSerializer.Serialize(new { unknown, context = request.HistoricalNewsPresence })));
        if (unknown is not null) return ("historical_news_evidence_unknown", rules.ToImmutable());
        if (request.Strategy.EntryRules.HistoricalNewsDiagnostic == HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews)
        {
            var count = request.HistoricalNewsPresence!.EligibleRevisions.Length;
            rules.Add(new("qualifier", "historical_news_presence_checked", count == 0,
                HistoricalNewsDiagnosticPolicy.AdmissionPolicyId,
                JsonSerializer.Serialize(new { eligibleStoryCount = count, freshnessHours = HistoricalNewsDiagnosticPolicy.FreshnessHours })));
            if (count > 0) return ("historical_recent_company_news_veto", rules.ToImmutable());
        }
        return (null, rules.ToImmutable());
    }

    // Refuse diagnostic evidence outside an explicitly bound Research backtest or alongside strict news inputs.
    public static void ValidateBinding(StrategyDecisionRequest request)
    {
        var treatment = request.Strategy.EntryRules.HistoricalNewsDiagnostic;
        var admission = request.AdmissionProfile.HistoricalNewsDiagnosticPolicyId;
        if (treatment is null && admission is null && request.HistoricalNewsPresence is null) return;
        if (!HistoricalCompanyNewsDiagnosticPolicy.IsBoundTreatment(treatment, admission) ||
            request.SelectionMode != StrategySelectionMode.Backtest)
            throw new InvalidOperationException("Historical news diagnostic requires its explicit Research backtest admission policy and treatment.");
        if (!request.Catalysts.IsEmpty || request.DrawdownContext?.Events is not null ||
            HasStrictNewsRequirements(request.Strategy.EntryRules))
            throw new InvalidOperationException("Historical news diagnostic cannot substitute for strict classified-event or catalyst rules.");
        if (request.HistoricalNewsPresence is { } context &&
            (context.PolicyId != HistoricalNewsDiagnosticPolicy.PolicyId || context.PolicySha256 != HistoricalNewsDiagnosticPolicy.PolicySha256))
            throw new InvalidOperationException("Historical news diagnostic policy differs from the frozen provider-update assumption.");
    }

    // Disabling the optional strict provider must never silently weaken a selected recipe's declared news conditions.
    public static bool HasStrictNewsRequirements(EntryRules rules) =>
        rules.RequirePositiveNews || rules.MinNewsSentiment is not null || rules.VetoNewsSentimentBelow is not null ||
        rules.MaxShortNewsSentiment is not null || rules.MinCatalystPriceMovePct is not null ||
        rules.MaxCatalystPriceMovePct is not null || rules.MinShortCatalystDropPct is not null ||
        rules.MaxCatalystConfirmationBars is not null || rules.DrawdownQualification?.UsesEventContext == true ||
        rules.VetoFreshNewsHours is not null || rules.SetupType.Equals("catalyst_drift", StringComparison.OrdinalIgnoreCase) ||
        rules.AnchoredVwapMode.Equals("catalyst", StringComparison.OrdinalIgnoreCase) ||
        rules.ShortAnchoredVwapMode.Equals("catalyst", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(rules.AnchorType, "catalyst", StringComparison.OrdinalIgnoreCase);

    // Return an identical unknown reason for both news arms; a malformed selected revision cannot prove absence.
    public static string? EvidenceUnknownReason(StrategyDecisionRequest request)
    {
        var context = request.HistoricalNewsPresence;
        if (context is null) return "diagnostic_context_missing";
        if (context.Symbol != request.Candidate.Identity.Symbol || context.DecisionAtUtc != request.AsOfUtc ||
            !IsUtc(context.DecisionAtUtc) || !IsHash(context.EvidenceSha256) || context.EligibleRevisions.IsDefault)
            return "diagnostic_context_binding_incomplete";
        if (!context.QueryEvidenceComplete || context.UnknownReason is not null)
            return context.UnknownReason ?? "provider_query_window_incomplete";
        var identities = new HashSet<(string Provider, string ArticleId)>();
        foreach (var revision in context.EligibleRevisions)
        {
            if (!IsSelectedRevisionValid(revision, context) || !identities.Add((revision.Provider, revision.ArticleId)))
                return "selected_news_revision_identity_or_clocks_ambiguous";
        }
        return null;
    }

    // Selected revisions must be fresh by publication, available by assumption, and linked by their own metadata.
    private static bool IsSelectedRevisionValid(HistoricalNewsPresenceRevision revision, HistoricalNewsPresenceContext context)
    {
        var age = context.DecisionAtUtc - revision.PublishedAtUtc;
        return !String.IsNullOrWhiteSpace(revision.Provider) && !String.IsNullOrWhiteSpace(revision.ArticleId) &&
            !String.IsNullOrWhiteSpace(revision.RevisionId) && IsHash(revision.ContentSha256) &&
            IsUtc(revision.PublishedAtUtc) && IsUtc(revision.UpdatedAtUtc) && IsUtc(revision.ActualReceivedAtUtc) &&
            IsUtc(revision.AssumedAvailableAtUtc) && revision.UpdatedAtUtc >= revision.PublishedAtUtc &&
            revision.ActualReceivedAtUtc >= revision.UpdatedAtUtc &&
            revision.UpdatedAtUtc <= DateTimeOffset.MaxValue.AddHours(-24) &&
            revision.AssumedAvailableAtUtc > revision.UpdatedAtUtc.AddHours(24) &&
            revision.AssumedAvailableAtUtc <= context.DecisionAtUtc && age >= TimeSpan.Zero &&
            age <= TimeSpan.FromHours(HistoricalNewsDiagnosticPolicy.FreshnessHours) &&
            !revision.Symbols.IsDefaultOrEmpty && revision.Symbols.Contains(context.Symbol, StringComparer.Ordinal) &&
            !revision.SourceSha256.IsDefaultOrEmpty && revision.SourceSha256.All(IsHash);
    }

    // Keep validation of source identities independent from any receipt availability claim.
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // UTC is mandatory for every explicit diagnostic clock, including real receipts after the decision.
    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
