using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Models;

public sealed record EtoroNotificationsResponse(
    [property: JsonPropertyName("messages")] IReadOnlyList<EtoroNotificationMessage> Messages,
    [property: JsonPropertyName("meta")] EtoroNotificationMeta? Meta);

public sealed record EtoroNotificationMessage(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("actionLink")] string? ActionLink,
    [property: JsonPropertyName("imageTitle")] string? ImageTitle,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("notificationType")] string? NotificationType,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("aggregatable")] bool Aggregatable,
    [property: JsonPropertyName("grouped")] bool Grouped,
    [property: JsonPropertyName("aggregationId")] string? AggregationId,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("publishDate")] DateTimeOffset? PublishDate,
    [property: JsonPropertyName("subCategory")] string? SubCategory,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("rtlLanguage")] bool RtlLanguage,
    [property: JsonPropertyName("section")] string? Section);

public sealed record EtoroNotificationMeta(
    [property: JsonPropertyName("notSeen")] int NotSeen);
