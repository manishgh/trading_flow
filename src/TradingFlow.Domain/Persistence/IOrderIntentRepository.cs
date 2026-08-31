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
    Task<OrderIntentRecord?> GetByIntentIdAsync(
        Guid intentId,
        CancellationToken cancellationToken = default);

    Task<OrderIntentRecord?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
        string symbol,
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
    int? CandidateExpectedVersion = null,
    string? CandidateSemanticDecisionSha256 = null);
