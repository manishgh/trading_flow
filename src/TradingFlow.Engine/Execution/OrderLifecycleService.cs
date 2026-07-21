using System.Text.Json;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public interface IOrderLifecycleService
{
    Task<OrderStateSnapshot> ApplyBrokerUpdateAsync(
        OrderUpdate update,
        CancellationToken cancellationToken = default);

    Task<OrderStateSnapshot> RequestCancelAsync(
        string clientOrderId,
        string brokerOrderId,
        CancellationToken cancellationToken = default);
}

public static class BrokerOrderUpdateFactory
{
    public static OrderUpdate Create(ActiveBrokerOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderUpdate(
            order.OrderId,
            order.ClientOrderId,
            order.Ticker,
            order.Side,
            OrderStatusCodec.ParseBrokerValue(order.Status),
            order.FilledQuantity,
            order.FilledAveragePrice ?? 0m,
            0m,
            null,
            null,
            order.UpdatedAt,
            BrokerUpdateSource.BrokerRest);
    }
}

/// <summary>
/// Projects authoritative broker updates into the append-only EXE-02 lifecycle.
/// </summary>
public sealed class OrderLifecycleService(IOrderEventRepository events) : IOrderLifecycleService
{
    public async Task<OrderStateSnapshot> RequestCancelAsync(
        string clientOrderId,
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        var current = await events.GetCurrentAsync(clientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot cancel unknown client order ID '{clientOrderId}'.");
        EnsureBrokerIdentity(current, brokerOrderId);
        if (current.State == OrderState.CancelPending)
        {
            return current;
        }

        if (OrderStateMachine.IsTerminal(current.State))
        {
            throw new InvalidOperationException(
                $"Cannot cancel terminal order '{clientOrderId}' in state {current.State.ToStorageValue()}.");
        }

        var transition = await events.TransitionAsync(
            new OrderTransitionRequest(
                clientOrderId,
                current.State,
                OrderState.CancelPending,
                Source: "engine",
                LocalTimestampUtc: DateTimeOffset.UtcNow,
                BrokerOrderId: brokerOrderId,
                PayloadJson: JsonSerializer.Serialize(new
                {
                    clientOrderId,
                    brokerOrderId,
                    action = "cancel_requested"
                })),
            cancellationToken);
        return transition.Snapshot;
    }

    public async Task<OrderStateSnapshot> ApplyBrokerUpdateAsync(
        OrderUpdate update,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        var current = await events.GetCurrentAsync(update.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Broker update references unknown client order ID '{update.ClientOrderId}'.");
        EnsureBrokerIdentity(current, update.OrderId);

        var target = ResolveTargetState(update.Status);
        if (current.State == OrderState.Submitted && target == OrderState.Rejected)
        {
            var rejected = await events.TransitionAsync(
                CreateRequest(current, OrderState.Rejected, update),
                cancellationToken);
            return rejected.Snapshot;
        }

        current = await EnsureAcknowledgedAsync(current, update, cancellationToken);
        if (target is null ||
            (current.State == target && target != OrderState.PartiallyFilled))
        {
            return current;
        }

        var transition = await events.TransitionAsync(
            CreateRequest(current, target.Value, update),
            cancellationToken);
        return transition.Snapshot;
    }

    private async Task<OrderStateSnapshot> EnsureAcknowledgedAsync(
        OrderStateSnapshot current,
        OrderUpdate update,
        CancellationToken cancellationToken)
    {
        if (current.State == OrderState.Submitted)
        {
            var acknowledged = await events.TransitionAsync(
                CreateRequest(current, OrderState.Acked, update),
                cancellationToken);
            return acknowledged.Snapshot;
        }

        if (current.State == OrderState.Intent)
        {
            throw new InvalidOperationException(
                $"Broker update arrived before order '{update.ClientOrderId}' was marked SUBMITTED.");
        }

        return current;
    }

    private static OrderTransitionRequest CreateRequest(
        OrderStateSnapshot current,
        OrderState target,
        OrderUpdate update) => new(
            update.ClientOrderId,
            current.State,
            target,
            Source: update.Source switch
            {
                BrokerUpdateSource.TradeStream => "broker_stream",
                BrokerUpdateSource.BrokerRest => "broker_rest",
                _ => throw new ArgumentOutOfRangeException(nameof(update.Source), update.Source, "Unknown broker update source.")
            },
            LocalTimestampUtc: DateTimeOffset.UtcNow,
            BrokerTimestampUtc: update.Timestamp.ToUniversalTime(),
            BrokerOrderId: update.OrderId,
            FilledQuantity: target is OrderState.PartiallyFilled or OrderState.Filled
                ? update.FilledQuantity
                : null,
            FillPrice: target is OrderState.PartiallyFilled or OrderState.Filled
                ? update.FilledPrice
                : null,
            PayloadJson: JsonSerializer.Serialize(new
            {
                update.OrderId,
                update.ClientOrderId,
                update.Ticker,
                update.Side,
                status = update.Status.ToString(),
                update.FilledQuantity,
                update.FilledPrice,
                update.Timestamp
            }));

    private static OrderState? ResolveTargetState(OrderStatus status) => status switch
    {
        OrderStatus.New or
        OrderStatus.Accepted or
        OrderStatus.PendingNew or
        OrderStatus.AcceptedForBidding => null,
        OrderStatus.PartiallyFilled => OrderState.PartiallyFilled,
        OrderStatus.Filled => OrderState.Filled,
        OrderStatus.PendingCancel => OrderState.CancelPending,
        OrderStatus.Canceled => OrderState.Canceled,
        OrderStatus.Expired or OrderStatus.DoneForDay => OrderState.Expired,
        OrderStatus.Rejected => OrderState.Rejected,
        OrderStatus.Replaced or
        OrderStatus.PendingReplace or
        OrderStatus.Stopped or
        OrderStatus.Suspended or
        OrderStatus.Calculated => throw new InvalidOperationException(
            $"Broker order status {status} requires explicit cancel/replace reconciliation."),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown order status.")
    };

    private static void EnsureBrokerIdentity(OrderStateSnapshot current, string brokerOrderId)
    {
        if (!String.IsNullOrWhiteSpace(current.BrokerOrderId) &&
            !current.BrokerOrderId.Equals(brokerOrderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Broker order ID changed for '{current.ClientOrderId}'.");
        }
    }

    private static void Validate(OrderUpdate update)
    {
        if (String.IsNullOrWhiteSpace(update.OrderId) ||
            String.IsNullOrWhiteSpace(update.ClientOrderId) ||
            String.IsNullOrWhiteSpace(update.Ticker) ||
            String.IsNullOrWhiteSpace(update.Side) ||
            update.Timestamp == default ||
            update.FilledQuantity < 0m ||
            update.FilledPrice < 0m)
        {
            throw new InvalidOperationException("Broker order update is incomplete or invalid.");
        }

        if (update.Status is (OrderStatus.PartiallyFilled or OrderStatus.Filled) &&
            (update.FilledQuantity <= 0m || update.FilledPrice <= 0m ||
             (update.Source == BrokerUpdateSource.TradeStream &&
              (update.LastFillQuantity <= 0m || update.PositionQuantity is null ||
               String.IsNullOrWhiteSpace(update.ExecutionId)))))
        {
            throw new InvalidOperationException("Fill updates require positive cumulative quantity and price.");
        }
    }
}
