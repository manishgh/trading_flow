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
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
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

    [Fact]
    public async Task ModifyOrderAsync_PatchesStopLeg_WithNewStopPrice()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            });

        var modified = await client.ModifyOrderAsync("stop-leg-1", 4.25m, 0m, CancellationToken.None);

        Assert.True(modified);
        Assert.Equal(HttpMethod.Patch, handler.Method);
        Assert.Equal("/v2/orders/stop-leg-1", handler.Path);
        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.Equal("4.25", root.GetProperty("stop_price").GetString());
        Assert.False(root.TryGetProperty("limit_price", out _));
    }

    [Fact]
    public async Task ClosePositionAsync_WithQuantity_UsesScopedPositionClose()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            });

        var closed = await client.ClosePositionAsync("rgti", 25, CancellationToken.None);

        Assert.True(closed);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("/v2/positions/RGTI?qty=25", handler.Path);
    }

    private sealed class CapturingHandler : HttpMessageHandler, IDisposable
    {
        public string RequestJson { get; private set; } = String.Empty;
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.PathAndQuery;
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
