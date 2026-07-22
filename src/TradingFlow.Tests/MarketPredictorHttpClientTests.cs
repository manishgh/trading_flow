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

        var result = await client.GetAsync("MU", "unified", "auto", CancellationToken.None);

        Assert.Equal("not_configured", result.AvailabilityStatus);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_MapsValidPromotedUnifiedEvidence()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidUnifiedPayload(Now.AddSeconds(-5))));
        var client = CreateClient(handler);

        var result = await client.GetAsync("mu", "unified", "auto", CancellationToken.None);

        Assert.Equal("available", result.AvailabilityStatus);
        Assert.True(result.IsValidPromotedEvidence);
        Assert.Equal("MU", result.Ticker);
        Assert.NotNull(result.Swing);
        Assert.NotNull(result.Intraday);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsPartialUnifiedEvidence()
    {
        var payload = ValidUnifiedPayload(Now.AddSeconds(-5)).Replace(
            "\"intraday\": {",
            "\"intraday_removed\": {",
            StringComparison.Ordinal);
        var handler = new StubHandler(_ => JsonResponse(payload));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "unified", "auto", CancellationToken.None);

        Assert.Equal("invalid", result.AvailabilityStatus);
        Assert.Contains("partial", result.AvailabilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_RejectsStaleEvidence()
    {
        var handler = new StubHandler(_ => JsonResponse(ValidUnifiedPayload(Now.AddMinutes(-16))));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "unified", "auto", CancellationToken.None);

        Assert.Equal("stale", result.AvailabilityStatus);
        Assert.False(result.IsValidPromotedEvidence);
    }

    [Fact]
    public async Task GetAsync_RejectsIncompatiblePayload()
    {
        var handler = new StubHandler(_ => JsonResponse("{not-json"));
        var client = CreateClient(handler);

        var result = await client.GetAsync("MU", "unified", "auto", CancellationToken.None);

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

    private static string ValidUnifiedPayload(DateTimeOffset generatedAtUtc) => $$$"""
    {
      "request_id": "req-1",
      "generated_at_utc": "{{{generatedAtUtc:O}}}",
      "mode": "unified",
      "horizon": "auto",
      "resolved_horizons": {"swing":"5d","intraday":"30m"},
      "models": {"swing":{"status":"promoted","model_type":"lightgbm","schema_version":"1","target":"return_5d"}},
      "predictions": [{
        "ticker": "MU",
        "final_signal": "watch",
        "readiness_status": "valid",
        "errors": [],
        "swing": {
          "probability": 0.61, "decision_score": 0.22, "signal": "watch", "rank": 4,
          "return_1d": 0.01, "volume_z20": 1.2,
          "global_context": {"net_impact":0.1,"active_flashpoints":[]},
          "catalyst": {"status":"confirmed","direction":"positive","score":0.7,"event_count":1,"relevance":0.9,"minutes_since_latest":12,"reasons":[]},
          "readiness": {"status":"valid","reasons":[],"latest_price_date":"2026-07-22","price_feed":"sip","benchmark_status":"valid","market_context_status":"valid","model_status":"promoted","source_status":"valid"}
        },
        "intraday": {
          "opportunity_probability": 0.58, "downside_probability": 0.21, "decision_score": 0.19,
          "signal": "watch", "rank": 8, "relative_volume": 1.8, "rsi_14": 58.0,
          "macd_signal_diff": 0.04, "entry_stop_pct": 0.01, "entry_target_pct": 0.03,
          "catalyst": {"status":"confirmed","direction":"positive","score":0.7,"event_count":1,"relevance":0.9,"minutes_since_latest":12,"reasons":[]},
          "readiness": {"status":"valid","reasons":[],"latest_price_date":"2026-07-22","price_feed":"sip","benchmark_status":"valid","market_context_status":"valid","model_status":"promoted","source_status":"valid"}
        }
      }],
      "errors": [],
      "snapshot_id": "snapshot-1"
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
