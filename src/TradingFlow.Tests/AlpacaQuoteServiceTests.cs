using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AlpacaQuoteServiceTests
{
    [Fact]
    public async Task GetLatestQuotesAsync_IsolatesInvalidSymbolWithoutDroppingValidQuotes()
    {
        var handler = new InvalidSymbolQuoteHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://data.alpaca.markets") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var service = new AlpacaQuoteService(
            new AlpacaCredentialProvider(configuration),
            new StubHttpClientFactory(client),
            NullLogger<AlpacaQuoteService>.Instance);

        var results = await service.GetLatestQuotesAsync(["RGTI", "UI2T"], "sip", CancellationToken.None);

        Assert.Equal(16.23m, results["RGTI"].BidPrice);
        Assert.Equal(16.24m, results["RGTI"].AskPrice);
        Assert.Null(results["UI2T"].MidPrice);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetPreviousClosesAsync_ReadsSnapshotsOnceADayAndDropsInvalidSymbols()
    {
        var handler = new SnapshotHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://data.alpaca.markets") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:KeyId"] = "test-key",
                ["Alpaca:SecretKey"] = "test-secret"
            })
            .Build();
        var service = new AlpacaQuoteService(
            new AlpacaCredentialProvider(configuration),
            new StubHttpClientFactory(client),
            NullLogger<AlpacaQuoteService>.Instance);
        var now = DateTimeOffset.Parse("2026-09-29T15:00:00Z");

        var first = await service.GetPreviousClosesAsync(["MU", "UI2T"], "sip", now, CancellationToken.None);
        Assert.Equal(100m, first["MU"].Close);
        Assert.False(first.ContainsKey("UI2T"));
        Assert.All(handler.Paths, path => Assert.StartsWith("/v2/stocks/snapshots", path));
        var requestsAfterFirst = handler.Paths.Count;

        // Same New York day: MU comes from the cache, so no request is made.
        var second = await service.GetPreviousClosesAsync(["MU"], "sip", now.AddHours(1), CancellationToken.None);
        Assert.Equal(100m, second["MU"].Close);
        Assert.Equal(requestsAfterFirst, handler.Paths.Count);
    }

    private sealed class SnapshotHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? String.Empty);
            var query = request.RequestUri?.Query ?? String.Empty;
            if (query.Contains("UI2T", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"code=400, message=invalid symbol: UI2T\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"MU\":{\"dailyBar\":{\"t\":\"2026-09-29T04:00:00Z\",\"c\":110}," +
                    "\"prevDailyBar\":{\"t\":\"2026-09-28T04:00:00Z\",\"c\":100}}}")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class InvalidSymbolQuoteHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var query = request.RequestUri?.Query ?? String.Empty;
            if (query.Contains("UI2T", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"code=400, message=invalid symbol: UI2T\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"quotes\":{\"RGTI\":{\"ap\":16.24,\"as\":1900,\"bp\":16.23,\"bs\":600," +
                    "\"t\":\"2026-08-03T14:51:53.975650128Z\"}}}")
            });
        }
    }
}
