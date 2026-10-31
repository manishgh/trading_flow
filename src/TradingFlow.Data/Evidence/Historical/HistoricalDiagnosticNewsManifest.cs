using System.Collections.Immutable;
using System.Text.Json.Serialization;
using TradingFlow.Data.Evidence.Sentiment;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>A captured archive rebased under the frozen research root when durable inputs are copied.</summary>
public sealed record HistoricalDiagnosticNewsArchive(string RootRelativePath, HistoricalEvidenceManifest Evidence);

/// <summary>The retained provider documentation that establishes which clock the query bounds filter.</summary>
public sealed record HistoricalDiagnosticQuerySemantics(string TimestampField, string EvidenceRelativePath, string EvidenceSha256,
    string Basis = "observed_provider_query_behavior");

/// <summary>Query success describes the exact retained response sequence, not original revision or world-news completeness.</summary>
public sealed record HistoricalDiagnosticNewsCoverage(string Provider, ImmutableArray<string> Symbols,
    DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, bool Complete, string? UnknownReason,
    ImmutableArray<string> SourceObservationIds);

/// <summary>Actual receipts remain source facts; assumed availability is explicitly separate and reproducible.</summary>
public sealed record HistoricalDiagnosticNewsRevision(int ArchiveIndex, string FactId, string Provider,
    DateTimeOffset? AssumedAvailableAtUtc, string? UnknownReason);

/// <summary>A frozen research package, with no classifier evaluation or strict historical-delivery claim.</summary>
public sealed record HistoricalDiagnosticNewsManifest(string SchemaVersion, DateTimeOffset BuiltAtUtc, string ResearchRoot,
    string PolicyId, string PolicySha256, ImmutableArray<HistoricalDiagnosticNewsArchive> Archives,
    HistoricalDiagnosticQuerySemantics? QuerySemantics, ImmutableArray<HistoricalDiagnosticNewsCoverage> QueryCoverage,
    ImmutableArray<HistoricalDiagnosticNewsRevision> Revisions, ImmutableArray<string> SelectedSymbols,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyNewsAssessmentReference? CompanyAssessmentManifest = null)
{
    public const string CurrentSchema = "historical-news-diagnostic-manifest.v1";
}

public sealed record HistoricalDiagnosticNewsDependency(string Path, string Sha256);
