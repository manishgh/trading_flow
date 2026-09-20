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
    string ClientOrderId,
    decimal FilledQuantity,
    decimal? FilledAveragePrice,
    DateTimeOffset UpdatedAt,
    string? ParentClientOrderId = null,
    string? TimeInForce = null,
    string? OrderClass = null,
    bool? ExtendedHours = null,
    decimal? BracketTakeProfitPrice = null,
    decimal? BracketStopPrice = null,
    string? ReplacesOrderId = null,
    string? ReplacedByOrderId = null);
