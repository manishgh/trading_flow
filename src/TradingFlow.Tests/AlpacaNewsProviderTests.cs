using System.Net;
using System.Text;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class AlpacaNewsProviderTests
{
    [Fact]
    public async Task GetCatalystsAsync_FollowsPaginationAndMapsArticleMetadata()
    {
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "news": [
                    {
                      "id": 101,
                      "headline": "MU raises guidance on strong AI memory demand",
                      "summary": "Management cited stronger datacenter orders.",
                      "source": "benzinga",
                      "url": "https://example.test/mu",
                      "created_at": "2026-06-09T13:30:00Z",
                      "updated_at": "2026-06-09T13:35:00Z",
                      "symbols": ["MU"]
                    }
                  ],
                  "next_page_token": "page-2"
                }
                """)
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "news": [
                    {
                      "id": "article-2",
                      "headline": "MU pulls back after early spike",
                      "created_at": "2026-06-09T14:30:00Z",
                      "symbols": ["MU", "NVDA"]
                    }
                  ]
                }
                """)
            });

        var analyzer = new DeterministicSentimentAnalyzer();
        var provider = new AlpacaNewsProvider(
            new HttpClient(handler),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            sentimentAnalyzer: analyzer);

        var beforeFetch = DateTimeOffset.UtcNow;
        var catalysts = await provider.GetCatalystsAsync(
            "MU",
            DateTimeOffset.Parse("2026-06-09T13:00:00Z"),
            DateTimeOffset.Parse("2026-06-09T15:00:00Z"),
            CancellationToken.None);
        var afterFetch = DateTimeOffset.UtcNow;

        Assert.Equal(2, catalysts.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page_token=page-2", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("101", catalysts[0].ExternalId);
        Assert.Equal("benzinga", catalysts[0].Source);
        Assert.Equal("https://example.test/mu", catalysts[0].Url);
        Assert.Equal("Management cited stronger datacenter orders.", catalysts[0].Summary);
        Assert.NotNull(catalysts[0].ReceivedAt);
        Assert.InRange(catalysts[0].ReceivedAt!.Value, beforeFetch.AddSeconds(-1), afterFetch.AddSeconds(1));
        Assert.Equal(2, analyzer.CallCount);
    }

    [Fact]
    public async Task GetCatalystsAsync_UsesCachedSentimentForRepeatedArticle()
    {
        var payload = """
        {
          "news": [
            {
              "id": "cache-me",
              "headline": "POET wins new optical contract",
              "created_at": "2026-06-09T13:30:00Z",
              "symbols": ["POET"]
            }
          ]
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(payload) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(payload) });

        var analyzer = new DeterministicSentimentAnalyzer();
        var provider = new AlpacaNewsProvider(
            new HttpClient(handler),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            sentimentAnalyzer: analyzer);

        await provider.GetCatalystsAsync("POET", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);
        await provider.GetCatalystsAsync("POET", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, analyzer.CallCount);
    }

    [Fact]
    public async Task GetCatalystsAsync_ReusesCachedSentimentWithoutReusingCachedTicker()
    {
        var payload = """
        {
          "news": [
            {
              "id": "shared-article",
              "headline": "Quantum stocks rise on sector catalyst",
              "created_at": "2026-06-09T13:30:00Z",
              "symbols": ["POET", "RGTI"]
            }
          ]
        }
        """;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(payload) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(payload) });

        var analyzer = new DeterministicSentimentAnalyzer();
        var provider = new AlpacaNewsProvider(
            new HttpClient(handler),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            sentimentAnalyzer: analyzer);

        var poet = await provider.GetCatalystsAsync("POET", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);
        var rgti = await provider.GetCatalystsAsync("RGTI", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("POET", Assert.Single(poet).Ticker);
        Assert.Equal("RGTI", Assert.Single(rgti).Ticker);
        Assert.Equal(1, analyzer.CallCount);
    }
    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class DeterministicSentimentAnalyzer : ISentimentAnalyzer
    {
        public int CallCount { get; private set; }

        public string AnalyzerName => "test";

        public Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken)
        {
            CallCount++;
            var score = article.Headline.Contains("pulls back", StringComparison.OrdinalIgnoreCase) ? -0.7m : 0.8m;
            return Task.FromResult(new SentimentResult(score, score < 0 ? "negative" : "positive", AnalyzerName, 0.9m));
        }
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
