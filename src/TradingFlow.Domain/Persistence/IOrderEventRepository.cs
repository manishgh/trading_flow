using TradingFlow.Domain.Orders;

namespace TradingFlow.Domain.Persistence;

public sealed record OrderStateSnapshot(
    Guid RunId,
    string ClientOrderId,
    string? BrokerOrderId,
    OrderState State,
    DateTimeOffset LocalTimestampUtc,
    DateTimeOffset? BrokerTimestampUtc,
    decimal? FilledQuantity,
    decimal? FillPrice,
    long EventId);

public sealed record OrderTransitionRequest(
    string ClientOrderId,
    OrderState ExpectedPreviousState,
    OrderState NewState,
    string Source,
    DateTimeOffset LocalTimestampUtc,
    DateTimeOffset? BrokerTimestampUtc = null,
    string? BrokerOrderId = null,
    decimal? FilledQuantity = null,
    decimal? FillPrice = null,
    string PayloadJson = "{}",
    OrderFillProjection? PositionFill = null);

/// <summary>
/// Position effect carried by an authoritative broker update. Stream updates
/// provide the resulting account position and execution identity; REST repairs
/// provide cumulative fill and let the repository calculate the missing delta
/// while holding the same write transaction as the order transition.
/// </summary>
public sealed record OrderFillProjection(
    string Symbol,
    string Side,
    decimal BrokerCumulativeFillQuantity,
    decimal BrokerCumulativeAverageFillPrice,
    string ExecutionId,
    decimal? AuthoritativePositionQuantity = null,
    decimal? AuthoritativeLastFillQuantity = null,
    decimal? AuthoritativeLastFillPrice = null);

public sealed record OrderTransitionResult(
    OrderStateSnapshot Snapshot,
    bool Applied);

public sealed record BrokerOrderReplacementTransition(
    string OwnerClientOrderId,
    string PredecessorBrokerOrderId,
    string SuccessorBrokerOrderId,
    DateTimeOffset BrokerTimestampUtc,
    DateTimeOffset LocalTimestampUtc,
    string PayloadJson);

public interface IOrderEventRepository
{
    Task<OrderStateSnapshot?> GetCurrentAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<OrderTransitionResult> TransitionAsync(
        OrderTransitionRequest request,
        CancellationToken cancellationToken = default);

    Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
        BrokerOrderReplacementTransition request,
        CancellationToken cancellationToken = default);
}
