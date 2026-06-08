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

public sealed class AlpacaStreamClient : IDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly AlpacaOptions _options;
    private readonly Uri _streamUrl;

    public AlpacaStreamClient(AlpacaOptions options)
    {
        _options = options;
        _streamUrl = new Uri("wss://stream.data.alpaca.markets/v2/iex");
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _webSocket.ConnectAsync(_streamUrl, cancellationToken);

        // Wait for welcome message
        var welcomeMsg = await ReceiveMessageAsync(cancellationToken);
        
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
        if (!authResponse.Contains("\"status\":\"authorized\"") && !authResponse.Contains("\"status\":\"authenticated\""))
        {
            if (authResponse.Contains("auth_failed") || authResponse.Contains("not authorized"))
            {
                throw new Exception($"Alpaca WebSocket Authentication Failed: {authResponse}");
            }
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

    public void Dispose()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None).GetAwaiter().GetResult();
        }
        _webSocket.Dispose();
    }
}
