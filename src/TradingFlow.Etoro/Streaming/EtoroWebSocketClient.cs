using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TradingFlow.Etoro.Authentication;
using TradingFlow.Etoro.Configuration;

namespace TradingFlow.Etoro.Streaming;

public sealed class EtoroWebSocketClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EtoroOptions options;
    private readonly EtoroCredentialsProvider credentialsProvider;

    public EtoroWebSocketClient(EtoroOptions options, EtoroCredentialsProvider credentialsProvider)
    {
        this.options = options;
        this.credentialsProvider = credentialsProvider;
    }

    public async IAsyncEnumerable<EtoroWebSocketMessage> ConnectAsync(
        IReadOnlyCollection<long> instrumentIds,
        bool subscribePrivate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(options.WebSocketUrl, cancellationToken);
        await AuthenticateAsync(socket, cancellationToken);

        foreach (var instrumentId in instrumentIds.Distinct())
        {
            await SubscribeAsync(socket, [$"instrument:{instrumentId}"], snapshot: true, cancellationToken);
        }

        if (subscribePrivate)
        {
            await SubscribeAsync(socket, ["private"], snapshot: true, cancellationToken);
        }

        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextMessageAsync(socket, buffer, cancellationToken);
            if (String.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var envelope = JsonSerializer.Deserialize<EtoroWebSocketEnvelope>(message, JsonOptions);
            if (envelope?.Messages is { Count: > 0 })
            {
                foreach (var parsed in envelope.Messages)
                {
                    yield return parsed;
                }
            }
        }
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var credentials = credentialsProvider.GetCredentials();
        await SendJsonAsync(
            socket,
            new EtoroWebSocketAuthentication(
                Guid.NewGuid().ToString("D"),
                "Authenticate",
                new EtoroWebSocketAuthenticationData(credentials.UserKey, credentials.ApiKey)),
            cancellationToken);
    }

    private static Task SubscribeAsync(
        ClientWebSocket socket,
        IReadOnlyList<string> topics,
        bool snapshot,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync(
            socket,
            new EtoroWebSocketSubscription(
                Guid.NewGuid().ToString("D"),
                "Subscribe",
                new EtoroWebSocketSubscriptionData(topics, snapshot)),
            cancellationToken);
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveTextMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                return String.Empty;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray()).Trim('\0', '\uFEFF');
            }
        }
    }
}
