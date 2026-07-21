using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class AlpacaNewsProviderTests : IDisposable
{
    private readonly string archiveRoot = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-alpaca-news-archive-tests",
        Guid.NewGuid().ToString("N"));

    private IRawArchiveWriter CreateArchiveWriter() =>
        new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot));

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
            CreateArchiveWriter(),
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
            CreateArchiveWriter(),
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
            CreateArchiveWriter(),
            sentimentAnalyzer: analyzer);

        var poet = await provider.GetCatalystsAsync("POET", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);
        var rgti = await provider.GetCatalystsAsync("RGTI", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("POET", Assert.Single(poet).Ticker);
        Assert.Equal("RGTI", Assert.Single(rgti).Ticker);
        Assert.Equal(1, analyzer.CallCount);
    }

    [Fact]
    public async Task GetCatalystsAsync_ArchivesExactPayloadBeforeParsing()
    {
        var payload = """
        {
          "news": [
            {
              "id": "archive-me",
              "headline": "RGTI announces a new quantum system",
              "created_at": "2026-06-09T13:30:00Z",
              "symbols": ["RGTI"]
            }
          ]
        }
        """;
        var expectedBytes = Encoding.UTF8.GetBytes(payload);
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(payload) });
        var provider = new AlpacaNewsProvider(
            new HttpClient(handler),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            CreateArchiveWriter(),
            sentimentAnalyzer: new DeterministicSentimentAnalyzer());

        var catalysts = await provider.GetCatalystsAsync(
            "RGTI",
            DateTimeOffset.Parse("2026-06-09T13:00:00Z"),
            DateTimeOffset.Parse("2026-06-09T14:00:00Z"),
            CancellationToken.None);

        Assert.Single(catalysts);
        var payloadPath = Assert.Single(
            Directory.EnumerateFiles(archiveRoot, "*.json", SearchOption.AllDirectories),
            path => !path.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(payloadPath));
        var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(
            await File.ReadAllTextAsync(payloadPath + ".manifest.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal("alpaca", manifest.Source);
        Assert.Equal("news-rest-page", manifest.ArtifactType);
        Assert.Equal(200, manifest.Http!.StatusCode);
        Assert.Equal("RGTI", manifest.CorrelationId);
        Assert.Equal(manifest.ReceivedAtUtc, Assert.Single(catalysts).ReceivedAt);
        Assert.DoesNotContain("key", await File.ReadAllTextAsync(payloadPath + ".manifest.json"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(payloadPath + ".manifest.json"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCatalystsAsync_ArchivesRateLimitResponseBeforeRetrying()
    {
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent("""{"message":"rate limited"}""")
        };
        var handler = new QueueHttpMessageHandler(
            rateLimited,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent("""{"news":[]}""") });
        var provider = new AlpacaNewsProvider(
            new HttpClient(handler),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            CreateArchiveWriter(),
            sentimentAnalyzer: new DeterministicSentimentAnalyzer());

        var catalysts = await provider.GetCatalystsAsync(
            "POET",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Empty(catalysts);
        Assert.Equal(2, handler.Requests.Count);
        var manifests = Directory.EnumerateFiles(archiveRoot, "*.manifest.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<RawArchiveManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .OrderBy(manifest => manifest.ReceivedAtUtc)
            .ToArray();
        Assert.Equal([429, 200], manifests.Select(manifest => manifest.Http!.StatusCode).ToArray());
    }

    [Fact]
    public async Task GetCatalystsAsync_DoesNotParseWhenArchiveCommitFails()
    {
        var provider = new AlpacaNewsProvider(
            new HttpClient(new QueueHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent("<invalid-json>") })),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with { KeyId = "key", SecretKey = "secret" },
            new FailingRawArchiveWriter(),
            sentimentAnalyzer: new DeterministicSentimentAnalyzer());

        var exception = await Assert.ThrowsAsync<IOException>(() => provider.GetCatalystsAsync(
            "MU",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None));

        Assert.Equal("simulated archive failure", exception.Message);
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
