using System;

namespace TradingFlow.Domain.Orders;

public sealed record ActiveBrokerOrder(
    string OrderId,
    string Ticker,
    string Side,
    string Status,
    string OrderType,
    decimal? LimitPrice,
    decimal? StopPrice,
    decimal? Qty,
    DateTimeOffset CreatedAt,
    string ClientOrderId = "");
