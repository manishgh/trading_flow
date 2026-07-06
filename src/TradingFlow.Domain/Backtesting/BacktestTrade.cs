namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestCandidateTrade(
    string Ticker,
    string StrategyName,
    string Direction,
    DateTimeOffset EntryTimestamp,
    decimal EntryPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    DateTimeOffset ExitTimestamp,
    decimal ExitPrice,
    string ExitReason,
    decimal StopDistance,
    decimal EntryBarVolume = 0m);

public sealed record BacktestTrade(
    string Ticker,
    string StrategyName,
    string Direction,
    DateTimeOffset EntryTimestamp,
    DateTimeOffset ExitTimestamp,
    int ShareQuantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    string ExitReason,
    decimal GrossProfit,
    decimal Fees,
    decimal NetProfit);
