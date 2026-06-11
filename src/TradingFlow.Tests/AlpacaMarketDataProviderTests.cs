using System.Net;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

public class AlpacaMarketDataProviderTests
{
    [Fact]
    public async Task GetBarsAsync_FollowsNextPageToken_ForMultiSymbolResponses()
    {
        using var handler = new PagedBarsHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new AlpacaMarketDataProvider(
            httpClient,
            AlpacaOptions.CreateDefault() with
            {
                KeyId = "test-key",
                SecretKey = "test-secret",
                MarketDataFeed = "sip"
            });

        var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync(
                           ["AMD", "MU", "NVDA"],
                           ["15m"],
                           new DateTimeOffset(2026, 6, 8, 20, 0, 0, TimeSpan.Zero),
                           new DateTimeOffset(2026, 6, 8, 21, 0, 0, TimeSpan.Zero),
                           CancellationToken.None))
        {
            bars.Add(bar);
        }

        Assert.Equal(2, handler.RequestUris.Count);
        Assert.DoesNotContain("page_token=", handler.RequestUris[0].Query);
        Assert.Contains("page_token=token-2", handler.RequestUris[1].Query);
        Assert.Contains("AMD", bars.Select(x => x.Ticker));
        Assert.Contains("MU", bars.Select(x => x.Ticker));
        Assert.Contains("NVDA", bars.Select(x => x.Ticker));
    }

    private sealed class PagedBarsHandler : HttpMessageHandler, IDisposable
    {
        private int requestCount;

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestCount++;
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI is required."));

            var json = requestCount == 1
                ? """
                  {
                    "bars": {
                      "AMD": [
                        { "t": "2026-06-08T20:00:00Z", "o": 100.0, "h": 101.0, "l": 99.0, "c": 100.5, "v": 1000 }
                      ],
                      "MU": [
                        { "t": "2026-06-08T20:00:00Z", "o": 200.0, "h": 201.0, "l": 199.0, "c": 200.5, "v": 2000 }
                      ]
                    },
                    "next_page_token": "token-2"
                  }
                  """
                : """
                  {
                    "bars": {
                      "NVDA": [
                        { "t": "2026-06-08T20:00:00Z", "o": 300.0, "h": 301.0, "l": 299.0, "c": 300.5, "v": 3000 }
                      ]
                    },
                    "next_page_token": null
                  }
                  """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }
}
