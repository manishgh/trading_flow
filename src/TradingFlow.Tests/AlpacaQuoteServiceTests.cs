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
