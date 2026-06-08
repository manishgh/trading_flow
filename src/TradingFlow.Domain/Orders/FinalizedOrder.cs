namespace TradingFlow.Domain.Orders;

public sealed record FinalizedOrder(
    string Ticker,
    string StrategyName,
    int ShareQuantity,
    decimal LimitPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    DateTimeOffset ExecutionTimestamp);

