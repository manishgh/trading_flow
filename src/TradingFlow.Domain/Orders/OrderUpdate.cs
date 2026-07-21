using System;

namespace TradingFlow.Domain.Orders;

public enum BrokerUpdateSource
{
    TradeStream,
    BrokerRest
}

public record OrderUpdate(
    string OrderId,
    string ClientOrderId,
    string Ticker,
    string Side,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal FilledPrice,
    decimal LastFillQuantity,
    decimal? PositionQuantity,
    string? ExecutionId,
    DateTimeOffset Timestamp,
    BrokerUpdateSource Source
);
