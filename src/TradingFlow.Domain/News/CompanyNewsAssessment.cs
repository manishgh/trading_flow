using System.Collections.Immutable;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.News;

public enum CompanyNewsAssessmentState { Unknown = 0, Irrelevant = 1, Neutral = 2, Positive = 3, Negative = 4 }
public enum CompanyNewsPolarity { Unknown = 0, Neutral = 1, Positive = 2, Negative = 3 }

/// <summary>Exact archived fields; no cleaned text replaces the retained revision.</summary>
public sealed record CompanyNewsRevisionInput(string Provider, string ProviderArticleId, string RevisionId,
    string Headline, string? Summary = null, string? Body = null);

/// <summary>Offsets address the original field, including its complete surrounding context.</summary>
public sealed record CompanyNewsEvidenceSpan(string Field, int Start, int Length, string Text, string FullContext);

/// <summary>Frozen official-name evidence; Data verifies the exact source row and original payload digest.</summary>
public sealed record CompanyNewsIssuerMapping(string Symbol, string OfficialName, string SourceProvider,
    string SourceIdentity, string SourceContentSha256, DateTimeOffset ObservedAtUtc);

/// <summary>Canonical ticker assertions used by the rules, with exact original full-field assertion spans.</summary>
public sealed record CompanyNewsRuleInputBinding(string Headline, string? Summary, string? Body,
    ImmutableArray<CompanyNewsEvidenceSpan> AssertionSpans);

/// <summary>A frozen model identity; unknown training provenance must be recorded explicitly.</summary>
public sealed record CompanyNewsPinnedModel(string Provider, string Model, string Version, string ArtifactSha256,
    string TrainingDataProvenance, DateTimeOffset? TrainingDataCutoffUtc);

/// <summary>The model's top label is textual polarity, never the probability of a profitable trade.</summary>
public sealed record CompanyNewsModelEvidence(CompanyNewsPinnedModel Identity, string InputContentSha256,
    CompanyNewsPolarity TopLabel, decimal Confidence, DateTimeOffset CompletedAtUtc,
    bool Succeeded = true, string? FailureReason = null);

/// <summary>Inspectable extraction before inference, bound to the exact revision and literal issuer.</summary>
public sealed record CompanyNewsExtraction(CompanyNewsRevisionInput Revision, string IssuerSymbol,
    string RevisionContentSha256, string ExtractionVersion, string MappingVersion,
    EconomicNewsRuleResult RuleResult, ImmutableArray<CompanyNewsEvidenceSpan> EvidenceSpans,
    string ModelInputText, string ModelInputSha256, bool CanAssess, ImmutableArray<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyNewsIssuerMapping? IssuerMapping = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyNewsRuleInputBinding? RuleInputBinding = null);

/// <summary>Research measurement with actual completion time; historical availability is a separate policy.</summary>
public sealed record CompanyNewsAssessment(CompanyNewsExtraction Extraction, CompanyNewsAssessmentState State,
    CompanyNewsModelEvidence? ModelEvidence, ImmutableArray<string> Reasons, DateTimeOffset AssessedAtUtc)
{
    public CompanyNewsRevisionInput Revision => Extraction.Revision;
    public string IssuerSymbol => Extraction.IssuerSymbol;
    public string RevisionContentSha256 => Extraction.RevisionContentSha256;
    public string ExtractionVersion => Extraction.ExtractionVersion;
    public string MappingVersion => Extraction.MappingVersion;
    public string RuleVersion => Extraction.RuleResult.Version;
    public EconomicNewsScope Scope => Extraction.RuleResult.Scope;
    public CatalystNewsCategory Category => Extraction.RuleResult.Category;
    public CatalystDirection AssertedDirection => Extraction.RuleResult.Direction;
    public CatalystMateriality? Materiality => Extraction.RuleResult.Materiality;
    public bool ResearchOnly => true;
    public bool AccuracyMeasured => false;
    public bool TradingActivationAllowed => false;
}
