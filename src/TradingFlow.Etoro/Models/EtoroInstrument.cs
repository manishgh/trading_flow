using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroInstrument(
    [property: JsonPropertyName("instrumentId")] long? InstrumentId,
    [property: JsonPropertyName("internalInstrumentId")] long? InternalInstrumentId,
    [property: JsonPropertyName("internalSymbolFull")] string? InternalSymbolFull,
    [property: JsonPropertyName("symbolFull")] string? SymbolFull,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("instrumentTypeID")] int? InstrumentTypeId,
    [property: JsonPropertyName("exchangeID")] int? ExchangeId,
    [property: JsonPropertyName("isInternalInstrument")] bool? IsInternalInstrument);
