namespace TradingFlow.Domain.Signals;

public sealed record ExternalSignal(
    string SchemaVersion,
    string EventId,
    string Source,
    string StrategyId,
    string StrategyName,
    int StrategyVersion,
    string Ticker,
    string Action,
    string Side,
    string SignalTimeframe,
    string ExecutionTimeframe,
    DateTimeOffset BarTimeUtc,
    decimal Price,
    decimal? StopLoss,
    decimal? TakeProfit,
    IReadOnlyDictionary<string, decimal> SignalValues);
