using System.Text.Json.Serialization;

namespace TradingFlow.Contracts.V1;

public sealed record ChartSeriesKey(string Symbol, string Interval, string SessionView, string Context = "auto", string Indicators = "");

public sealed record ChartSeriesEvidence(
    string Provider,
    string Feed,
    string AdjustmentPolicy,
    string Coverage,
    DateTimeOffset? CoverageThroughUtc,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? LastSourceObservedUtc,
    string? SourceGeneration,
    bool Reconstructed,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DateTimeOffset> BasisBreaks);

public sealed record ChartCandle(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateOnly SessionDate,
    string Segment,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    string SourceKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? KnownAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetrievedAtUtc,
    DateTimeOffset AvailableAtUtc,
    string CompletionState = "completed");

public sealed record ChartZone(
    string Id,
    string Type,
    decimal Lower,
    decimal Upper,
    int PivotCount,
    string SourceTimeframe,
    DateTimeOffset FirstPivotUtc,
    DateTimeOffset LatestConfirmationUtc,
    DateTimeOffset EffectiveAtUtc,
    IReadOnlyList<DateOnly> SourceDates,
    bool IsPriceInBand,
    DateTimeOffset? UntilUtc = null,
    bool CurrentOnly = false);

public sealed record ChartLevelPoint(DateTimeOffset StartUtc, decimal Value, DateTimeOffset AvailableAtUtc);

public sealed record ChartLevel(
    string Key,
    string Label,
    decimal Value,
    decimal? DistancePct,
    DateTimeOffset EffectiveAtUtc,
    string SourceTimeframe,
    DateOnly? SourceDate = null,
    IReadOnlyList<ChartLevelPoint>? Points = null);

public sealed record ChartMarker(
    string Id,
    string Kind,
    DateTimeOffset EventAtUtc,
    DateTimeOffset? KnownAtUtc,
    string Title,
    string? Detail,
    string Source,
    string? Url = null,
    bool IsEstimate = false);

// Values are column-major and share the snapshot's bar indices. Availability runs are [index, count, UTC milliseconds].
public sealed record ChartIndicatorSeries(
    string Id,
    string SourceInterval,
    string SessionView,
    string AdjustmentPolicy,
    string FormulaVersion,
    string Parameters,
    DateTimeOffset? CutoffUtc,
    DateTimeOffset? AnchorUtc,
    string? SourceRevision,
    bool Reconstructed,
    DateTimeOffset? AvailableAtUtc,
    string Status,
    string? Reason,
    IReadOnlyList<string> Columns,
    IReadOnlyList<double?[]> Samples,
    IReadOnlyList<int> SegmentStarts,
    IReadOnlyList<long[]> AvailabilityRuns,
    int? SeedObservations = null,
    int? WarmupObservations = null,
    IReadOnlyList<DateTimeOffset?>? SegmentAnchorsUtc = null,
    int? BaselineSessions = null,
    int? BaselineSampleCount = null);

public sealed record ChartSeriesSnapshot(
    ChartSeriesKey SeriesKey,
    DateTimeOffset AsOfUtc,
    string Status,
    string? Reason,
    ChartSeriesEvidence Evidence,
    IReadOnlyList<ChartCandle> Bars,
    IReadOnlyList<ChartZone> Zones,
    IReadOnlyList<ChartLevel> Levels,
    IReadOnlyList<ChartMarker> Markers,
    int ContractVersion = 2,
    string? Epoch = null,
    long Sequence = 0,
    string? HistoryCursor = null,
    IReadOnlyList<ChartIndicatorSeries>? Indicators = null);
