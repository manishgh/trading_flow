using System.Collections.Immutable;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;

namespace TradingFlow.Domain.Strategies;

/// <summary>Optional source evidence; missing components remain unknown, never inferred.</summary>
public sealed record DrawdownDecisionContext(DrawdownHistoryContract? History = null,
    DrawdownSectorContext? Sector = null, DrawdownEventContext? Events = null);

public sealed record DrawdownSecurityIdentity(string SecurityId, string Ticker, string DataFeed,
    string AdjustmentPolicy, string AdjustmentVersion, DateTimeOffset EffectiveFromUtc,
    DateTimeOffset EffectiveToUtc, DateTimeOffset KnownAtUtc, string EvidenceSha256,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DrawdownPricePrefixBinding? PricePrefixBinding = null);

public sealed record DrawdownBenchmarkEvidence(string Symbol, string SecurityId, string AdjustmentVersion,
    DateTimeOffset KnownAtUtc, string EvidenceSha256, ImmutableArray<OhlcvBar> Bars,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    DrawdownPricePrefixBinding? PricePrefixBinding = null);

public sealed record DrawdownSectorContext(DrawdownSecurityIdentity Identity, string SectorId,
    string BenchmarkSymbol, DateTimeOffset MappingEffectiveFromUtc, DateTimeOffset MappingEffectiveToUtc,
    DateTimeOffset MappingKnownAtUtc, string MappingSha256, DateTimeOffset CalendarKnownAtUtc,
    string CalendarSha256, ImmutableArray<DateTimeOffset> ExpectedSessionTimestamps,
    DrawdownBenchmarkEvidence SectorBenchmark, DrawdownBenchmarkEvidence MarketBenchmark);

public enum DrawdownEventScope { Company = 1, Sector = 2, Global = 3 }
public enum DrawdownEventCategory { Company = 1, Sector = 2, Global = 3, Financial = 4, Policy = 5 }

/// <summary>Independent human-label evaluation bound to the exact taxonomy and classifier.</summary>
public sealed record DrawdownClassifierEvaluation(string TaxonomyVersion, string ClassifierVersion,
    string ProtocolVersion, string LabelDatasetSha256, string EvaluationSha256, bool Passed,
    DateTimeOffset EvaluatedAtUtc);

/// <summary>An empty event set is meaningful only with independent scope-specific coverage.</summary>
public sealed record DrawdownEventScopeCoverage(DrawdownEventScope Scope, string Target,
    DateTimeOffset FromUtc, DateTimeOffset ThroughUtc, DateTimeOffset KnownAtUtc, string EvidenceSha256);

public sealed record DrawdownClassifiedEvent(string Provider, string ArticleId, string RevisionId,
    string RevisionContentSha256, DateTimeOffset PublishedAtUtc, DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset ReceivedAtUtc, DateTimeOffset ClassifiedAtUtc, DateTimeOffset KnownAtUtc,
    string TaxonomyVersion, string ClassifierVersion, string EvaluationSha256,
    DrawdownEventCategory Category, DrawdownEventScope Scope, string Target,
    CatalystDirection Direction, bool Adverse);

public sealed record DrawdownHistoricalSectorMapping(string SectorId, DateTimeOffset EffectiveFromUtc,
    DateTimeOffset EffectiveToUtc, DateTimeOffset KnownAtUtc, string EvidenceSha256);

public sealed record DrawdownEventContext(DrawdownSecurityIdentity Identity, string SectorId,
    DrawdownClassifierEvaluation Evaluation, ImmutableArray<DrawdownEventScopeCoverage> ScopeCoverages,
    ImmutableArray<DrawdownClassifiedEvent> Events, DrawdownHistoricalSectorMapping? SectorMapping = null);

public sealed record DrawdownContextAudit(string? SectorUnknownReason, string? EventUnknownReason,
    IReadOnlyDictionary<string, DrawdownMetric> Metrics, int? EventCount, int? AdverseEventCount,
    int? GlobalEventCount, int? AdverseGlobalEventCount, string? ConsumedEvidenceSha256);
