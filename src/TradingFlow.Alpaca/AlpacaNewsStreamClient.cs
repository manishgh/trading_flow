using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TradingFlow.Alpaca;

public sealed class AlpacaNewsStreamClient : IDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly AlpacaOptions _options;
    private readonly Uri _streamUrl;
    private readonly ILogger<AlpacaNewsStreamClient>? _logger;

    public AlpacaNewsStreamClient(AlpacaOptions options, ILogger<AlpacaNewsStreamClient>? logger = null)
    {
        _options = options;
        _streamUrl = options.ResolveNewsStreamUrl();
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Connecting to Alpaca news stream at {StreamUrl}.", _streamUrl);
        await _webSocket.ConnectAsync(_streamUrl, cancellationToken);

        // Wait for connection response
        var connectResponse = await ReceiveMessageAsync(cancellationToken);
        
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
        if (!authResponse.Contains("\"T\":\"success\"", StringComparison.OrdinalIgnoreCase) && 
            !authResponse.Contains("\"msg\":\"authenticated\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Alpaca news WebSocket authentication was rejected. Response: {authResponse}");
        }
    }

    public async Task SubscribeAllNewsAsync(CancellationToken cancellationToken)
    {
        var subPayload = new
        {
            action = "subscribe",
            news = new[] { "*" }
        };
        await SendMessageAsync(subPayload, cancellationToken);
        var subResponse = await ReceiveMessageAsync(cancellationToken);
        _logger?.LogInformation("Subscribed to Alpaca news stream. Response: {Response}", subResponse);
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
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
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

    public void Dispose()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).GetAwaiter().GetResult();
        }
        _webSocket.Dispose();
        _sendLock.Dispose();
    }
}
