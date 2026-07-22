namespace TradingFlow.Domain.Orders;

/// <summary>
/// Canonical lifecycle for one broker order. Storage uses the exact uppercase
/// values from the binding production specification.
/// </summary>
public enum OrderState
{
    Intent,
    Submitted,
    Acked,
    PartiallyFilled,
    Filled,
    CancelPending,
    Canceled,
    Rejected,
    Expired
}

public static class OrderStateMachine
{
    private static readonly IReadOnlyDictionary<OrderState, IReadOnlySet<OrderState>> AllowedTransitions =
        new Dictionary<OrderState, IReadOnlySet<OrderState>>
        {
            [OrderState.Intent] = Set(OrderState.Submitted),
            [OrderState.Submitted] = Set(OrderState.Acked, OrderState.Rejected),
            [OrderState.Acked] = Set(
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.CancelPending,
                OrderState.Canceled,
                OrderState.Rejected,
                OrderState.Expired),
            [OrderState.PartiallyFilled] = Set(
                OrderState.Filled,
                OrderState.CancelPending,
                OrderState.Canceled,
                OrderState.Expired),
            [OrderState.CancelPending] = Set(
                OrderState.Canceled,
                OrderState.PartiallyFilled,
                OrderState.Filled,
                OrderState.Rejected,
                OrderState.Expired),
            [OrderState.Filled] = Set(),
            [OrderState.Canceled] = Set(),
            [OrderState.Rejected] = Set(),
            [OrderState.Expired] = Set()
        };

    public static bool CanTransition(OrderState previous, OrderState next) =>
        AllowedTransitions[previous].Contains(next);

    public static bool IsTerminal(OrderState state) =>
        state is OrderState.Filled or OrderState.Canceled or OrderState.Rejected or OrderState.Expired;

    public static string ToStorageValue(this OrderState state) => state switch
    {
        OrderState.Intent => "INTENT",
        OrderState.Submitted => "SUBMITTED",
        OrderState.Acked => "ACKED",
        OrderState.PartiallyFilled => "PARTIALLY_FILLED",
        OrderState.Filled => "FILLED",
        OrderState.CancelPending => "CANCEL_PENDING",
        OrderState.Canceled => "CANCELED",
        OrderState.Rejected => "REJECTED",
        OrderState.Expired => "EXPIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown order state.")
    };

    public static OrderState ParseStorageValue(string value) => value.Trim() switch
    {
        "INTENT" => OrderState.Intent,
        "SUBMITTED" => OrderState.Submitted,
        "ACKED" => OrderState.Acked,
        "PARTIALLY_FILLED" => OrderState.PartiallyFilled,
        "FILLED" => OrderState.Filled,
        "CANCEL_PENDING" => OrderState.CancelPending,
        "CANCELED" => OrderState.Canceled,
        "REJECTED" => OrderState.Rejected,
        "EXPIRED" => OrderState.Expired,
        _ => throw new InvalidOperationException($"Unknown persisted order state '{value}'.")
    };

    private static IReadOnlySet<OrderState> Set(params OrderState[] states) => states.ToHashSet();
}
