using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroRate(
    [property: JsonPropertyName("instrumentID")] long InstrumentId,
    [property: JsonPropertyName("bid")] decimal? Bid,
    [property: JsonPropertyName("ask")] decimal? Ask,
    [property: JsonPropertyName("last")] decimal? Last,
    [property: JsonPropertyName("lastExecution")] decimal? LastExecution,
    [property: JsonPropertyName("open")] decimal? Open,
    [property: JsonPropertyName("high")] decimal? High,
    [property: JsonPropertyName("low")] decimal? Low,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("date")] DateTimeOffset? Date);
