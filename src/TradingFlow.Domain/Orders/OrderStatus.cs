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
