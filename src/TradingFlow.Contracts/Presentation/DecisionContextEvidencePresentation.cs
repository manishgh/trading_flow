using System.Globalization;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Displays the server's frozen evidence facts without evaluating coverage or classification.</summary>
public static class DecisionContextEvidencePresentation
{
    /// <summary>Names the same evidence fields shown in the Web client, retaining full immutable hashes.</summary>
    public static IReadOnlyList<string> Lines(DecisionContextEvidenceSummary? summary)
    {
        if (summary is null) return [];
        if (summary.HistoricalNewsDiagnostic is { } diagnostic)
            return diagnostic.CompanyAssessments is { } assessments
                ? DiagnosticLines(summary, diagnostic).Concat(CompanyNewsAssessmentPresentation.Lines(assessments)).ToArray()
                : DiagnosticLines(summary, diagnostic);
        return [
            $"Evidence: {summary.Reference.Id}",
            $"Manifest SHA-256: {summary.Reference.ManifestSha256}",
            $"Provider: {summary.Provider} · Version: {summary.Version}",
            $"Taxonomy versions: {Joined(summary.TaxonomyVersions)}",
            $"Classifier versions: {Joined(summary.ClassifierVersions)}",
            $"Evaluation SHA-256: {Joined(summary.EvaluationSha256s)}",
            $"Availability basis: {Joined(summary.AvailabilityBasis)}",
            $"Coverage scopes: {Joined(summary.CoverageScopes)}",
            $"Event lookback days: {summary.EventLookbackDays?.ToString(CultureInfo.InvariantCulture) ?? "Not specified"}",
            $"Maximum coverage age hours: {summary.MaxCoverageAgeHours?.ToString("G29", CultureInfo.InvariantCulture) ?? "Not specified"}",
            $"Dependency graph SHA-256: {summary.DependencyGraphSha256}",
            $"Unknown reasons: {Joined(summary.UnknownReasons, "; ")}"];
    }

    /// <summary>Shows assumed availability beside real receipts; incomplete per-decision evidence remains explicit.</summary>
    private static IReadOnlyList<string> DiagnosticLines(DecisionContextEvidenceSummary summary,
        HistoricalNewsDiagnosticSummary diagnostic) => [
        "Historical news diagnostic — provider update + 24h, then next session",
        $"Evidence: {summary.Reference.Id}",
        $"Manifest SHA-256: {summary.Reference.ManifestSha256}",
        $"Policy: {diagnostic.PolicyId}",
        $"Policy SHA-256: {diagnostic.PolicySha256}",
        $"Publication age: up to {diagnostic.FreshnessHours} hours",
        $"Actual receipts UTC: {diagnostic.ActualReceivedFromUtc:O} through {diagnostic.ActualReceivedThroughUtc:O}",
        "Original historical availability and classifier accuracy are unproved. Research only.",
        $"Query evidence: {Joined(diagnostic.QueryEvidenceNotes, "; ")}",
        $"Dependency graph SHA-256: {summary.DependencyGraphSha256}",
        $"Unknown reasons: {Joined(summary.UnknownReasons, "; ")}"];

    /// <summary>Keeps absence explicit instead of treating omitted server facts as a passed check.</summary>
    private static string Joined(IReadOnlyList<string> values, string separator = ", ") =>
        values.Count == 0 ? "None reported" : String.Join(separator, values);
}
