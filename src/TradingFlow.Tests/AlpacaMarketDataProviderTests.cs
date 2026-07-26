using System.Net;
using TradingFlow.Alpaca;

namespace TradingFlow.Tests;

public class AlpacaMarketDataProviderTests
{
    [Fact]
    public void ResolveMarketDataStreamUrl_DefaultsToSip()
    {
        var options = AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper);

        Assert.Equal("sip", options.ResolveMarketDataFeed());
        Assert.Equal(
            new Uri("wss://stream.data.alpaca.markets/v2/sip"),
            options.ResolveMarketDataStreamUrl());
    }

    [Fact]
    public void ResolveMarketDataStreamUrl_RejectsIexUnlessFallbackIsExplicit()
    {
        var options = AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { MarketDataFeed = "iex" };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolveMarketDataStreamUrl);

        Assert.Contains("IEX fallback is disabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMarketDataStreamUrl_AllowsExplicitDevelopmentIexFallback()
    {
        var options = AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
        {
            MarketDataFeed = "IEX",
            AllowIexFallback = true
        };

        Assert.Equal("iex", options.ResolveMarketDataFeed());
        Assert.Equal(
            new Uri("wss://stream.data.alpaca.markets/v2/iex"),
            options.ResolveMarketDataStreamUrl());
    }

    [Fact]
    public void ResolveMarketDataFeed_RejectsUnknownFeed()
    {
        var options = AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { MarketDataFeed = "unknown" };

        Assert.Throws<ArgumentException>(options.ResolveMarketDataFeed);
    }

    [Theory]
    [InlineData("[{\"T\":\"success\",\"msg\":\"authenticated\"}]")]
    [InlineData("{\"status\":\"authorized\"}")]
    [InlineData("{\"status\":\"authenticated\"}")]
    public void IsAuthorizedResponse_AcceptsOnlyExplicitAuthentication(string response)
    {
        Assert.True(AlpacaStreamClient.IsAuthorizedResponse(response));
    }

    [Theory]
    [InlineData("[{\"T\":\"success\",\"msg\":\"connected\"}]")]
    [InlineData("[{\"T\":\"error\",\"msg\":\"auth failed\"}]")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void IsAuthorizedResponse_RejectsAmbiguousOrFailedResponses(string response)
    {
        Assert.False(AlpacaStreamClient.IsAuthorizedResponse(response));
    }

    [Fact]
    public async Task GetBarsAsync_FollowsNextPageToken_ForMultiSymbolResponses()
    {
        using var handler = new PagedBarsHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new AlpacaMarketDataProvider(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
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
        Assert.Contains("adjustment=all", handler.RequestUris[0].Query);
        Assert.Contains("page_token=token-2", handler.RequestUris[1].Query);
        Assert.Contains("adjustment=all", handler.RequestUris[1].Query);
        Assert.Contains("AMD", bars.Select(x => x.Ticker));
        Assert.Contains("MU", bars.Select(x => x.Ticker));
        Assert.Contains("NVDA", bars.Select(x => x.Ticker));
    }

    [Fact]
    public async Task GetBarsAsync_MapsClassShareSymbolAtProviderBoundary()
    {
        using var handler = new ClassShareBarsHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new AlpacaMarketDataProvider(
            httpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = "test-key",
                SecretKey = "test-secret",
                MarketDataFeed = "sip"
            });

        var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
        await foreach (var bar in provider.GetBarsAsync(
                           ["BRK-B"],
                           ["1d"],
                           new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero),
                           new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero),
                           CancellationToken.None))
        {
            bars.Add(bar);
        }

        Assert.Contains("symbols=BRK.B", handler.RequestUri.Query);
        Assert.Equal("BRK-B", Assert.Single(bars).Ticker);
    }

    [Theory]
    [InlineData("BRK-B", "BRK.B")]
    [InlineData("brk.b", "BRK.B")]
    [InlineData("MSFT", "MSFT")]
    public void AlpacaSymbolMapper_UsesProviderDotNotation(string input, string expectedProviderSymbol)
    {
        Assert.Equal(expectedProviderSymbol, AlpacaSymbolMapper.ToProviderSymbol(input));
        Assert.Equal(expectedProviderSymbol.Replace('.', '-'), AlpacaSymbolMapper.ToCanonicalSymbol(expectedProviderSymbol));
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

    private sealed class ClassShareBarsHandler : HttpMessageHandler
    {
        public Uri RequestUri { get; private set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "bars": {
                    "BRK.B": [
                      { "t": "2026-06-08T04:00:00Z", "o": 500.0, "h": 505.0, "l": 499.0, "c": 504.0, "v": 1000 }
                    ]
                  },
                  "next_page_token": null
                }
                """)
            });
        }
    }
}
