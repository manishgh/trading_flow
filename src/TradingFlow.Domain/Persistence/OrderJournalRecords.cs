using TradingFlow.Domain.Orders;

namespace TradingFlow.Domain.Persistence;

public sealed class OrderIntentRecord : OperationalRecord
{
    public Guid IntentId { get; set; }
    public OrderIntentKind Kind { get; set; }
    public Guid? CandidateId { get; set; }
    public int? CandidateTriggeredVersion { get; set; }
    public int? CandidateConsumedVersion { get; set; }
    public string? CandidateSemanticDecisionSha256 { get; set; }
    public string? CandidateTriggeredEvidenceSha256 { get; set; }
    public string? CandidateConsumptionEvidenceSha256 { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string TimeInForce { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public long? PositionGenerationEventId { get; set; }
    public DateOnly SessionDate { get; set; }
    public int SequenceNumber { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DispatchExpiresAtUtc { get; set; }
    public string? DispatchLeaseOwner { get; set; }
    public Guid? DispatchLeaseToken { get; set; }
    public DateTimeOffset? DispatchLeaseExpiresAtUtc { get; set; }
    public int DispatchAttemptCount { get; set; }
    public DateTimeOffset? LastDispatchAttemptAtUtc { get; set; }
    public string RequestJson { get; set; } = string.Empty;
}

public sealed class OrderEventRecord : OperationalRecord
{
    public long EventId { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string? BrokerOrderId { get; set; }
    public string? PreviousState { get; set; }
    public string NewState { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? BrokerTimestampUtc { get; set; }
    public DateTimeOffset LocalTimestampUtc { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? FillPrice { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}

public enum ProtectiveStopReplacementState
{
    Pending = 1,
    Verified = 2,
    Superseded = 3
}

/// <summary>
/// Durable desired-state command for raising one broker-resting protective stop.
/// The broker PATCH is never issued until this record has committed.
/// </summary>
public sealed class ProtectiveStopReplacementRecord : OperationalRecord
{
    public Guid CommandId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string OwnerClientOrderId { get; set; } = string.Empty;
    public string RootBrokerOrderId { get; set; } = string.Empty;
    public string BrokerOrderId { get; set; } = string.Empty;
    public string ReplacementClientOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal StopPrice { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public ProtectiveStopReplacementState State { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public string? VerifiedBrokerOrderId { get; set; }
    public string? LastError { get; set; }
    public int Version { get; set; }
}

public enum PortfolioRiskReservationState
{
    PendingBrokerSubmission = 1,
    BrokerAccepted = 2,
    PartiallyFilled = 3,
    BackingOpenPosition = 4,
    Released = 5
}

/// <summary>
/// Durable capacity owned by one entry intent. Pending orders reserve every
/// resource; filled positions retain their stop-defined portfolio risk until an
/// authoritative fill journal proves the symbol is flat.
/// </summary>
public sealed class PortfolioRiskReservationRecord : OperationalRecord
{
    public Guid ReservationId { get; set; }
    public Guid IntentId { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Horizon { get; set; } = string.Empty;
    public PortfolioRiskReservationState State { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal PendingQuantity { get; set; }
    public decimal CumulativeFilledQuantity { get; set; }
    public decimal OpenPositionQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal RiskPerShare { get; set; }
    public decimal ReservedBuyingPower { get; set; }
    public decimal ReservedGrossExposure { get; set; }
    public decimal ReservedNetExposure { get; set; }
    public decimal ReservedPortfolioRisk { get; set; }
    public int ReservedPositionSlots { get; set; }
    public decimal AccountEquityAtReservation { get; set; }
    public decimal BrokerBuyingPowerAtReservation { get; set; }
    public decimal BrokerGrossExposureAtReservation { get; set; }
    public decimal BrokerNetExposureAtReservation { get; set; }
    public int BrokerPositionCountAtReservation { get; set; }
    public decimal MaxGrossExposure { get; set; }
    public decimal MaxPortfolioRisk { get; set; }
    public int MaxPositions { get; set; }
    public DateTimeOffset AccountSnapshotRequestedAtUtc { get; set; }
    public DateTimeOffset AccountSnapshotObservedAtUtc { get; set; }
    public DateTimeOffset ReservedAtUtc { get; set; }
    public DateTimeOffset StateChangedAtUtc { get; set; }
    public DateTimeOffset? BrokerAcceptedAtUtc { get; set; }
    public DateTimeOffset? LatestFillAtUtc { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public string? ReleaseReason { get; set; }
    public int Version { get; set; }
}
