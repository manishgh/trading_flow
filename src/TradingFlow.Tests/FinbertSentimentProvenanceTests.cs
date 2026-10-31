using System.Net;
using System.Net.Http.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class FinbertSentimentProvenanceTests
{
    private static readonly string Revision = new('a', 40);
    private static readonly Uri Endpoint = new("http://sentiment.test/");

    // A successful score must retain the exact model commit through the existing classification-version field.
    [Fact]
    public async Task VerifiedInference_RetainsActualLoadedRevision()
    {
        using var client = Client(new
        {
            label = "negative", score = -0.9m, confidence = 0.9m,
            model = "ProsusAI/finbert", model_revision = Revision, model_loaded = true
        });
        var fallback = new Fallback();
        var result = await new FinbertHttpSentimentAnalyzer(client, Endpoint, fallback: fallback)
            .AnalyzeAsync(Article(), default);

        Assert.Equal(-0.9m, result.Score);
        Assert.Equal($"finbert-http:ProsusAI/finbert@{Revision}", result.AnalyzerName);
        Assert.Equal(0, fallback.Calls);
    }

    // Reachable servers with absent, floating or inconsistent metadata must not be recorded as a verified model.
    [Theory]
    [InlineData("main", true, "ProsusAI/finbert", 0.9, 0.9)]
    [InlineData(null, true, "ProsusAI/finbert", 0.9, 0.9)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, "ProsusAI/finbert", 0.9, 0.9)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, "different/model", 0.9, 0.9)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, "ProsusAI/finbert", 2.0, 0.9)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, "ProsusAI/finbert", 0.7, 0.9)]
    public async Task UnverifiedOrInvalidInference_UsesActualFallbackIdentity(string? revision, bool loaded,
        string model, double score, double confidence)
    {
        using var client = Client(new { label = "positive", score, confidence, model, model_revision = revision, model_loaded = loaded });
        var fallback = new Fallback();
        var result = await new FinbertHttpSentimentAnalyzer(client, Endpoint, fallback: fallback)
            .AnalyzeAsync(Article(), default);
        Assert.Equal("vader", result.AnalyzerName);
        Assert.Equal(0.25m, result.Score);
        Assert.Equal(1, fallback.Calls);
    }

    // A failed-load response falls back explicitly, while cancellation requested by the caller must propagate.
    [Fact]
    public async Task FailedLoad_UsesFallbackButCallerCancellationDoesNot()
    {
        using var client = Client(new { detail = "pinned model is not cached" }, HttpStatusCode.ServiceUnavailable);
        var fallback = new Fallback();
        var analyzer = new FinbertHttpSentimentAnalyzer(client, Endpoint, fallback: fallback);
        Assert.Equal("vader", (await analyzer.AnalyzeAsync(Article(), default)).AnalyzerName);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(Article(), cancelled.Token));
        Assert.Equal(1, fallback.Calls);
    }

    // Health succeeds only after both the actual and configured immutable revision are confirmed by a loaded model.
    [Theory]
    [InlineData(true, "ready", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData(false, "ready", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(true, "load_failed", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(true, "ready", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false)]
    public async Task Health_ReadsLoadedEvidenceRatherThanConfiguration(bool loaded, string status,
        string configured, bool expectedReady)
    {
        using var client = Client(new
        {
            status, model = "ProsusAI/finbert", model_loaded = loaded, model_revision = Revision,
            configured_revision = configured, detail = (string?)null
        });
        var health = await new FinbertHealthReader(client).ReadAsync(Endpoint, default);
        Assert.Equal(expectedReady, health.IsReady);
        if (expectedReady) Assert.Contains(Revision, health.Detail);
    }

    // The previous endpoint-only health contract must not be accepted as proof that weights are loaded.
    [Fact]
    public async Task OldHealthResponse_DoesNotClaimLoadedModel()
    {
        using var client = Client(new { status = "ok", model = "ProsusAI/finbert" });
        var health = await new FinbertHealthReader(client).ReadAsync(Endpoint, default);
        Assert.False(health.IsReady);
        Assert.Contains("not confirmed", health.Detail);
    }

    // Every HTTP exchange remains in-process; no sidecar, model weights or provider account is contacted.
    private static HttpClient Client(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new Handler(payload, status));

    // Keep the article deterministic and independent of operational news storage.
    private static NewsArticle Article() => new("fixture", "article-1", "Company reduces guidance",
        DateTimeOffset.Parse("2026-10-20T12:00:00Z"), ["MSFT"]);

    private sealed class Handler(object payload, HttpStatusCode status) : HttpMessageHandler
    {
        // Return controlled protocol responses and preserve cancellation behavior without network requests.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(payload) });
        }
    }

    private sealed class Fallback : ISentimentAnalyzer
    {
        public int Calls { get; private set; }
        public string AnalyzerName => "vader";

        // Return a separately identified fallback so tests detect any false FinBERT attribution.
        public Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SentimentResult(0.25m, "positive", AnalyzerName));
        }
    }
}
