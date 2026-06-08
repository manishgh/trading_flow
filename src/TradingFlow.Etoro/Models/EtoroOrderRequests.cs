using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroOpenOrderByUnitsRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("transaction")] string Transaction,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("instrumentId")] long InstrumentId,
    [property: JsonPropertyName("settlementType")] string SettlementType,
    [property: JsonPropertyName("orderType")] string OrderType,
    [property: JsonPropertyName("triggerRate")] decimal? TriggerRate,
    [property: JsonPropertyName("leverage")] decimal Leverage,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("orderCurrency")] string OrderCurrency,
    [property: JsonPropertyName("units")] decimal Units,
    [property: JsonPropertyName("contracts")] decimal? Contracts,
    [property: JsonPropertyName("stopLossRate")] decimal? StopLossRate,
    [property: JsonPropertyName("takeProfitRate")] decimal? TakeProfitRate,
    [property: JsonPropertyName("stopLossType")] string StopLossType,
    [property: JsonPropertyName("additionalMargin")] decimal? AdditionalMargin,
    [property: JsonPropertyName("positionIds")] IReadOnlyList<long>? PositionIds);

public sealed record EtoroOpenOrderByAmountRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("transaction")] string Transaction,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("instrumentId")] long InstrumentId,
    [property: JsonPropertyName("settlementType")] string SettlementType,
    [property: JsonPropertyName("orderType")] string OrderType,
    [property: JsonPropertyName("triggerRate")] decimal? TriggerRate,
    [property: JsonPropertyName("leverage")] decimal Leverage,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("orderCurrency")] string OrderCurrency,
    [property: JsonPropertyName("units")] decimal? Units,
    [property: JsonPropertyName("contracts")] decimal? Contracts,
    [property: JsonPropertyName("stopLossRate")] decimal? StopLossRate,
    [property: JsonPropertyName("takeProfitRate")] decimal? TakeProfitRate,
    [property: JsonPropertyName("stopLossType")] string StopLossType,
    [property: JsonPropertyName("additionalMargin")] decimal? AdditionalMargin,
    [property: JsonPropertyName("positionIds")] IReadOnlyList<long>? PositionIds);

public sealed record EtoroClosePositionRequest(
    [property: JsonPropertyName("InstrumentID")] long InstrumentId,
    [property: JsonPropertyName("UnitsToDeduct")] decimal? UnitsToDeduct);
