using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TradingFlow.Alpaca;

public sealed class AlpacaStreamClient : IDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly AlpacaOptions _options;
    private readonly Uri _streamUrl;
    private readonly ILogger<AlpacaStreamClient>? _logger;

    public AlpacaStreamClient(AlpacaOptions options, ILogger<AlpacaStreamClient>? logger = null)
    {
        _options = options;
        _streamUrl = options.ResolveMarketDataStreamUrl();
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Connecting to Alpaca {MarketDataFeed} market-data stream at {StreamUrl}.",
            _options.ResolveMarketDataFeed(),
            _streamUrl);
        await _webSocket.ConnectAsync(_streamUrl, cancellationToken);

        // Alpaca sends a connection acknowledgement before accepting authentication.
        _ = await ReceiveMessageAsync(cancellationToken);
        
        // Send Auth
        var authPayload = new
        {
            action = "auth",
            key = _options.KeyId,
            secret = _options.SecretKey
        };
        await SendMessageAsync(authPayload, cancellationToken);

        // Wait for auth response
        var authResponse = await ReceiveMessageAsync(cancellationToken);
        if (!IsAuthorizedResponse(authResponse))
        {
            throw new InvalidOperationException("Alpaca market-data WebSocket authentication was rejected.");
        }
    }

    public async Task SubscribeBarsAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken)
    {
        var subPayload = new
        {
            action = "subscribe",
            bars = tickers
        };
        await SendMessageAsync(subPayload, cancellationToken);
        var subResponse = await ReceiveMessageAsync(cancellationToken);
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

            var messageJson = Encoding.UTF8.GetString(ms.ToArray());
            
            using var doc = JsonDocument.Parse(messageJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    yield return element.Clone();
                }
            }
        }
    }

    private async Task SendMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
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

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static bool IsAuthorizedResponse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement
                    .EnumerateArray()
                    .Any(IsAuthorizedElement),
                JsonValueKind.Object => IsAuthorizedElement(document.RootElement),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAuthorizedElement(JsonElement element)
    {
        var status = element.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (status is "authorized" or "authenticated")
        {
            return true;
        }

        var type = element.TryGetProperty("T", out var typeElement)
            ? typeElement.GetString()
            : null;
        var message = element.TryGetProperty("msg", out var messageElement)
            ? messageElement.GetString()
            : null;

        return String.Equals(type, "success", StringComparison.OrdinalIgnoreCase) &&
               String.Equals(message, "authenticated", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _webSocket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disposed", timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            _webSocket.Abort();
        }
        finally
        {
            _webSocket.Dispose();
        }
    }
}
