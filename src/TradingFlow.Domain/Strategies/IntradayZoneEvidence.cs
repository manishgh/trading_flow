using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.Strategies;

public enum IntradayZoneRole { Support, Resistance }
public enum IntradayZonePivotKind { Peak, Valley }

/// <summary>Calendar supplied by the shared exchange-session resolver, newest prior session first.</summary>
public sealed record IntradayZoneSessionContext(DateOnly SessionDate, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc,
    IReadOnlyList<DateOnly> PreviousSessionDates, DateTimeOffset? PreviousSessionCloseUtc = null)
{
    public DateTimeOffset DeadlineUtc => CloseUtc.AddMinutes(-5);
}

public sealed record IntradayZonePivotEvidence(IntradayZonePivotKind Kind, DateTimeOffset PivotBarStartUtc,
    DateTimeOffset PivotCompletedAtUtc, DateTimeOffset ConfirmedAtUtc, DateOnly PivotSession, decimal Bottom, decimal Top);

/// <summary>Every published version is immutable; KnownAtUtc is the availability of this exact geometry and role.</summary>
public sealed record IntradayZoneEvidence(long ZoneId, int Version, IntradayZoneRole Role, decimal Bottom, decimal Top,
    int PivotCount, DateTimeOffset CreatedAtUtc, DateTimeOffset KnownAtUtc, DateTimeOffset FirstPivotBarStartUtc,
    DateOnly LatestPivotSession, IntradayZonePivotEvidence LatestPivot);

public sealed record IntradayZoneTransition(IntradayZoneEvidence Before, IntradayZoneEvidence After,
    DateTimeOffset FlippedAtUtc, bool CanArmRetest, decimal Atr,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OhlcvBar? FlipBar = null);

public sealed record IntradayZoneSnapshot(string Ticker, DateTimeOffset AsOfUtc, DateOnly SessionDate,
    IReadOnlyList<IntradayZoneEvidence> Zones, IReadOnlyList<IntradayZoneTransition> Transitions, bool CoverageKnown = true);

public sealed record IntradayZoneUpdate(IntradayZoneSnapshot Snapshot, string? RejectionReason = null);

public sealed record IntradayZoneSignalEvidence(IntradayZoneEvidence Zone, DateTimeOffset? FlippedAtUtc,
    DateTimeOffset ConfirmationCompletedAtUtc, decimal? Vwap, decimal? SlotRelativeVolume, decimal? SlotMedianVolume,
    int SlotRelativeVolumeSampleCount, string? MarketEvidenceProfileVersion,
    OhlcvBar? PreviousBar = null, decimal? Atr = null, IntradayZoneEvidence? TargetZone = null,
    decimal? RawStopPrice = null, decimal? RawTargetPrice = null, string? PolicyId = null,
    DateTimeOffset? SessionOpenUtc = null, DateTimeOffset? SessionCloseUtc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PythonZoneVolumeSpreadEvidence? VolumeSpread = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IntradayZoneTargetPlanningEvidence? TargetPlan = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LongBounceVolumeMode = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? StopBasis = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IntradayZoneRetestEvidence? Retest = null);

/// <summary>The completed upward flip freezes its source independently of later same-role merges.</summary>
public sealed record IntradayZoneArmedRetestEvidence(IntradayZoneTransition Transition, DateOnly SessionDate)
{
    [System.Text.Json.Serialization.JsonIgnore] public IntradayZoneEvidence Zone => Transition.After;
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset FlippedAtUtc => Transition.FlippedAtUtc;
}

/// <summary>Preserve both the frozen arm and the currently active source version known at confirmation start.</summary>
public sealed record IntradayZoneRetestEvidence(IntradayZoneTransition ArmTransition, IntradayZoneEvidence ActiveSourceAtConfirmationStart,
    DateTimeOffset ConfirmationSnapshotAsOfUtc);

/// <summary>Retain a cancelled frozen arm and the exact completed observation that ended its eligibility.</summary>
public sealed record IntradayZoneRetestCancellationEvidence(IntradayZoneArmedRetestEvidence ArmedRetest, string Reason);

