namespace TradingFlow.Domain.ScreenCaptures;

public enum FinvizSlotKind { BeforeOpen, AfterClose }

public enum FinvizCaptureState { Pending, Captured, VerifiedEmpty, Partial, Failed, Missed }

public enum FinvizColumnCoverage { Unverified, Baseline, Unexpected, Reduced }

/// <summary>One screen observation opportunity, keyed independently of the live preset row.</summary>
public sealed class FinvizCaptureSlot
{
    public Guid Id { get; set; }
    public Guid PresetId { get; set; }
    public DateOnly SessionDate { get; set; }
    public FinvizSlotKind Kind { get; set; }
    public DateTimeOffset PlannedAtUtc { get; set; }
    public string? PresetName { get; set; }
    public DateTimeOffset? PresetUpdatedAtUtc { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public bool? PlannedRevisionKnown { get; set; }
    public string? Query { get; set; }
    public string? QuerySha256 { get; set; }
    public string? ExportFormat { get; set; }
    public FinvizCaptureState State { get; set; }
    public string? Reason { get; set; }
    public int AttemptCount { get; set; }
    public long? RetryNotBeforeUtcTicks { get; set; }
    public Guid? EvidenceAttemptId { get; set; }
    public Guid? PublishedAttemptId { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

/// <summary>The durable pre-request correlation and the subsequently observed outcome of one attempt.</summary>
public sealed class FinvizCaptureAttempt
{
    public Guid Id { get; set; }
    public Guid SlotId { get; set; }
    public int Number { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? ObservationJson { get; set; }
}

/// <summary>Calendar progress survives a stopped host and bounds historical missed-slot reconciliation.</summary>
public sealed class FinvizCaptureSchedule
{
    public int Id { get; set; }
    public DateOnly NextUnclosedSessionDate { get; set; }
    public DateTimeOffset? CalendarCheckedAtUtc { get; set; }
    public DateTimeOffset? NextSlotAtUtc { get; set; }
    public string? CalendarError { get; set; }
}

public sealed record FinvizPlannedSlot(DateOnly SessionDate, FinvizSlotKind Kind, DateTimeOffset PlannedAtUtc)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public DateTimeOffset DeadlineUtc => PlannedAtUtc + Window;
}

/// <summary>Exact transport clocks and receipt references plus conservative inspection of the archived CSV.</summary>
public sealed record FinvizExportObservation(
    FinvizCaptureState State,
    string Reason,
    DateTimeOffset? ReceivedAtUtc = null,
    DateTimeOffset? HeadersReceivedAtUtc = null,
    DateTimeOffset? DispatchedAtUtc = null,
    int? HttpStatus = null,
    string? ArchiveId = null,
    string? Sha256 = null,
    long? ArchivedBytes = null,
    long? DeclaredBytes = null,
    long? MinimumBytesObserved = null,
    bool CompletePayload = false,
    string[]? Headers = null,
    int? RowCount = null,
    FinvizColumnCoverage ColumnCoverage = FinvizColumnCoverage.Unverified,
    bool MembershipVerified = false,
    DateTimeOffset? RetryAfterUtc = null);

public sealed record FinvizExportRequest(
    Guid PresetId,
    Guid CorrelationId,
    string Query,
    string QuerySha256,
    string Format,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset DeadlineUtc);
