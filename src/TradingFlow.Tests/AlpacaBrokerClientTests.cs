using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public class AlpacaBrokerClientTests : IDisposable
{
    private readonly string archiveRoot = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-alpaca-broker-archive-tests",
        Guid.NewGuid().ToString("N"));

    private IRawArchiveWriter CreateArchiveWriter() =>
        new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot));

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
            },
            CreateArchiveWriter());

        await client.SubmitExitOrdersAsync("OUST", 10, 40.12m, 45.67m, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.Equal("oco", root.GetProperty("order_class").GetString());
        Assert.False(root.TryGetProperty("limit_price", out _));
        Assert.Equal("45.67", root.GetProperty("take_profit").GetProperty("limit_price").GetString());
        Assert.Equal("40.12", root.GetProperty("stop_loss").GetProperty("stop_price").GetString());

        var payloadPath = Assert.Single(
            Directory.EnumerateFiles(archiveRoot, "*.json", SearchOption.AllDirectories),
            path => !path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(handler.ResponseBytes, await File.ReadAllBytesAsync(payloadPath));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(payloadPath + ".manifest.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal("broker-submit-exit-order", manifest.ArtifactType);
        Assert.Equal(200, manifest.Http!.StatusCode);
        var manifestText = await File.ReadAllTextAsync(payloadPath + ".manifest.json");
        Assert.DoesNotContain("test-key", manifestText, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", manifestText, StringComparison.Ordinal);
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
            },
            CreateArchiveWriter());

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
            },
            CreateArchiveWriter());

        var closed = await client.ClosePositionAsync("rgti", 25, CancellationToken.None);

        Assert.True(closed);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("/v2/positions/RGTI?qty=25", handler.Path);
    }

    [Fact]
    public async Task GetOpenOrdersAsync_ArchivesErrorBeforeThrowing()
    {
        using var handler = new CapturingHandler(
            HttpStatusCode.ServiceUnavailable,
            """{"message":"broker\nunavailable"}""");
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetOpenOrdersAsync(CancellationToken.None));

        Assert.Contains("broker unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RawArchiveId=", exception.Message, StringComparison.Ordinal);
        var payloadPath = Assert.Single(
            Directory.EnumerateFiles(archiveRoot, "*.json", SearchOption.AllDirectories),
            path => !path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(handler.ResponseBytes, await File.ReadAllBytesAsync(payloadPath));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(payloadPath + ".manifest.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(503, manifest!.Http!.StatusCode);
        Assert.Equal("broker-open-orders", manifest.ArtifactType);
    }

    [Fact]
    public async Task GetOpenOrdersAsync_DoesNotInterpretWhenArchiveCommitFails()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK, "<invalid-json>");
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            new FailingRawArchiveWriter());

        var exception = await Assert.ThrowsAsync<IOException>(
            () => client.GetOpenOrdersAsync(CancellationToken.None));

        Assert.Equal("simulated archive failure", exception.Message);
    }

    [Fact]
    public async Task SubmitExitOrdersAsync_DoesNotInventOrderIdWhenProviderOmitsIt()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SubmitExitOrdersAsync("OUST", 10, 40.12m, 45.67m, CancellationToken.None));

        Assert.Contains("returned no order id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RawArchiveId=", exception.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """{"id":"oco-order-id"}""") : HttpMessageHandler, IDisposable
    {
        public byte[] ResponseBytes { get; } = Encoding.UTF8.GetBytes(responseBody);

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

            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(ResponseBytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                }
            };
        }
    }

    private sealed class FailingRawArchiveWriter : IRawArchiveWriter
    {
        public Task<RawArchiveReceipt> ArchiveAsync(
            RawArchiveRequest request,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            Task.FromException<RawArchiveReceipt>(new IOException("simulated archive failure"));

        public Task<RawArchiveRetentionResult> EnforceRetentionAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(archiveRoot))
        {
            Directory.Delete(archiveRoot, recursive: true);
        }
    }
}
