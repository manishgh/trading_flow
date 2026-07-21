using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingFlow.Alpaca;

public sealed class AlpacaTradeStreamClient : IDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly AlpacaOptions _options;
    private readonly Uri _streamUrl;

    public AlpacaTradeStreamClient(AlpacaOptions options)
    {
        _options = options;
        
        _streamUrl = _options.ResolveTradingStreamUrl();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _webSocket.ConnectAsync(_streamUrl, cancellationToken);
        await SendMessageAsync(
            AlpacaTradeStreamProtocol.CreateAuthenticationMessage(
                _options.KeyId,
                _options.SecretKey),
            cancellationToken);
        var authResponse = await ReceiveMessageAsync(cancellationToken);
        AlpacaTradeStreamProtocol.RequireAuthorization(authResponse);
    }

    public async Task SubscribeTradeUpdatesAsync(CancellationToken cancellationToken)
    {
        await SendMessageAsync(
            AlpacaTradeStreamProtocol.CreateTradeUpdateSubscriptionMessage(),
            cancellationToken);
        var subscriptionResponse = await ReceiveMessageAsync(cancellationToken);
        AlpacaTradeStreamProtocol.RequireTradeUpdateSubscription(subscriptionResponse);
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                break;
            }

            if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            {
                throw new InvalidOperationException(
                    $"Unsupported Alpaca trade-stream message type {result.MessageType}.");
            }

            var messageJson = Encoding.UTF8.GetString(ms.ToArray());
            
            // Note: Trade Updates payload format differs from Market Data
            using var doc = JsonDocument.Parse(messageJson);
            yield return doc.RootElement.Clone();
        }
    }

    private async Task SendMessageAsync(string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException("Alpaca closed the account stream during protocol negotiation.");
        }

        if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
        {
            throw new InvalidOperationException(
                $"Unsupported Alpaca trade-stream message type {result.MessageType}.");
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        _webSocket.Abort();
        _webSocket.Dispose();
    }
}

/// <summary>
/// Validates Alpaca's documented account-stream acknowledgements before order processing is armed.
/// </summary>
public static class AlpacaTradeStreamProtocol
{
    public static string CreateAuthenticationMessage(string keyId, string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        return JsonSerializer.Serialize(new
        {
            action = "auth",
            key = keyId,
            secret = secretKey
        });
    }

    public static string CreateTradeUpdateSubscriptionMessage() =>
        JsonSerializer.Serialize(new
        {
            action = "listen",
            data = new
            {
                streams = new[] { "trade_updates" }
            }
        });

    public static void RequireAuthorization(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.TryGetProperty("stream", out var stream) &&
            String.Equals(stream.GetString(), "authorization", StringComparison.Ordinal) &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("status", out var status) &&
            String.Equals(status.GetString(), "authorized", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("Alpaca trade WebSocket authentication was not authorized.");
    }

    public static void RequireTradeUpdateSubscription(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.TryGetProperty("stream", out var stream) &&
            String.Equals(stream.GetString(), "listening", StringComparison.Ordinal) &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("streams", out var streams) &&
            streams.ValueKind == JsonValueKind.Array &&
            streams.EnumerateArray().Any(item =>
                String.Equals(item.GetString(), "trade_updates", StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException("Alpaca did not acknowledge the trade_updates subscription.");
    }
}
