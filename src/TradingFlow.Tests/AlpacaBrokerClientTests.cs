using System.Net;
using System.Text.Json;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

public class AlpacaBrokerClientTests
{
    [Fact]
    public async Task SubmitExitOrdersAsync_SendsTakeProfitLimitPrice_ForOcoOrder()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.CreateDefault() with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            });

        await client.SubmitExitOrdersAsync("OUST", 10, 40.12m, 45.67m, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.Equal("oco", root.GetProperty("order_class").GetString());
        Assert.False(root.TryGetProperty("limit_price", out _));
        Assert.Equal("45.67", root.GetProperty("take_profit").GetProperty("limit_price").GetString());
        Assert.Equal("40.12", root.GetProperty("stop_loss").GetProperty("stop_price").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler, IDisposable
    {
        public string RequestJson { get; private set; } = String.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestJson = request.Content is null
                ? String.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"oco-order-id"}""")
            };
        }
    }
}
