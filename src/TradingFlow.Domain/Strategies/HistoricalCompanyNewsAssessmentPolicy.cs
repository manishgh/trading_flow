using TradingFlow.Domain.News;

namespace TradingFlow.Domain.Strategies;

/// <summary>Frozen company-assessment treatments; retrospective labels never supply strict historical availability.</summary>
public static class HistoricalCompanyNewsDiagnosticPolicy
{
    public const string AdmissionPolicyId = "historical-company-news-assessment-research-v1";
    public const string AssessmentMethodVersion = "issuer-rule-finbert-agreement-v1";
    public const string PinnedModelName = "ProsusAI/finbert";
    public const string CoverageOnly = "assessed_news_coverage";
    public const string NegativeVeto = "avoid_negative_company_news";
    public const string PositiveRequired = "require_positive_company_news";
    public const string HistoricalUseLimitation = "retrospective_measurement_model_historical_availability_unproven";

    // Admit only the three reviewed assessment arms; their completeness test is deliberately identical.
    public static bool IsTreatment(string? treatment) => treatment is CoverageOnly or NegativeVeto or PositiveRequired;

    // Bind each treatment family to its own admission policy without changing existing presence recipes.
    public static bool IsBoundTreatment(string? treatment, string? admissionPolicyId) =>
        IsTreatment(treatment) ? admissionPolicyId == AdmissionPolicyId :
        (treatment is HistoricalNewsDiagnosticPolicy.EvidenceChecked or HistoricalNewsDiagnosticPolicy.AvoidRecentCompanyNews) &&
            admissionPolicyId == HistoricalNewsDiagnosticPolicy.AdmissionPolicyId;
}

/// <summary>
/// Package-level classifier provenance. Unknown training history must be stated explicitly, while the
/// mandatory limitation prevents interpreting a model run today as an April-observed classifier event.
/// Original receipt/assessment times remain on the selected revision and its assessment.
/// </summary>
public sealed record HistoricalCompanyNewsAssessmentPolicy(string PolicyId, string ModelIdentity,
    string ModelArtifactSha256, string TrainingDataProvenance, string MappingVersion,
    string ExtractionVersion, string RuleVersion, string AssessmentMethodVersion,
    string HistoricalUseLimitation, string ModelProvider, string ModelVersion,
    DateTimeOffset? ModelTrainingDataCutoffUtc = null)
{
    // Reject incomplete provenance before an empty-news context can masquerade as a frozen assessment package.
    public bool IsComplete() => HasCompleteProvenance();

    // Preserve the original classifier policy while permitting only the explicitly versioned provider-name extension.
    private bool HasCompleteProvenance() => PolicyId == HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId &&
        ModelIdentity == HistoricalCompanyNewsDiagnosticPolicy.PinnedModelName && !String.IsNullOrWhiteSpace(ModelProvider) &&
        ModelVersion is { Length: 40 } && ModelVersion.All(Uri.IsHexDigit) && ModelArtifactSha256 is { Length: 64 } &&
        ModelArtifactSha256.All(Uri.IsHexDigit) && !String.IsNullOrWhiteSpace(TrainingDataProvenance) &&
        (IsLiteralPair(MappingVersion, ExtractionVersion) || IsMappedPair(MappingVersion, ExtractionVersion)) &&
        RuleVersion == EconomicNewsRuleClassifier.Version &&
        AssessmentMethodVersion == HistoricalCompanyNewsDiagnosticPolicy.AssessmentMethodVersion &&
        HistoricalUseLimitation == HistoricalCompanyNewsDiagnosticPolicy.HistoricalUseLimitation &&
        (ModelTrainingDataCutoffUtc is null || ModelTrainingDataCutoffUtc.Value != default &&
            ModelTrainingDataCutoffUtc.Value.Offset == TimeSpan.Zero);

    // A v2 package may preserve the original literal-ticker method when no verified company-name mapping exists.
    public bool AllowsExtraction(CompanyNewsExtraction extraction) => HasCompleteProvenance() &&
        (extraction.IssuerMapping is null
            ? IsLiteralPair(extraction.MappingVersion, extraction.ExtractionVersion)
            : IsMappedPair(MappingVersion, ExtractionVersion) && IsMappedPair(extraction.MappingVersion, extraction.ExtractionVersion));

    // Pair the original method versions exactly so existing immutable publications retain their original interpretation.
    private static bool IsLiteralPair(string mapping, string extraction) =>
        mapping == CompanyNewsAssessmentClassifier.MappingVersion && extraction == CompanyNewsAssessmentClassifier.ExtractionVersion;

    // Pair the provider-name mapping and extraction versions; individual version strings cannot be mixed.
    private static bool IsMappedPair(string mapping, string extraction) =>
        mapping == CompanyNewsAssessmentClassifier.MappedMappingVersion && extraction == CompanyNewsAssessmentClassifier.MappedExtractionVersion;
}
