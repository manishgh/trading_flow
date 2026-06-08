namespace TradingFlow.Domain.Orders;

public sealed record BrokerPosition(
    string Ticker,
    string Side,
    decimal Qty,
    decimal EntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPl);