/// <summary>One bounded per-source emission event, independent of later selection or portfolio rejection.</summary>
public sealed record IntradayZoneRetestConsumptionEvidence(IntradayZoneArmedRetestEvidence ArmedRetest, DateTimeOffset EmittedAtUtc);

/// <summary>Frozen target origin and admission estimates; unresolved risk targets have no invented target-zone evidence.</summary>
public sealed record IntradayZoneTargetPlanningEvidence(string TargetOrigin, decimal? FallbackNetRiskMultiple,
    int? PlannedQuantity = null, decimal? PlannerLossDollars = null, decimal? PlannerRiskBudgetDollars = null,
    decimal? EstimatedEntryFillPrice = null, decimal? EstimatedStopFillPrice = null, decimal? EstimatedEntryFees = null,
    decimal? EstimatedStopExitFees = null, decimal? EstimatedTargetFees = null, decimal? EstimatedNetStopRisk = null,
    decimal? EstimatedNetReward = null, decimal? EstimatedNetRewardRisk = null, decimal? AdverseCostBps = null,
    decimal? ResolvedTargetPrice = null);

/// <summary>Synthetic confirmation quotes and, after chronological sizing, the literal ticket-fee ratio.</summary>
public sealed record PythonZoneVolumeSpreadEvidence(decimal BidPrice, decimal AskPrice, decimal FullSpreadBps,
    decimal MaximumSpreadBps, decimal MinimumRewardRisk, int OrderedAlternativeIndex,
    int? PlannedQuantity = null, decimal? EstimatedRoundTripTicketFees = null,
    decimal? EstimatedNetRiskDollars = null, decimal? EstimatedNetRewardDollars = null, decimal? SelectedRewardRisk = null);

public sealed record IntradayZoneSignal(string Direction, string Pattern, OhlcvBar ConfirmationBar,
    decimal EntryReferencePrice, decimal InitialStopPrice, decimal TickSize, DateTimeOffset ActiveAtUtc,
    DateTimeOffset SessionDeadlineUtc, IntradayZoneSignalEvidence Evidence, int MaxFilledParentTradesPerSession = 2,
    DateTimeOffset? EntryCutoffUtc = null, decimal? FixedTargetPrice = null);

public sealed record IntradayZoneDecisionContext(IntradayZoneSnapshot Snapshot, IntradayZoneSignal? Signal,
    string? RejectionReason = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneSignal>? OrderedAlternatives = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IntradayZoneEvaluationTrace? Trace = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneArmedRetestEvidence>? ArmedRetests = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneRetestCancellationEvidence>? RetestCancellations = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneRetestConsumptionEvidence>? RetestConsumptions = null);

/// <summary>Bounded completed-bar diagnostics containing only the current causal snapshot and compact pattern predicates.</summary>
public sealed record IntradayZoneEvaluationTrace(string Ticker, DateTimeOffset AsOfUtc, OhlcvBar ConfirmationBar,
    OhlcvBar? PreviousBar, DateTimeOffset SnapshotAsOfUtc, bool CoverageKnown, string? VolumeMode, string? TargetPolicy,
    decimal? SlotRelativeVolume, decimal? SlotMedianVolume, int SlotSampleCount, decimal? Atr, string? RejectionReason,
    IReadOnlyList<IntradayZonePatternTrace> Alternatives,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneRetestCancellationEvidence>? RetestCancellations = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IntradayZoneRetestConsumptionEvidence>? RetestConsumptions = null);

public sealed record IntradayZonePatternTrace(IntradayZoneEvidence Zone, string Pattern, string Direction,
    bool? PreviousPredicate, bool? CloseBeyondZone, bool? CandleDirection, bool? VolumeEvidenceValid,
    bool? VolumeThresholdPassed, bool VolumeThresholdRequired, IntradayZoneEvidence? TargetZone, string? Reason,
    decimal? TickSize = null, decimal? RawStopPrice = null, decimal? RoundedStopPrice = null,
    decimal? RawTargetPrice = null, decimal? RoundedTargetPrice = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? StopBasis = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IntradayZoneRetestEvidence? Retest = null);
