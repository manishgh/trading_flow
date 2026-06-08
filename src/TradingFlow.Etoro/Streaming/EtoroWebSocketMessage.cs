using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingFlow.Etoro.Streaming;

public sealed record EtoroWebSocketMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("payload")] JsonElement Payload);

public sealed record EtoroWebSocketSubscription(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("data")] EtoroWebSocketSubscriptionData Data);

public sealed record EtoroWebSocketSubscriptionData(
    [property: JsonPropertyName("topics")] IReadOnlyList<string> Topics,
    [property: JsonPropertyName("snapshot")] bool Snapshot);

public sealed record EtoroWebSocketAuthentication(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("data")] EtoroWebSocketAuthenticationData Data);

public sealed record EtoroWebSocketAuthenticationData(
    [property: JsonPropertyName("userKey")] string UserKey,
    [property: JsonPropertyName("apiKey")] string ApiKey);

public sealed record EtoroWebSocketEnvelope(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("success")] bool? Success,
    [property: JsonPropertyName("operation")] string? Operation,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("messages")] IReadOnlyList<EtoroWebSocketMessage>? Messages);
