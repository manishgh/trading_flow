namespace TradingFlow.Domain.Persistence;

/// <summary>
/// Reserves immutable order intents and their initial lifecycle event before broker submission.
/// </summary>
public interface IOrderIntentRepository
{
    Task<OrderIntentRecord?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates the owning run and reserves one immutable intent. Repeating the
    /// same intent ID returns the original row and therefore the original client order ID.
    /// </summary>
    Task<OrderIntentRecord> ReserveAsync(
        ProductionRun run,
        OrderIntentReservation reservation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Broker-neutral data required to reserve an order intent before any network submission.
/// The repository allocates the per-session sequence and client order ID transactionally.
/// </summary>
public sealed record OrderIntentReservation(
    Guid IntentId,
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
    string RequestJson);
