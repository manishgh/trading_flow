using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroCandle(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("fromDate")] DateTimeOffset? FromDate,
    [property: JsonPropertyName("open")] decimal Open,
    [property: JsonPropertyName("high")] decimal High,
    [property: JsonPropertyName("low")] decimal Low,
    [property: JsonPropertyName("close")] decimal Close,
    [property: JsonPropertyName("volume")] decimal Volume);
