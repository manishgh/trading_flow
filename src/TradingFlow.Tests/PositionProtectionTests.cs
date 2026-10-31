using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class PositionProtectionTests
{
    /// <summary>Only actual closing-side executable quantity can cover a holding; opening stops are never substituted for protection.</summary>
    [Theory]
    [InlineData("long", true, true)]
    [InlineData("short", true, true)]
    [InlineData("opening-long", false, true)]
    [InlineData("opening-short", false, true)]
    [InlineData("unknown", false, false)]
    [InlineData("wrong-side", false, false)]
    [InlineData("partial", false, true)]
    [InlineData("partial-fill", true, true)]
    [InlineData("pending", false, false)]
    [InlineData("held", false, false)]
    [InlineData("done_for_day", false, false)]
    [InlineData("suspended", false, false)]
    [InlineData("unknown-status", false, false)]
    [InlineData("invalid-quantity", false, false)]
    [InlineData("excess", false, false)]
    [InlineData("terminal", false, true)]
    public async Task Coverage_RequiresActualDirectionRoleAndRemainingQuantity(string scenario, bool protectedPosition, bool known)
    {
        using var fixture = new Fixture();
        var quantity = scenario is "short" or "opening-short" ? -10m : 10m;
        var side = quantity > 0 ? "sell" : "buy";
        string? role = quantity > 0 ? "sell_to_close" : "buy_to_close";
        decimal? orderQuantity = 10;
        decimal filled = 0;
        var status = "new";
        switch (scenario)
        {
            case "opening-long": side = "buy"; role = "buy_to_open"; break;
            case "opening-short": side = "sell"; role = "sell_to_open"; break;
            case "unknown": role = null; break;
            case "wrong-side": side = "buy"; break;
            case "partial": orderQuantity = 4; break;
            case "partial-fill": orderQuantity = 12; filled = 2; status = "partially_filled"; break;
            case "pending": status = "pending_new"; break;
            case "held": status = "held"; break;
            case "done_for_day": status = "done_for_day"; break;
            case "suspended": status = "suspended"; break;
            case "unknown-status": status = "unexpected_new_state"; break;
            case "invalid-quantity": orderQuantity = null; break;
            case "excess": orderQuantity = 11; break;
            case "terminal": status = "canceled"; break;
        }
        fixture.Handler.Body = JsonSerializer.Serialize(new[] { Order(side, role, orderQuantity, filled, status) });
        var result = Assert.Single(await fixture.Service.GetAsync([Holding(quantity)], default)).Value;
        Assert.Equal(protectedPosition, result.HasBrokerStop);
        Assert.Equal(known, result.IsKnown);
        if (scenario == "partial") Assert.Contains("Partial broker stop coverage: 4 of 10", result.Detail);
        Assert.Single(fixture.Handler.Paths);
        Assert.Contains("status=open", fixture.Handler.Paths[0], StringComparison.Ordinal);
    }

    /// <summary>An exact owned closing intent can qualify broker orders lacking position_intent; another account or an entry cannot.</summary>
    [Theory]
    [InlineData(OrderIntentKind.ProtectiveStop, "account-fixture", true, true)]
    [InlineData(OrderIntentKind.PositionExit, "account-fixture", true, true)]
    [InlineData(OrderIntentKind.OperatorEntry, "account-fixture", false, true)]
    [InlineData(OrderIntentKind.ProtectiveStop, "another-account", false, false)]
    public async Task OwnedIntent_MustIdentifyTheBoundAccountAndClosingRole(OrderIntentKind kind, string account, bool protectedPosition, bool known)
    {
        using var fixture = new Fixture();
        fixture.Handler.Body = JsonSerializer.Serialize(new[] { Order("sell", null, 10, 0, "new") });
        fixture.Intents.Setup(item => item.GetByClientOrderIdAsync("stop-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "stop-client", AccountId = account, Symbol = "AAPL", Side = "sell", Kind = kind });
        var result = Assert.Single(await fixture.Service.GetAsync([Holding(10)], default)).Value;
        Assert.Equal(protectedPosition, result.HasBrokerStop);
        Assert.Equal(known, result.IsKnown);
    }

    /// <summary>The broker's nested stop leg can inherit closing ownership from its exact journaled entry parent.</summary>
    [Fact]
    public async Task OwnedParentEntry_QualifiesItsOppositeSideNestedStop()
    {
        using var fixture = new Fixture();
        var parent = Order("buy", "buy_to_open", 10, 10, "filled", "entry", "entry-client", "limit");
        parent["legs"] = new[] { Order("sell", null, 10, 0, "new") };
        fixture.Handler.Body = JsonSerializer.Serialize(new[] { parent });
        fixture.Intents.Setup(item => item.GetByClientOrderIdAsync("entry-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "entry-client", AccountId = "account-fixture", Symbol = "AAPL", Side = "buy", Kind = OrderIntentKind.StrategyEntry });
        var result = Assert.Single(await fixture.Service.GetAsync([Holding(10)], default)).Value;
        Assert.True(result.HasBrokerStop);
        Assert.True(result.IsKnown);
    }

    /// <summary>Independent closing stops may cover disjoint portions; duplicate records and excess total quantity cannot assert full protection.</summary>
    [Theory]
    [InlineData("split", true)]
    [InlineData("duplicate", false)]
    [InlineData("excess", false)]
    public async Task MultipleStops_UseUniqueRemainingClosingQuantities(string scenario, bool protectedPosition)
    {
        using var fixture = new Fixture();
        var first = Order("sell", "sell_to_close", scenario == "split" ? 5 : 10, 0, "new", "first", "first-client");
        var second = Order("sell", "sell_to_close", 5, 0, "new", scenario == "duplicate" ? "first" : "second", "second-client");
        fixture.Handler.Body = JsonSerializer.Serialize(new[] { first, second });
        var result = Assert.Single(await fixture.Service.GetAsync([Holding(10)], default)).Value;
        Assert.Equal(protectedPosition, result.HasBrokerStop);
        Assert.Equal(scenario == "split", result.IsKnown);
    }

    /// <summary>A failure or saturated provider page is unknown rather than evidence that a full closing stop exists.</summary>
    [Theory]
    [InlineData("401")]
    [InlineData("saturated")]
    [InlineData("malformed")]
    public async Task IncompleteOrderRead_RemainsUnknown(string scenario)
    {
        using var fixture = new Fixture();
        if (scenario == "401") fixture.Handler.Status = HttpStatusCode.Unauthorized;
        fixture.Handler.Body = scenario switch {
            "saturated" => JsonSerializer.Serialize(Enumerable.Range(0, 500).Select(index => Order("sell", "sell_to_close", 10, 0, "new", "stop-" + index))),
            "malformed" => "{}",
            _ => "{\"message\":\"fixture unauthorized\"}"
        };
        var result = Assert.Single(await fixture.Service.GetAsync([Holding(10)], default)).Value;
        Assert.False(result.IsKnown);
        Assert.False(result.HasBrokerStop);
    }

    /// <summary>Uses supplied broker holdings without issuing another account or position query.</summary>
    private static DeskHolding Holding(decimal quantity) => new("AAPL", quantity, 100, quantity * 100, quantity * 105, 50);

    /// <summary>Creates the provider's real open-order shape with explicit role and cumulative fill quantity.</summary>
    private static Dictionary<string, object?> Order(string side, string? role, decimal? quantity, decimal filled, string status,
        string id = "stop-order", string client = "stop-client", string type = "stop") => new() {
        ["id"] = id, ["symbol"] = "AAPL", ["side"] = side, ["status"] = status, ["type"] = type,
        ["client_order_id"] = client, ["qty"] = quantity?.ToString(CultureInfo.InvariantCulture),
        ["filled_qty"] = filled.ToString(CultureInfo.InvariantCulture), ["stop_price"] = type == "stop" ? "95" : null,
        ["limit_price"] = type == "limit" ? "100" : null, ["created_at"] = "2026-10-21T13:30:00Z",
        ["updated_at"] = "2026-10-21T13:31:00Z", ["position_intent"] = role
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-protection-tests", Guid.NewGuid().ToString("N"));
        public Handler Handler { get; } = new();
        public Mock<IOrderIntentRepository> Intents { get; } = new(MockBehavior.Strict);
        public PositionProtectionService Service { get; }

        /// <summary>Builds the production service with fake transport, a mock intent reader and isolated archive storage.</summary>
        public Fixture()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Alpaca:KeyId"] = "fixture-key", ["Alpaca:SecretKey"] = "fixture-secret", ["Alpaca:ExpectedAccountId"] = "account-fixture"
            }).Build();
            Intents.Setup(item => item.GetByClientOrderIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((OrderIntentRecord?)null);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(Handler, disposeHandler: false));
            Service = new(new AlpacaCredentialProvider(config), new FileSystemRawArchiveWriter(new RawArchiveOptions(root)),
                NullLogger<PositionProtectionService>.Instance, Intents.Object, clients.Object, config);
        }

        /// <summary>Deletes only this test-owned archive directory after checking its absolute parent.</summary>
        public void Dispose()
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tradingflow-protection-tests"));
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != boundary) throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Handler.Dispose();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Paths { get; } = [];

        /// <summary>Captures only fake broker requests and returns controlled payloads; it cannot perform network I/O.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
