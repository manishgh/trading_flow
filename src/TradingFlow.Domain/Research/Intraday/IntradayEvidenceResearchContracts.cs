using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.Research.Intraday;

public enum IntradayEvidenceEligibility
{
    PromotionEligible = 1,
    DiagnosticOnly = 2,
    Rejected = 3
}

public enum IntradayMorphology
{
    None = 0,
    OpeningRangeContinuation = 1,
    PullbackReclaim = 2,
    FailedBreak = 3
}

public sealed record IntradayClassifierValidation(
    int LabeledStoryCount,
    int DoubleLabeledStoryCount,
    decimal CategoryPrecision,
    decimal CategoryRecall,
    decimal CategoryF1,
    decimal DirectionPrecision,
    decimal CohenKappa,
    bool PassedFrozenValidation)
{
    public bool MeetsTrackBMinimum =>
        PassedFrozenValidation &&
        LabeledStoryCount >= 500 &&
        DoubleLabeledStoryCount >= 100;
}

public sealed record IntradayQuoteEvidence(
    string Ticker,
    DateTimeOffset TimestampUtc,
    decimal Bid,
    decimal Ask,
    string Feed);

public sealed record PointInTimeSectorEvidence(
    string Ticker,
    string SectorCode,
    string BenchmarkTicker,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset EffectiveToUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record IntradayEvidenceStudyOptions
{
    public IReadOnlyList<TimeSpan> Horizons { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
         TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    public IReadOnlyList<CatalystStudyPartitionDefinition> Partitions { get; init; } =
        Array.Empty<CatalystStudyPartitionDefinition>();

    public int PriorSessionTarget { get; init; } = 63;
    public int PriorSessionMinimum { get; init; } = 40;
    public TimeSpan OpeningRange { get; init; } = TimeSpan.FromMinutes(15);
    public bool AllowProviderTimestampDiagnosticMode { get; init; }
}

public sealed record CatalystStudyPartitionDefinition(
    string Label,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record IntradayEvidenceStudyRequest(
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>> CatalystsByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<IntradayQuoteEvidence>> QuotesByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BenchmarkBarsByTicker,
    IReadOnlyList<PointInTimeSectorEvidence> SectorMembership,
    IReadOnlyList<ClassifiedCatalystEvidenceRow> Classifications,
    IntradayClassifierValidation ClassifierValidation,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleTimeframe,
    IntradayEvidenceStudyOptions Options);

public sealed record IntradayEvidenceStudyReport(
    DateTimeOffset GeneratedAtUtc,
    IntradayEvidenceEligibility Eligibility,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<IntradayEventEvidenceObservation> Observations,
    IReadOnlyList<IntradayHolmPValue> HolmReadyPValues);

public sealed record IntradayEventEvidenceObservation(
    string Ticker,
    string GlobalStoryCluster,
    string NewsRevisionId,
    string Partition,
    string EventSession,
    string ResponseSession,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset? ObservedReceiptAtUtc,
    DateTimeOffset ClassifierCompletedAtUtc,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset ResponseAnchorCompletedAtUtc,
    CatalystNewsCategory Category,
    CatalystDirection Direction,
    CatalystMateriality Materiality,
    IntradayEvidenceEligibility Eligibility,
    string? CensorReason,
    decimal? CumulativeRegularSessionRvol,
    int ComparableSessionCount,
    decimal? RobustLogVolumeZScore,
    decimal? PremarketDollarVolume,
    decimal? SpreadBps,
    decimal? GapPct,
    IntradayMorphology Morphology,
    IReadOnlyList<IntradayMorphologyEvidence> MorphologyTimeline,
    IReadOnlyList<IntradayHorizonEvidence> Horizons);

public sealed record IntradayMorphologyEvidence(
    DateTimeOffset CompletedAtUtc,
    IntradayMorphology State,
    decimal Close,
    decimal? SessionVwap);

public sealed record IntradayHorizonEvidence(
    string Horizon,
    DateTimeOffset? TargetCompletedAtUtc,
    decimal? RawSignedReturnPct,
    decimal? SpyAbnormalReturnPct,
    decimal? SectorAbnormalReturnPct,
    decimal? ExecutableReturnPct,
    decimal? MfePct,
    decimal? MaePct,
    bool IsCensored,
    string? CensorReason);

public sealed record IntradayHolmPValue(
    string Family,
    string Hypothesis,
    int SampleCount,
    decimal RawPValue,
    int HolmRank,
    int FamilySize);
