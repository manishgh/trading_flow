using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroPortfolio(
    [property: JsonPropertyName("clientPortfolio")] EtoroClientPortfolio? ClientPortfolio,
    [property: JsonPropertyName("positions")] IReadOnlyList<EtoroPosition>? Positions,
    [property: JsonPropertyName("orders")] IReadOnlyList<EtoroOrderStatus>? Orders);

public sealed record EtoroClientPortfolio(
    [property: JsonPropertyName("credit")] decimal? Credit,
    [property: JsonPropertyName("unrealizedPnL")] decimal? UnrealizedPnl,
    [property: JsonPropertyName("positions")] IReadOnlyList<EtoroPosition>? Positions,
    [property: JsonPropertyName("orders")] IReadOnlyList<EtoroOrderStatus>? Orders,
    [property: JsonPropertyName("ordersForOpen")] IReadOnlyList<EtoroOrderStatus>? OrdersForOpen,
    [property: JsonPropertyName("ordersForClose")] IReadOnlyList<EtoroOrderStatus>? OrdersForClose);

public sealed record EtoroPosition(
    [property: JsonPropertyName("positionID")] long? PositionId,
    [property: JsonPropertyName("instrumentID")] long? InstrumentId,
    [property: JsonPropertyName("isBuy")] bool? IsBuy,
    [property: JsonPropertyName("units")] decimal? Units,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("openRate")] decimal? OpenRate,
    [property: JsonPropertyName("currentRate")] decimal? CurrentRate,
    [property: JsonPropertyName("stopLossRate")] decimal? StopLossRate,
    [property: JsonPropertyName("takeProfitRate")] decimal? TakeProfitRate,
    [property: JsonPropertyName("profit")] decimal? Profit,
    [property: JsonPropertyName("openedAt")] DateTimeOffset? OpenedAt);

public sealed record EtoroPnl(
    [property: JsonPropertyName("clientPortfolio")] EtoroClientPortfolio? ClientPortfolio,
    [property: JsonPropertyName("realized")] decimal? Realized,
    [property: JsonPropertyName("unrealized")] decimal? Unrealized,
    [property: JsonPropertyName("total")] decimal? Total);
