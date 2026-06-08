using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Signals;

public sealed record TradingViewWebhookPayload(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("strategy_id")] string StrategyId,
    [property: JsonPropertyName("strategy_name")] string StrategyName,
    [property: JsonPropertyName("strategy_version")] int StrategyVersion,
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("signal_timeframe")] string SignalTimeframe,
    [property: JsonPropertyName("execution_timeframe")] string ExecutionTimeframe,
    [property: JsonPropertyName("bar_time_epoch_ms")] long BarTimeEpochMs,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("stop_loss")] decimal? StopLoss,
    [property: JsonPropertyName("take_profit")] decimal? TakeProfit,
    [property: JsonPropertyName("exit_reason")] string? ExitReason,
    [property: JsonPropertyName("signal_values")] IReadOnlyDictionary<string, decimal> SignalValues)
{
    public ExternalSignal ToExternalSignal()
    {
        return new ExternalSignal(
            SchemaVersion,
            EventId,
            Source,
            StrategyId,
            StrategyName,
            StrategyVersion,
            Ticker,
            Action,
            Side,
            SignalTimeframe,
            ExecutionTimeframe,
            DateTimeOffset.FromUnixTimeMilliseconds(BarTimeEpochMs),
            Price,
            StopLoss,
            TakeProfit,
            SignalValues);
    }

    public static TradingViewWebhookPayload FromExternalSignal(ExternalSignal signal, string? exitReason)
    {
        return new TradingViewWebhookPayload(
            signal.SchemaVersion,
            signal.Source,
            signal.EventId,
            signal.StrategyId,
            signal.StrategyName,
            signal.StrategyVersion,
            signal.Ticker,
            signal.Action,
            signal.Side,
            signal.SignalTimeframe,
            signal.ExecutionTimeframe,
            signal.BarTimeUtc.ToUnixTimeMilliseconds(),
            signal.Price,
            signal.StopLoss,
            signal.TakeProfit,
            exitReason,
            signal.SignalValues);
    }
}
