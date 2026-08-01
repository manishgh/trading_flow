using TradingFlow.Domain.Orders;

namespace TradingFlow.Domain.Persistence;

/// <summary>
/// Read-only projection of the latest lifecycle state for one durable order intent.
/// </summary>
public sealed record OrderActivitySnapshot(
    Guid RunId,
    string ClientOrderId,
    string? BrokerOrderId,
    string StrategyId,
    string Symbol,
    string Side,
    string OrderType,
    string TimeInForce,
    decimal RequestedQuantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    OrderState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    decimal? FilledQuantity,
    decimal? FillPrice,
    string EventSource);

/// <summary>
/// Queries the immutable order journal without changing broker or engine state.
/// </summary>
public interface IOrderActivityQuery
{
    Task<IReadOnlyList<OrderActivitySnapshot>> ListRecentAsync(
        OrderState? state = null,
        int limit = 200,
        CancellationToken cancellationToken = default);
}
