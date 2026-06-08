using System;

namespace TradingFlow.Domain.Orders;

public record OrderUpdate(
    string OrderId,
    string ClientOrderId,
    string Ticker,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal FilledPrice,
    DateTimeOffset Timestamp
);
