using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MarketPredictorHttpClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAsync_WhenEndpointIsNotConfigured_DoesNotSendRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Request was not expected."));
        var client = CreateClient(handler, configured: false);

        var result = await client.GetAsync("MU", "auto", CancellationToken.None);

        Assert.Equal("not_configured", result.AvailabilityStatus);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_MapsValidPromotedSwingEvidence()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddSeconds(-5))));
        var client = CreateClient(handler);

        var result = await client.GetAsync("mu", "auto", CancellationToken.None);

        Assert.Equal("available", result.AvailabilityStatus);
        Assert.True(result.IsValidPromotedEvidence);
        Assert.Equal("MU", result.Ticker);
        Assert.NotNull(result.Swing);
        Assert.Equal("10b", result.ResolvedHorizon);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("5d")]
    [InlineData("30m")]
    [InlineData("10d")]
    public async Task GetAsync_RejectsWrongSwingHorizon(string horizon)
    {
        var payload = ValidSwingPayload(Now.AddSeconds(-5)).Replace("\"10b\"", $"\"{horizon}\"", StringComparison.Ordinal);
        var client = CreateClient(new StubHandler(_ => JsonResponse(payload)));
        var result = await client.GetAsync("MU", "auto", CancellationToken.None);
        Assert.Equal("incompatible", result.AvailabilityStatus);
        Assert.False(result.IsValidPromotedEvidence);
    }

    [Fact]
    public async Task GetAsync_DoesNotInferHorizonFromAnotherModel()
    {
        var payload = ValidSwingPayload(Now.AddSeconds(-5)).Replace(
            "\"resolved_horizons\": {\"swing\":\"10b\"}",
            "\"resolved_horizons\": {\"other\":\"10b\"}", StringComparison.Ordinal);
        var client = CreateClient(new StubHandler(_ => JsonResponse(payload)));
        var result = await client.GetAsync("MU", "auto", CancellationToken.None);
        Assert.Equal("incompatible", result.AvailabilityStatus);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"swing\":null}")]
    [InlineData("{\"other\":{\"status\":\"promoted\"}}")]
    public async Task GetAsync_RejectsMissingOrNullSwingModel(string models)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(ValidSwingPayload(Now.AddSeconds(-5)))!;
        payload["models"] = System.Text.Json.Nodes.JsonNode.Parse(models);
        var client = CreateClient(new StubHandler(_ => JsonResponse(payload.ToJsonString())));
        var result = await client.GetAsync("MU", "auto", CancellationToken.None);
        Assert.Equal("incompatible", result.AvailabilityStatus);
        Assert.False(result.IsValidPromotedEvidence);
    }

    [Fact]
    public async Task GetAsync_RejectsMissingSwingEvidence()
    {
        var payload = ValidSwingPayload(Now.AddSeconds(-5)).Replace(
            "\"swing\": {",
            "\"swing_removed\": {",
            StringComparison.Ordinal);
        var handler = new StubHandler(_ => JsonResponse(payload));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "auto", CancellationToken.None);

        Assert.Equal("invalid", result.AvailabilityStatus);
        Assert.Contains("Swing prediction", result.AvailabilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_RejectsStaleEvidence()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddMinutes(-16))));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "auto", CancellationToken.None);

        Assert.Equal("stale", result.AvailabilityStatus);
        Assert.False(result.IsValidPromotedEvidence);
    }

    [Fact]
    public async Task GetAsync_RejectsIncompatiblePayload()
    {
        var handler = new StubHandler(_ => JsonResponse("{not-json"));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "auto", CancellationToken.None);

        Assert.Equal("incompatible", result.AvailabilityStatus);
    }

    [Fact]
    public async Task GetHealthAsync_MapsReadyAndNotConfiguredStates()
    {
        var readyHandler = new StubHandler(_ => JsonResponse("{\"status\":\"ready\",\"reason\":\"models promoted\"}"));
        var readyClient = CreateClient(readyHandler);
        var notConfiguredClient = CreateClient(new StubHandler(_ => throw new InvalidOperationException()), configured: false);

        var ready = await readyClient.GetHealthAsync(CancellationToken.None);
        var notConfigured = await notConfiguredClient.GetHealthAsync(CancellationToken.None);

        Assert.Equal("ready", ready.Status);
        Assert.Equal("not configured", notConfigured.Status);
    }

    [Fact]
    public async Task GetBatchAsync_ScoresEveryRequestedSymbolFromOneRequest()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddSeconds(-5), "MU", "NVDA")));
        var client = CreateClient(handler);

        var results = await client.GetBatchAsync(["mu", "NVDA"], "auto", CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("available", results["MU"].AvailabilityStatus);
        Assert.Equal("available", results["NVDA"].AvailabilityStatus);
    }

    [Fact]
    public async Task GetBatchAsync_ReturnsUnavailableForASymbolTheServiceOmitted()
    {
        // A symbol the service did not answer for must come back as unavailable
        // evidence, not be missing: an absent row would read as "no signal"
        // rather than "no answer".
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddSeconds(-5), "MU")));
        var client = CreateClient(handler);

        var results = await client.GetBatchAsync(["MU", "NVDA"], "auto", CancellationToken.None);

        Assert.Equal("available", results["MU"].AvailabilityStatus);
        Assert.Equal("invalid", results["NVDA"].AvailabilityStatus);
        Assert.Contains("No prediction", results["NVDA"].AvailabilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBatchAsync_ChunksToTheServiceTickerCap()
    {
        var tickers = Enumerable.Range(0, 150).Select(index => $"AA{index:D3}").ToArray();
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddSeconds(-5), "AA000")));
        var client = CreateClient(handler);

        var results = await client.GetBatchAsync(tickers, "auto", CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(150, results.Count);
    }

    [Fact]
    public async Task GetBatchAsync_WhenABatchFails_DegradesThatBatchRatherThanThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = CreateClient(handler);

        var results = await client.GetBatchAsync(["MU", "NVDA"], "auto", CancellationToken.None);

        Assert.All(results.Values, result => Assert.Equal("unavailable", result.AvailabilityStatus));
    }

    [Fact]
    public async Task GetBatchAsync_RecordsAnUnusableSymbolWithoutCallingTheService()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidSwingPayload(Now.AddSeconds(-5), "MU")));
        var client = CreateClient(handler);

        var results = await client.GetBatchAsync(["MU", "not a ticker"], "auto", CancellationToken.None);

        Assert.Equal("available", results["MU"].AvailabilityStatus);
        Assert.Equal("invalid", results["NOT A TICKER"].AvailabilityStatus);
    }

    private static MarketPredictorHttpClient CreateClient(StubHandler handler, bool configured = true)
    {
        var resolvedBaseUri = new Uri("http://predictor.test/");
        var options = new MarketPredictorOptions(
            configured ? resolvedBaseUri : null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(15));
        return new MarketPredictorHttpClient(
            new HttpClient(handler) { BaseAddress = resolvedBaseUri },
            options,
            new FixedTimeProvider(Now),
            NullLogger<MarketPredictorHttpClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private static string ValidSwingPayload(DateTimeOffset generatedAtUtc, params string[] tickers)
    {
        var predictions = String.Join(",", (tickers.Length == 0 ? ["MU"] : tickers).Select(PredictionFor));
        return $$$"""
        {
          "request_id": "req-1",
          "generated_at_utc": "{{{generatedAtUtc:O}}}",
          "mode": "swing",
          "horizon": "auto",
          "resolved_horizons": {"swing":"10b"},
          "models": {"swing":{"status":"promoted","model_type":"classifier","schema_version":"1","target":"return_10_sessions"}},
          "predictions": [{{{predictions}}}],
          "errors": [],
          "snapshot_id": "snapshot-1"
        }
        """;
    }

    private static string PredictionFor(string ticker) => $$"""
      {
        "ticker": "{{ticker}}",
        "final_signal": "watch",
        "readiness_status": "valid",
        "errors": [],
        "swing": {
          "probability": 0.61, "decision_score": 0.22, "signal": "watch", "rank": 4,
          "return_1d": 0.01, "volume_z20": 1.2,
          "global_context": {"net_impact":0.1,"active_flashpoints":[]},
          "catalyst": {"status":"confirmed","direction":"positive","score":0.7,"event_count":1,"relevance":0.9,"minutes_since_latest":12,"reasons":[]},
          "readiness": {"status":"valid","reasons":[],"latest_price_date":"2026-07-22","price_feed":"sip","benchmark_status":"valid","market_context_status":"valid","model_status":"promoted","source_status":"valid"}
        }
      }
      """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
