using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroOrderResponse(
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("positionId")] string? PositionId,
    [property: JsonPropertyName("instrumentId")] long? InstrumentId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("units")] decimal? Units,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("openRate")] decimal? OpenRate,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt);

public sealed record EtoroOrderStatus(
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("positionId")] string? PositionId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("rejectReason")] string? RejectReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt);
