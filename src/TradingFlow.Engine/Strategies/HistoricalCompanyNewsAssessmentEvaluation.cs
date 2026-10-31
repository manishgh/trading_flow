using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Shared multi-story Research decision: common usable-evidence mask, negative veto, then positive requirement.</summary>
public static class HistoricalCompanyNewsAssessmentEvaluation
{
    // Apply the same completeness test in all arms before exposing any directional decision or order plan.
    public static (string? RejectionReason, ImmutableArray<StrategyDecisionRule> Rules) Evaluate(StrategyDecisionRequest request)
    {
        var unknown = AssessmentUnknownReason(request);
        var rules = ImmutableArray.CreateBuilder<StrategyDecisionRule>();
        rules.Add(new("qualifier", "historical_company_news_assessment_checked", unknown is null,
            HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId,
            JsonSerializer.Serialize(new { unknown, context = request.HistoricalNewsPresence })));
        if (unknown is not null) return ("historical_company_news_assessment_unknown", rules.ToImmutable());

        var treatment = request.Strategy.EntryRules.HistoricalNewsDiagnostic;
        var revisions = request.HistoricalNewsPresence!.EligibleRevisions;
        var positive = revisions.Count(item => item.Assessment!.State == CompanyNewsAssessmentState.Positive);
        var negative = revisions.Count(item => item.Assessment!.State == CompanyNewsAssessmentState.Negative);
        if (treatment is HistoricalCompanyNewsDiagnosticPolicy.NegativeVeto or HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired)
        {
            rules.Add(new("qualifier", "historical_company_news_negative_veto_checked", negative == 0,
                HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId,
                JsonSerializer.Serialize(new { positiveStoryCount = positive, negativeStoryCount = negative,
                    knownMixed = positive > 0 && negative > 0, negativeWins = true })));
            if (negative > 0) return ("historical_negative_company_news_veto", rules.ToImmutable());
        }
        if (treatment == HistoricalCompanyNewsDiagnosticPolicy.PositiveRequired)
        {
            rules.Add(new("qualifier", "historical_positive_company_news_required", positive > 0,
                HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId,
                JsonSerializer.Serialize(new { positiveStoryCount = positive })));
            if (positive == 0) return ("historical_positive_company_news_missing", rules.ToImmutable());
        }
        return (null, rules.ToImmutable());
    }

    // Missing/uncertain/failed assessment is unknown in every arm, including the direction-neutral coverage control.
    public static string? AssessmentUnknownReason(StrategyDecisionRequest request)
    {
        var evidenceUnknown = HistoricalNewsPresenceEvaluation.EvidenceUnknownReason(request);
        if (evidenceUnknown is not null) return evidenceUnknown;
        var context = request.HistoricalNewsPresence!;
        if (context.AssessmentPolicy is not { } policy || !policy.IsComplete())
            return "company_news_assessment_policy_incomplete";
        foreach (var revision in context.EligibleRevisions)
        {
            var reason = RevisionAssessmentUnknownReason(revision, context.Symbol, policy);
            if (reason is not null) return reason;
        }
        return null;
    }

    // Apply the shared package/extraction pairing, then rederive retained text and pinned-model output to reject edited labels.
    private static string? RevisionAssessmentUnknownReason(HistoricalNewsPresenceRevision revision, string symbol,
        HistoricalCompanyNewsAssessmentPolicy policy)
    {
        if (revision.Assessment is not { } assessment) return "selected_revision_company_assessment_missing";
        if (assessment.Extraction is not { Revision: not null, RuleResult: not null } extraction ||
            extraction.EvidenceSpans.IsDefault || extraction.Reasons.IsDefault || assessment.Reasons.IsDefault)
            return "selected_revision_company_assessment_incomplete";
        if (assessment.IssuerSymbol != symbol || assessment.Revision.Provider != revision.Provider ||
            assessment.Revision.ProviderArticleId != revision.ArticleId || assessment.Revision.RevisionId != revision.RevisionId ||
            assessment.RevisionContentSha256 != CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(assessment.Revision))
            return "selected_revision_company_assessment_binding_mismatch";
        if (!policy.AllowsExtraction(extraction) || assessment.RuleVersion != policy.RuleVersion)
            return "company_news_assessment_method_mismatch";
        if (assessment.AssessedAtUtc == default || assessment.AssessedAtUtc.Offset != TimeSpan.Zero ||
            assessment.AssessedAtUtc < revision.ActualReceivedAtUtc ||
            assessment.Extraction.RuleResult.ClassifiedAtUtc < revision.ActualReceivedAtUtc)
            return "company_news_actual_assessment_clock_invalid";
        var pinned = new CompanyNewsPinnedModel(policy.ModelProvider, policy.ModelIdentity, policy.ModelVersion,
            policy.ModelArtifactSha256, policy.TrainingDataProvenance, policy.ModelTrainingDataCutoffUtc);
        try
        {
            var verified = CompanyNewsAssessmentClassifier.Complete(assessment.Extraction, assessment.ModelEvidence,
                pinned, assessment.AssessedAtUtc);
            if (verified.State != assessment.State || assessment.State == CompanyNewsAssessmentState.Unknown ||
                !Enum.IsDefined(assessment.State)) return "company_news_relevance_direction_or_model_uncertain";
        }
        catch (ArgumentException)
        {
            return "company_news_assessment_content_or_model_invalid";
        }
        return null;
    }
}
