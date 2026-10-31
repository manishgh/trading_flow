using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class AlpacaFillHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-fill-history-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Verify full pagination and one cached order-role lookup for many partial executions of the same order.</summary>
    [Fact]
    public async Task FullPage_RequiresNextPage_AndCachesOrderIntent()
    {
        var page = 0;
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal) ? "[]" : path.StartsWith("/v2/account/activities", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(++page == 1 ? Enumerable.Range(1, 100).Select(Fill).ToArray() : [])
            : "{\"id\":\"order-1\",\"position_intent\":\"buy_to_open\"}");
        using var client = Client(handler);
        var result = await client.GetFillHistoryAsync("account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(result.Complete);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(100, result.Activities.Count);
        Assert.All(result.Activities, fill => Assert.Equal("buy_to_open", fill.PositionIntent));
        Assert.Single(handler.Paths, path => path.StartsWith("/v2/orders/", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, path => path.Contains("page_token=activity-100", StringComparison.Ordinal));
        Assert.Equal(4, result.RawArchiveIds.Count);
    }

    /// <summary>A successful empty response establishes zero fills; failed HTTP responses never do.</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task EmptyHistory_RequiresSuccessfulArchivedResponse(HttpStatusCode status)
    {
        using var handler = new Handler(_ => "[]", status);
        using var client = Client(handler);
        var task = client.GetFillHistoryAsync("account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None);
        if (status == HttpStatusCode.OK)
        {
            var result = await task;
            Assert.True(result.Complete);
            Assert.Empty(result.Activities);
            Assert.Equal(2, result.RawArchiveIds.Count);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    /// <summary>A full final allowed page is incomplete even when all records themselves are valid.</summary>
    [Fact]
    public async Task ExhaustedPageBudget_RejectsInsteadOfReturningPartialHistory()
    {
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal) ? "[]" : path.StartsWith("/v2/orders/", StringComparison.Ordinal)
            ? "{\"id\":\"order-1\",\"position_intent\":\"buy_to_open\"}"
            : JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(Fill)));
        using var client = Client(handler, maximumPages: 1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFillHistoryAsync(
            "account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Contains("page budget", error.Message);
    }

    /// <summary>Repeated pagination IDs and malformed response shapes cannot establish complete daily evidence.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidPages_AreRejected(bool repeated)
    {
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal) ? "[]" : path.StartsWith("/v2/orders/", StringComparison.Ordinal)
            ? "{\"id\":\"order-1\",\"position_intent\":\"buy_to_open\"}"
            : repeated ? JsonSerializer.Serialize(new[] { Fill(1), Fill(1) }) : "{}");
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetFillHistoryAsync(
            "account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None));
    }

    /// <summary>Missing broker intent stays unknown so the repository must use local ownership or reject it explicitly.</summary>
    [Fact]
    public async Task MissingOrderRole_IsNotInferredFromBuySide()
    {
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal) ? "[]" : path.StartsWith("/v2/orders/", StringComparison.Ordinal)
            ? "{\"id\":\"order-1\"}" : JsonSerializer.Serialize(new[] { Fill(1) }));
        using var client = Client(handler);
        var result = await client.GetFillHistoryAsync("account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Null(Assert.Single(result.Activities).PositionIntent);
    }

    /// <summary>A saturated pending-orders page cannot be treated as complete account capacity evidence.</summary>
    [Fact]
    public async Task SaturatedPendingOrders_RejectsCompleteHistoryClaim()
    {
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal)
            ? "[" + String.Join(',', Enumerable.Repeat("{}", 500)) + "]" : "[]");
        using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetFillHistoryAsync(
            "account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None));
    }

    /// <summary>Preserve the explicit opening role and the archive identity for the whole-account pending snapshot.</summary>
    [Fact]
    public async Task PendingOpeningOrder_IsIncludedWithBrokerRole()
    {
        var timestamp = TradingLimitDay.StartUtc(TradingLimitDay.At(DateTimeOffset.UtcNow));
        var pending = new[] { new { id = "pending-1", symbol = "AAPL", side = "buy", status = "new", type = "limit",
            client_order_id = "external-client", qty = "10", filled_qty = "0", created_at = timestamp,
            updated_at = timestamp, position_intent = "buy_to_open" } };
        using var handler = new Handler(path => path.StartsWith("/v2/orders?", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(pending) : "[]");
        using var client = Client(handler);
        var result = await client.GetFillHistoryAsync("account-1", TradingLimitDay.At(DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal("buy_to_open", Assert.Single(result.PendingOrders!).PositionIntent);
        Assert.Equal(2, result.RawArchiveIds.Count);
    }

    /// <summary>Build a transport-only adapter with isolated raw archives and no network-capable handler.</summary>
    private AlpacaBrokerClient Client(HttpMessageHandler handler, int maximumPages = 100) => new(
        new HttpClient(handler, disposeHandler: false), new HttpClient(handler, disposeHandler: false),
        AlpacaOptions.Create(ProductionProfile.Paper) with
        {
            KeyId = "fixture-key", SecretKey = "fixture-secret", AccountFillHistoryMaxPages = maximumPages
        }, new FileSystemRawArchiveWriter(new RawArchiveOptions(root)));

    /// <summary>Create real provider field shapes in the current NY session, safely before the frozen request cutoff.</summary>
    private static object Fill(int sequence) => new
    {
        id = "activity-" + sequence, order_id = "order-1", symbol = "MSFT", activity_type = "FILL",
        type = "partial_fill", side = "buy", qty = "1", cum_qty = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        transaction_time = TradingLimitDay.StartUtc(TradingLimitDay.At(DateTimeOffset.UtcNow))
    };

    private sealed class Handler(Func<string, string> body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        /// <summary>Record each fake request and return a deterministic archived JSON response.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body(path), Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Delete only this fixture's GUID-named raw archive tree after verifying its expected parent.</summary>
    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tradingflow-fill-history-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
