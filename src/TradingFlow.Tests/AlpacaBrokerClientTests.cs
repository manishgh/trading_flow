using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Execution;
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
    public async Task SubmitProtectiveStopAsync_SendsStandaloneGtcStopWithBindingClientId()
    {
        using var handler = new CapturingHandler(
            responseBody: """{"id":"stop-order-id","created_at":"2026-07-21T15:00:00Z"}""");
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var receipt = await client.SubmitProtectiveStopAsync(
            new ProtectiveStopOrder(
                "MSFT", "sell", 10m, 98.123m, "gtc",
                "BACKSTOP-S-MSFT-20260721-001-12345678"),
            CancellationToken.None);

        Assert.Equal("stop-order-id", receipt.BrokerOrderId);
        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.Equal("stop", root.GetProperty("type").GetString());
        Assert.Equal("gtc", root.GetProperty("time_in_force").GetString());
        Assert.Equal("sell", root.GetProperty("side").GetString());
        Assert.Equal("10", root.GetProperty("qty").GetString());
        Assert.Equal("98.12", root.GetProperty("stop_price").GetString());
        Assert.Equal("BACKSTOP-S-MSFT-20260721-001-12345678", root.GetProperty("client_order_id").GetString());
    }

    [Fact]
    public async Task GetOpenOrdersAsync_FlattensOpenBracketLegs_WithParentOwnership()
    {
        const string parentClientId = "SWGA-B-MSFT-20260721-001-12345678";
        var response = $$"""
        [{
          "id":"parent-1","symbol":"MSFT","side":"buy","status":"filled","type":"limit",
          "client_order_id":"{{parentClientId}}","limit_price":"100","stop_price":null,"qty":"10",
          "filled_qty":"10","filled_avg_price":"100","created_at":"2026-07-21T14:30:00Z","updated_at":"2026-07-21T14:31:00Z",
          "legs":[{
            "id":"stop-leg-1","symbol":"MSFT","side":"sell","status":"new","type":"stop",
            "client_order_id":"broker-generated-leg-id","limit_price":null,"stop_price":"98","qty":"10",
            "filled_qty":"0","filled_avg_price":null,"created_at":"2026-07-21T14:31:00Z","updated_at":"2026-07-21T14:31:00Z","legs":null
          }]
        }]
        """;
        using var handler = new CapturingHandler(responseBody: response);
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var order = Assert.Single(await client.GetOpenOrdersAsync(CancellationToken.None));

        Assert.Equal("/v2/orders?status=open&nested=true&limit=500", handler.Path);
        Assert.Equal("stop-leg-1", order.OrderId);
        Assert.Equal(parentClientId, order.ParentClientOrderId);
        Assert.Equal(98m, order.StopPrice);
    }


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
    public async Task GetOrderByClientOrderIdAsync_ParsesTerminalOrderAndArchivesResponse()
    {
        const string clientOrderId = "SWGA-B-MSFT-20260721-001-12345678";
        using var handler = new CapturingHandler(
            HttpStatusCode.OK,
            $$"""
            {
              "id":"broker-1","client_order_id":"{{clientOrderId}}","symbol":"MSFT",
              "side":"buy","status":"filled","type":"limit","limit_price":"100.00",
              "stop_price":null,"qty":"10","filled_qty":"10","filled_avg_price":"100.50",
              "created_at":"2026-07-21T14:59:00Z","updated_at":"2026-07-21T15:00:00Z"
            }
            """);
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var order = await client.GetOrderByClientOrderIdAsync(clientOrderId, CancellationToken.None);

        Assert.NotNull(order);
        Assert.Equal("filled", order.Status);
        Assert.Equal(10m, order.FilledQuantity);
        Assert.Equal(100.50m, order.FilledAveragePrice);
        Assert.Equal(
            $"/v2/orders:by_client_order_id?client_order_id={clientOrderId}",
            handler.Path);
        var manifestPath = Assert.Single(
            Directory.EnumerateFiles(archiveRoot, "*.manifest.json", SearchOption.AllDirectories));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("broker-order-by-client-id", manifest!.ArtifactType);
    }

    [Fact]
    public async Task GetOrderByClientOrderIdAsync_NotFound_ReturnsNullAfterArchiving()
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"message":"order not found"}""");
        using var httpClient = new HttpClient(handler);
        using var client = new AlpacaBrokerClient(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

        var order = await client.GetOrderByClientOrderIdAsync(
            "SWGA-B-MSFT-20260721-001-12345678",
            CancellationToken.None);

        Assert.Null(order);
        var manifestPath = Assert.Single(
            Directory.EnumerateFiles(archiveRoot, "*.manifest.json", SearchOption.AllDirectories));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(404, manifest!.Http!.StatusCode);
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

    [Fact]
    public async Task SubmitOrderAsync_RegularLimitOrder_PreservesBracketAndOmitsExtendedFlag()
    {
        using var handler = new CapturingHandler(
            responseBody: """{"id":"regular-order","created_at":"2026-07-21T15:00:00Z"}""");
        using var client = CreateClient(handler);

        await client.SubmitOrderAsync(
            CreateEntryOrder(submitOutsideRegularHours: false),
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.Equal("bracket", root.GetProperty("order_class").GetString());
        Assert.False(root.TryGetProperty("extended_hours", out _));
        Assert.Equal("limit", root.GetProperty("type").GetString());
        Assert.Equal("day", root.GetProperty("time_in_force").GetString());
    }

    [Fact]
    public async Task SubmitOrderAsync_ExtendedLimitOrder_SendsExactStandaloneContract()
    {
        using var handler = new CapturingHandler(
            responseBody: """{"id":"extended-order","created_at":"2026-07-21T12:00:00Z"}""");
        using var client = CreateClient(handler);

        await client.SubmitOrderAsync(
            CreateEntryOrder(submitOutsideRegularHours: true),
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.RequestJson);
        var root = document.RootElement;
        Assert.True(root.GetProperty("extended_hours").GetBoolean());
        Assert.False(root.TryGetProperty("order_class", out _));
        Assert.Equal("limit", root.GetProperty("type").GetString());
        Assert.Equal("day", root.GetProperty("time_in_force").GetString());
        Assert.Equal("100.00", root.GetProperty("limit_price").GetString());
    }

    [Fact]
    public async Task GetSessionAsync_UsesProviderCalendarAndCachesTheTradeDate()
    {
        using var handler = new CapturingHandler(
            responseBody: """[{"date":"2026-07-21","open":"09:30","close":"16:00"}]""");
        using var client = CreateClient(handler);

        var premarket = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var regular = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var afterHours = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 21, 21, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(EquityTradingSession.Premarket, premarket.Session);
        Assert.Equal(EquityTradingSession.Regular, regular.Session);
        Assert.Equal(EquityTradingSession.AfterHours, afterHours.Session);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("/v2/calendar?start=2026-07-21&end=2026-07-21", handler.Path);
    }

    [Fact]
    public async Task GetSessionAsync_AfterEightPm_UsesNextTradeDateForOvernight()
    {
        using var handler = new CapturingHandler(
            responseBody: """[{"date":"2026-07-21","open":"09:30","close":"16:00"}]""");
        using var client = CreateClient(handler);

        var session = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 7, 21), session.TradeDate);
        Assert.Equal(EquityTradingSession.Overnight, session.Session);
        Assert.Equal("/v2/calendar?start=2026-07-21&end=2026-07-21", handler.Path);
    }

    [Fact]
    public async Task GetSessionAsync_UsesProviderEarlyCloseInsteadOfHardcodedClose()
    {
        using var handler = new CapturingHandler(
            responseBody: """[{"date":"2026-07-02","open":"09:30","close":"13:00"}]""");
        using var client = CreateClient(handler);

        var session = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 2, 17, 1, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(EquityTradingSession.AfterHours, session.Session);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 17, 0, 0, TimeSpan.Zero), session.RegularCloseUtc);
    }

    [Fact]
    public async Task GetSessionAsync_EmptyProviderCalendar_FailsClosed()
    {
        using var handler = new CapturingHandler(responseBody: "[]");
        using var client = CreateClient(handler);

        var session = await client.GetSessionAsync(
            new DateTimeOffset(2026, 7, 25, 15, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(EquityTradingSession.Closed, session.Session);
        Assert.Null(session.RegularOpenUtc);
        Assert.Null(session.RegularCloseUtc);
    }

    [Fact]
    public async Task GetEligibilityAsync_MapsOvernightEligibilityAndCachesAsset()
    {
        using var handler = new CapturingHandler(
            responseBody: """{"symbol":"MSFT","status":"active","tradable":true,"overnight_tradable":true}""");
        using var client = CreateClient(handler);

        var first = await client.GetEligibilityAsync("msft", CancellationToken.None);
        var second = await client.GetEligibilityAsync("MSFT", CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(first.Active);
        Assert.True(first.Tradable);
        Assert.True(first.OvernightTradable);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("/v2/assets/MSFT", handler.Path);
    }

    private AlpacaBrokerClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler, disposeHandler: false),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret"
            },
            CreateArchiveWriter());

    private static BrokerEntryOrder CreateEntryOrder(bool submitOutsideRegularHours) =>
        new(
            new FinalizedOrder(
                "MSFT",
                "Test Strategy",
                10,
                100m,
                98m,
                104m,
                new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
                "TEST-B-MSFT-20260721-001-12345678"),
            "buy",
            "limit",
            "day",
            submitOutsideRegularHours);

    private sealed class CapturingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """{"id":"oco-order-id"}""") : HttpMessageHandler, IDisposable
    {
        public byte[] ResponseBytes { get; } = Encoding.UTF8.GetBytes(responseBody);

        public string RequestJson { get; private set; } = String.Empty;
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
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
