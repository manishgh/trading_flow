namespace TradingFlow.Contracts.V1;

/// <summary>Server catalog identity and expected immutable bytes; never a client filesystem path.</summary>
public sealed record DecisionContextEvidenceReference(string Id, string ManifestSha256);

/// <summary>Facts extracted from verified context documents; unknown inputs remain explicit.</summary>
public sealed record DecisionContextEvidenceSummary(DecisionContextEvidenceReference Reference, string Provider,
    string Version, IReadOnlyList<string> TaxonomyVersions, IReadOnlyList<string> ClassifierVersions,
    IReadOnlyList<string> EvaluationSha256s, IReadOnlyList<string> AvailabilityBasis,
    IReadOnlyList<string> CoverageScopes, int? EventLookbackDays, decimal? MaxCoverageAgeHours,
    IReadOnlyList<string> UnknownReasons, string DependencyGraphSha256,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    HistoricalNewsDiagnosticSummary? HistoricalNewsDiagnostic = null);

/// <summary>Displays frozen research assumptions and actual capture clocks without claiming original historical delivery.</summary>
public sealed record HistoricalNewsDiagnosticSummary(string PolicyId, string PolicySha256,
    int FreshnessHours, DateTimeOffset ActualReceivedFromUtc, DateTimeOffset ActualReceivedThroughUtc,
    IReadOnlyList<string> QueryEvidenceNotes,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CompanyNewsAssessmentSummary? CompanyAssessments = null);

/// <summary>A server profile binding, including the concrete reason an option cannot start.</summary>
public sealed record DecisionContextEvidenceChoice(DecisionContextEvidenceReference? Reference, string Name,
    bool Available, string? UnavailableReason, DecisionContextEvidenceSummary? Summary);
