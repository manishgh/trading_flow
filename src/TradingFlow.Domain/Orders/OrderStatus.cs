namespace TradingFlow.Domain.Orders;

public enum OrderStatus
{
    New,
    PartiallyFilled,
    Filled,
    DoneForDay,
    Canceled,
    Expired,
    Replaced,
    PendingCancel,
    PendingReplace,
    Accepted,
    PendingNew,
    AcceptedForBidding,
    Stopped,
    Rejected,
    Suspended,
    Calculated
}

public static class OrderStatusCodec
{
    public static OrderStatus ParseBrokerValue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "new" => OrderStatus.New,
        "partial_fill" or "partially_filled" => OrderStatus.PartiallyFilled,
        "fill" or "filled" => OrderStatus.Filled,
        "done_for_day" => OrderStatus.DoneForDay,
        "canceled" or "cancelled" => OrderStatus.Canceled,
        "expired" => OrderStatus.Expired,
        "replaced" => OrderStatus.Replaced,
        "pending_cancel" => OrderStatus.PendingCancel,
        "pending_replace" => OrderStatus.PendingReplace,
        "accepted" => OrderStatus.Accepted,
        "pending_new" => OrderStatus.PendingNew,
        "accepted_for_bidding" => OrderStatus.AcceptedForBidding,
        "stopped" => OrderStatus.Stopped,
        "rejected" => OrderStatus.Rejected,
        "suspended" => OrderStatus.Suspended,
        "calculated" => OrderStatus.Calculated,
        _ => throw new InvalidOperationException($"Unknown broker order status '{value}'.")
    };
}
