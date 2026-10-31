using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Shared Web/Android wording for retrospective company assessments and unresolved meaning.</summary>
public static class CompanyNewsAssessmentPresentation
{
    /// <summary>Shows actual model and assessment clocks rather than implying an old trade observed today's classification.</summary>
    public static IReadOnlyList<string> Lines(CompanyNewsAssessmentSummary summary) => [
        "Company news assessments — retrospective research; accuracy not yet measured",
        $"Classifier: {summary.ClassifierVersion}",
        $"Model: {summary.Model}@{summary.ModelRevision}",
        $"Model artifact SHA-256: {summary.ModelArtifactSha256}",
        $"Company subject mapping: {summary.SubjectMappingVersion}",
        $"Training-data cutoff bound UTC: {summary.TrainingDataCutoffUtc?.ToString("O") ?? "Unknown"}. {summary.TrainingDataProvenance}",
        $"Actually assessed UTC: {summary.AssessedFromUtc:O} through {summary.AssessedThroughUtc:O}",
        $"Assessments: {summary.AssessmentCount}. " + String.Join(", ", summary.StateCounts.OrderBy(item => item.Key)
            .Select(item => $"{item.Key}: {item.Value}")),
        "Model confidence describes its text label, not the probability of a profitable trade."];

    /// <summary>Names the supported effect on this company without converting uncertainty into neutral news.</summary>
    public static string DirectionLabel(string state) => state.ToLowerInvariant() switch
    {
        "positive" => "Positive for this company",
        "negative" => "Negative for this company",
        "neutral" => "Neutral for this company",
        "irrelevant" => "Not relevant to this company",
        _ => "Company impact uncertain"
    };
}
