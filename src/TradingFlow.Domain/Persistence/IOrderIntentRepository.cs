using TradingFlow.Domain.Orders;

namespace TradingFlow.Domain.Persistence;

public sealed record ActiveOrderIntent(
    string ClientOrderId,
    string StrategyId,
    string Symbol,
    string Side,
    OrderState State);

/// <summary>
/// Reserves immutable order intents and their initial lifecycle event before broker submission.
/// </summary>
public interface IOrderIntentRepository
{
    Task<ProductionRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<OrderIntentRecord?> GetByIntentIdAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task<OrderIntentRecord?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderIntentRecord>> ListByRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<PortfolioRiskReservationRecord?> GetRiskReservationByIntentIdAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveOrderIntent>> ListActiveProtectiveForSymbolAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default);

    Task<bool> HasActivePositionExitAsync(
        string accountId,
        string symbol,
        long positionGenerationEventId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderIntentRecord>> ListUnpreparedPositionExitsAsync(
        string accountId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> TryExpireUnattemptedUnleasedPositionExitAsync(
        Guid intentId,
        string reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates the owning run and reserves one immutable intent. A strategy
    /// reservation consumes its Triggered candidate in the same transaction. Repeating
    /// the same intent ID returns the original row and therefore the original client
    /// order ID.
    /// </summary>
    Task<OrderIntentReservationResult> ReserveAsync(
        ProductionRun run,
        OrderIntentReservation reservation,
        CancellationToken cancellationToken = default);
}

public sealed record OrderDispatchLease(
    OrderIntentRecord Intent,
    OrderState State,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc);

/// <summary>
/// Serializes ownership of broker dispatch without making a database lease the
/// external idempotency boundary. The persisted client order ID remains that boundary.
/// </summary>
public interface IOrderDispatchRepository
{
    Task<IReadOnlyList<Guid>> ListRecoverableIntentIdsAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<OrderDispatchLease?> TryAcquireDispatchLeaseAsync(
        Guid intentId,
        string accountId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<OrderIntentRecord> RecordDispatchAttemptAsync(
        Guid intentId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    Task ReleaseDispatchLeaseAsync(
        Guid intentId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);
}

public sealed class OrderDispatchLeaseLostException(Guid intentId) : InvalidOperationException(
    $"Dispatch ownership for intent {intentId:N} was lost before broker submission.")
{
    public Guid IntentId { get; } = intentId;
}

public sealed class OrderDispatchExpiredException(
    Guid intentId,
    DateTimeOffset expiredAtUtc) : InvalidOperationException(
        $"Dispatch for intent {intentId:N} expired at {expiredAtUtc:O} before broker submission.")
{
    public Guid IntentId { get; } = intentId;
    public DateTimeOffset ExpiredAtUtc { get; } = expiredAtUtc;
}

public sealed class OrderDispatchAdoptionRequiredException(Guid intentId) : InvalidOperationException(
    $"Intent {intentId:N} has a prior broker attempt and requires adoption; local expiry cannot prove that no broker order exists.")
{
    public Guid IntentId { get; } = intentId;
}

public sealed class OrderDispatchRunInactiveException(Guid intentId) : InvalidOperationException(
    $"Intent {intentId:N} belongs to a run that no longer accepts new broker submissions.")
{
    public Guid IntentId { get; } = intentId;
}

public sealed class OrderCancellationPendingException(
    string clientOrderId,
    string brokerOrderId) : InvalidOperationException(
        $"Cancellation for client order '{clientOrderId}' and broker order '{brokerOrderId}' is still pending broker confirmation.")
{
    public string ClientOrderId { get; } = clientOrderId;
    public string BrokerOrderId { get; } = brokerOrderId;
}

public sealed record OrderIntentReservationResult(
    OrderIntentRecord Intent,
    bool Created,
    string OwningRunStatus);

public sealed class CandidateOrderIntentConflictException(
    Guid candidateId,
    Guid requestedIntentId,
    Guid? owningIntentId = null,
    Exception? innerException = null) : InvalidOperationException(
        owningIntentId is { } owner
            ? $"Candidate {candidateId:N} is already owned by intent {owner:N}; intent {requestedIntentId:N} was rejected."
            : $"Candidate {candidateId:N} could not be reserved by intent {requestedIntentId:N} because another intent won the reservation.",
        innerException)
{
    public Guid CandidateId { get; } = candidateId;
    public Guid RequestedIntentId { get; } = requestedIntentId;
    public Guid? OwningIntentId { get; } = owningIntentId;
}

public sealed class ExpiredCandidateOrderIntentException(
    Guid candidateId,
    Guid requestedIntentId,
    DateTimeOffset expiredAtUtc) : InvalidOperationException(
        $"Candidate {candidateId:N} expired at {expiredAtUtc:O} before intent {requestedIntentId:N} could consume it.")
{
    public Guid CandidateId { get; } = candidateId;
    public Guid RequestedIntentId { get; } = requestedIntentId;
    public DateTimeOffset ExpiredAtUtc { get; } = expiredAtUtc;
}

/// <summary>
/// Broker-neutral data required to reserve an order intent before any network submission.
/// The repository allocates the per-session sequence and client order ID transactionally.
/// </summary>
public sealed record OrderIntentReservation(
    Guid IntentId,
    OrderIntentKind Kind,
    Guid? CandidateId,
    string AccountId,
    string StrategyId,
    string Symbol,
    string Side,
    string OrderType,
    string TimeInForce,
    decimal RequestedQuantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    DateOnly SessionDate,
    DateTimeOffset CreatedAtUtc,
    string RequestJson,
    DateTimeOffset? DispatchExpiresAtUtc = null,
    PortfolioRiskReservationRequest? PortfolioRisk = null,
    int? CandidateExpectedVersion = null,
    string? CandidateSemanticDecisionSha256 = null,
    long? PositionGenerationEventId = null);

public sealed class PositionExitReservationRejectedException(
    Guid intentId,
    string accountId,
    string symbol,
    long positionGenerationEventId,
    decimal requestedQuantity,
    decimal availableQuantity) : InvalidOperationException(
        $"Exit intent {intentId:N} requested {requestedQuantity} {symbol} units, but only " +
        $"{availableQuantity} remain unreserved for position generation {positionGenerationEventId}.")
{
    public Guid IntentId { get; } = intentId;
    public string AccountId { get; } = accountId;
    public string Symbol { get; } = symbol;
    public long PositionGenerationEventId { get; } = positionGenerationEventId;
    public decimal RequestedQuantity { get; } = requestedQuantity;
    public decimal AvailableQuantity { get; } = availableQuantity;
}

public sealed class PositionExitInProgressException(
    string accountId,
    string symbol,
    long positionGenerationEventId) : InvalidOperationException(
        $"Position generation {positionGenerationEventId} for {symbol} in account '{accountId}' already has an active exit; protective-order creation is deferred.")
{
    public string AccountId { get; } = accountId;
    public string Symbol { get; } = symbol;
    public long PositionGenerationEventId { get; } = positionGenerationEventId;
}

public sealed class PositionExitPreparationRequiredException(Guid intentId) : InvalidOperationException(
    $"Position exit intent {intentId:N} has not completed protective-order cancellation and reconciliation.")
{
    public Guid IntentId { get; } = intentId;
}

/// <summary>
/// Coherent account and portfolio capacity captured by the ordered entry gates.
/// The repository rechecks this capacity against newer local reservations while
/// holding the same database transaction that consumes the candidate and writes
/// the broker intent.
/// </summary>
public sealed record PortfolioRiskReservationRequest(
    string AccountId,
    string Horizon,
    decimal AccountEquity,
    decimal AvailableBuyingPower,
    decimal BrokerGrossExposure,
    decimal BrokerNetExposure,
    IReadOnlySet<string> BrokerPositionSymbols,
    decimal ProposedNotional,
    decimal PlannedRisk,
    decimal MaxGrossExposure,
    decimal MaxPortfolioRisk,
    int MaxPositions,
    DateTimeOffset AccountSnapshotRequestedAtUtc,
    DateTimeOffset AccountSnapshotObservedAtUtc);

public sealed class PortfolioRiskReservationRejectedException(
    Guid intentId,
    string reasonCode,
    string message) : InvalidOperationException(message)
{
    public Guid IntentId { get; } = intentId;
    public string ReasonCode { get; } = reasonCode;
}
