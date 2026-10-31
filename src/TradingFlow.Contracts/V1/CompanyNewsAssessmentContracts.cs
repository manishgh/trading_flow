namespace TradingFlow.Contracts.V1;

/// <summary>Server-verified model and text-assessment facts, without implying measured accuracy or trading permission.</summary>
public sealed record CompanyNewsAssessmentSummary(string ClassifierVersion, string Model,
    string ModelRevision, string ModelArtifactSha256, string SubjectMappingVersion,
    DateTimeOffset? TrainingDataCutoffUtc, string TrainingDataProvenance,
    DateTimeOffset AssessedFromUtc, DateTimeOffset AssessedThroughUtc,
    int AssessmentCount, IReadOnlyDictionary<string, int> StateCounts);

/// <summary>An archived company assessment for operator review; text tone and company direction remain separate.</summary>
public sealed record CompanyNewsAssessmentSample(string Symbol, string Headline,
    DateTimeOffset? PublishedAtUtc, string Category, string CompanyDirection,
    string? TextTone, decimal? ModelConfidence, string Explanation,
    string EvidenceText, string TextSupport, DateTimeOffset AssessedAtUtc,
    string RevisionId, string RevisionContentSha256,
    string? CompanyName = null, string? CompanyNameProvider = null,
    DateTimeOffset? CompanyNameObservedAtUtc = null);

/// <summary>A read-only publication preview; it never triggers collection, classification, or portfolio execution.</summary>
public sealed record CompanyNewsAssessmentPreview(string? PublicationId, string? ManifestSha256,
    CompanyNewsAssessmentSummary? Summary, IReadOnlyList<CompanyNewsAssessmentSample> Samples,
    string Message);
