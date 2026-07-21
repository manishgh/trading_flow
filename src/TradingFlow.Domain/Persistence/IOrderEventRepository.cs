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
    string PayloadJson = "{}");

public sealed record OrderTransitionResult(
    OrderStateSnapshot Snapshot,
    bool Applied);

public interface IOrderEventRepository
{
    Task<OrderStateSnapshot?> GetCurrentAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<OrderTransitionResult> TransitionAsync(
        OrderTransitionRequest request,
        CancellationToken cancellationToken = default);
}
