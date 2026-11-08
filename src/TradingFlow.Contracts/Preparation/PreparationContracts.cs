namespace TradingFlow.Contracts.Preparation;

public sealed record PreparationSource(string Kind, string Key, bool Required = true, Guid? WishlistId = null, string? Query = null);
public sealed record PreparationFilters(decimal? MinimumPrice = null, decimal? MinimumAverageShares = null,
    decimal? MinimumAverageDollarVolume = null, int? TrendAverageDays = null, decimal? MaximumRangePercent = null);
public sealed record PreparationSettings
{
    public string Name { get; init; } = "Swing preparation";
    public bool Enabled { get; init; }
    public string Calendar { get; init; } = "us-equities";
    public string TimeZone { get; init; } = "America/New_York";
    public PreparationSource[] Sources { get; init; } = [];
    public string Merge { get; init; } = "union";
    public int NightlyAfterCloseMinutes { get; init; } = 45;
    public int MorningStartBeforeOpenMinutes { get; init; } = 90;
    public int PublicationBeforeOpenMinutes { get; init; } = 15;
    public int HandoffBeforeOpenMinutes { get; init; }
    public int RefreshMinutes { get; init; } = 15;
    public int CatchUpMinutes { get; init; }
    public int MaximumSourceAgeMinutes { get; init; } = 30;
    public int SnapshotLifetimeMinutes { get; init; } = 30;
    public int MaximumSymbols { get; init; } = 500;
    public int ParallelSymbols { get; init; } = 4;
    public int HistorySessions { get; init; } = 126;
    public int MaximumAttempts { get; init; } = 3;
    public int RetrySeconds { get; init; } = 30;
    public int CollectionTimeoutSeconds { get; init; } = 120;
    public bool FreshDiscoveries { get; init; } = true;
    public bool StandaloneMorning { get; init; }
    public bool NotifyReady { get; init; } = true;
    public bool NotifyBlocked { get; init; } = true;
    public bool NotifyFailed { get; init; } = true;
    public PreparationFilters Filters { get; init; } = new();
    public decimal? GapAnnotationPercent { get; init; }
    public decimal? RelativeVolumeAnnotation { get; init; }
    public int? EarningsWithinDays { get; init; }
}
public sealed record PreparationDefinition(Guid WorkflowId, int Revision, string ContentSha256,
    DateTimeOffset EffectiveAtUtc, PreparationSettings Settings);
public sealed record SavePreparationRequest(Guid WorkflowId, int ExpectedRevision, PreparationSettings Settings);
public sealed record PreparationSession(DateOnly Date, bool TradingDay, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc);
public sealed record PreparationCalendar(DateTimeOffset RetrievedAtUtc, string Provider, PreparationSession[] Sessions);
public sealed record PreparationSlot(DateOnly TargetDate, string Phase, DateTimeOffset ScheduledAtUtc,
    DateTimeOffset DeadlineUtc, DateTimeOffset HandoffUntilUtc, PreparationSession PreviousSession, PreparationCalendar Calendar);
public sealed record PreparationCurationRequest(Guid ParentRevisionId, string[] AddSymbols, string[] RemoveSymbols, string Reason, string IdempotencyKey);
public sealed record PreparationCurationEvidence(PreparationCurationRequest Request, string Actor, Guid WishlistId, DateTimeOffset ObservedAtUtc, string[]? EffectiveAddSymbols = null, string[]? EffectiveRemoveSymbols = null);
public sealed record PreparationRunRequest(string Phase, string IdempotencyKey);
public sealed record PreparationSourceEvidence(PreparationSource Source, string Status, string? Reason,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? ExpiresAtUtc, string? ContentSha256, string? Reference, string[] Symbols);
public sealed record PreparationNews(string Id, string Headline, string? Url, string Provider, DateTimeOffset PublishedAtUtc, DateTimeOffset KnownAtUtc);
public sealed record PreparationSymbol(string Symbol, string Change, string Readiness, string[] Reasons,
    DateOnly? DailyDate, int HistoryCount, decimal? Close, decimal? AverageShares, decimal? AverageDollarVolume,
    decimal? TrendAverage, decimal? RangePercent, decimal? GapPercent, decimal? SameTimeRelativeVolume,
    string Feed, string Adjustment, string EvidenceSha256, PreparationNews[] News, string[] Context, string? InputEvidenceJson = null);
public sealed record PreparationSnapshot(Guid RevisionId, Guid? ParentRevisionId, DateTimeOffset CutoffUtc,
    DateTimeOffset PublishedAtUtc, DateTimeOffset ExpiresAtUtc, string Readiness, string Outcome,
    string[] Reasons, PreparationSourceEvidence[] Sources, PreparationSymbol[] Symbols, string[] RemovedSymbols, PreparationCurationEvidence? Curation = null);
public sealed record PreparationOccurrence(Guid Id, Guid WorkflowId, int DefinitionRevision, PreparationSlot Slot,
    string Status, string? Error, PreparationSnapshot? Snapshot);
public sealed record PreparationDetail(PreparationDefinition Definition, PreparationSlot[] Upcoming, PreparationOccurrence[] History,
    string? PlanningError = null, PreparationSlot[]? CollectionWindows = null, DateTimeOffset? ReadAtUtc = null);
public sealed record PreparationHandoffRequest(Guid RevisionId, string[] Symbols, string Reason, string IdempotencyKey);
public sealed record PreparationAlert(long EventSequence, DateTimeOffset OccurredAtUtc, string PayloadJson);
public sealed record PreparationAlertTarget(Guid WorkflowId, Guid OccurrenceId, Guid RevisionId, string Readiness);
public sealed record PreparationHandoff(Guid ReviewId, Guid WishlistId, Guid UniverseSnapshotId, DateTimeOffset ExpiresAtUtc,
    Guid ParentRevisionId, string[] Symbols, string Reason);
