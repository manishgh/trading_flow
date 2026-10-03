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

    private const string FixtureSourceRoot = @"C:\project\market-predictor";
    private const string FixtureRelativePath = "tests/fixtures/contracts/swing_prediction_response.json";
    private const string FixtureSha256 = "96cbcd133b8e96253b034fabba10624be79b0d80550f043f3869905251bec522";
    private static readonly DateTimeOffset FixtureTime = new(2026, 7, 8, 22, 5, 5, TimeSpan.Zero);

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Contracts", "swing_prediction_response.json");

    [Fact]
    public void GoldenFixture_MatchesPublishedHashAndLocalProducerCopy()
    {
        var copy = File.ReadAllBytes(FixturePath);
        Assert.Equal(FixtureSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(copy)));
        if (Directory.Exists(FixtureSourceRoot))
        {
            var source = Path.Combine(FixtureSourceRoot, FixtureRelativePath);
            Assert.True(File.Exists(source), $"Producer fixture is missing: {source}");
            Assert.Equal(File.ReadAllBytes(source), copy);
        }
    }

    [Theory]
    [InlineData("T000", "positive_setup", null, null)]
    [InlineData("T059", "ranked_candidate", null, null)]
    [InlineData("T060", "abstain", "live_inputs_incomplete", "inputs incomplete at the decision")]
    [InlineData("T061", "abstain", "sector_peer_floor", "too few eligible sector peers for ranking")]
    [InlineData("MISSING", "abstain", "out_of_universe", "not in the point-in-time universe")]
    public async Task GetAsync_GoldenFixtureRetainsEachOutcome(
        string ticker, string signal, string? reason, string? displayReason)
    {
        var payload = await File.ReadAllTextAsync(FixturePath);
        var handler = new StubHandler(_ => JsonResponse(payload));
        var client = CreateClient(handler, now: FixtureTime);

        var result = await client.GetAsync(ticker, "auto", CancellationToken.None);

        AssertGoldenOutcome(result, ticker, signal, reason, displayReason);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetBatchAsync_GoldenFixtureRetainsAllFiveOutcomes()
    {
        var payload = await File.ReadAllTextAsync(FixturePath);
        var handler = new StubHandler(_ => JsonResponse(payload));
        var client = CreateClient(handler, now: FixtureTime);
        var results = await client.GetBatchAsync(["T000", "T059", "T060", "T061", "MISSING"], "auto", CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(5, results.Count);
        AssertGoldenOutcome(results["T000"], "T000", "positive_setup", null, null);
        AssertGoldenOutcome(results["T059"], "T059", "ranked_candidate", null, null);
        AssertGoldenOutcome(results["T060"], "T060", "abstain", "live_inputs_incomplete", "inputs incomplete at the decision");
        AssertGoldenOutcome(results["T061"], "T061", "abstain", "sector_peer_floor", "too few eligible sector peers for ranking");
        AssertGoldenOutcome(results["MISSING"], "MISSING", "abstain", "out_of_universe", "not in the point-in-time universe");
    }

    private static void AssertGoldenOutcome(
        MarketPredictorResult result, string ticker, string signal, string? reason, string? displayReason)
    {
        Assert.Equal("available", result.AvailabilityStatus);
        Assert.Equal("market_predictor.prediction.v4", result.Contract);
        Assert.Equal(ticker, result.Ticker);
        Assert.Equal("10b", result.ResolvedHorizon);
        Assert.Equal(signal, result.FinalSignal);
        var swing = Assert.IsType<PredictorSwingPrediction>(result.Swing);
        Assert.Equal(signal, swing.Signal);
        Assert.NotNull(result.Model);
        Assert.Null(result.Model.TrainingDataEnd);
        if (reason is null)
        {
            Assert.True(result.IsValidPromotedEvidence);
            Assert.Empty(result.Errors);
            Assert.Equal(0.72m, swing.Probability);
            Assert.Equal(ticker == "T000" ? 1 : 60, swing.Rank);
        }
        else
        {
            Assert.False(result.IsValidPromotedEvidence);
            Assert.Equal("invalid", result.ReadinessStatus);
            Assert.Equal(reason, Assert.Single(result.Errors));
            Assert.Null(swing.Probability);
            Assert.Null(swing.DecisionScore);
            Assert.Null(swing.Rank);
            Assert.Equal(displayReason, result.ErrorSummary);
            // The Android API retains the exact code and nullable evidence;
            // its reason view uses the shared presentation formatter.
            var mobile = SymbolIntelligenceService.MapEvidence(result);
            Assert.Equal("available", mobile.AvailabilityStatus);
            Assert.Contains(reason, mobile.ReadinessReasons);
            Assert.Contains(displayReason!, mobile.ReadinessReasons.Select(
                TradingFlow.Contracts.Evidence.PredictorEvidenceDisplay.FormatReason));
            Assert.NotNull(mobile.Swing);
            Assert.Null(mobile.Swing.Probability);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("market_predictor.other")]
    [InlineData("market_predictor.prediction")]
    [InlineData("market_predictor.prediction.v1")]
    [InlineData("market_predictor.prediction.v3")]
    [InlineData("MARKET_PREDICTOR.PREDICTION.V4")]
    [InlineData("MARKET_PREDICTOR.PREDICTION")]
    public async Task BothRequestPaths_RejectUnsupportedContracts(string? contract)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(ValidSwingPayload(Now))!;
        payload["contract_version"] = contract;
        await AssertIncompatibleContractAsync(payload.ToJsonString());
    }

    [Fact]
    public async Task BothRequestPaths_RejectMissingContract()
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(ValidSwingPayload(Now))!.AsObject();
        Assert.True(payload.Remove("contract_version"));
        await AssertIncompatibleContractAsync(payload.ToJsonString());
    }

    private static async Task AssertIncompatibleContractAsync(string payload)
    {
        var client = CreateClient(new StubHandler(_ => JsonResponse(payload)));
        var single = await client.GetAsync("MU", "auto", CancellationToken.None);
        var batch = await client.GetBatchAsync(["MU"], "auto", CancellationToken.None);
        foreach (var result in new[] { single, batch["MU"] })
        {
            Assert.Equal("incompatible", result.AvailabilityStatus);
            Assert.Empty(result.Contract);
            Assert.Null(result.Swing);
            Assert.False(result.IsValidPromotedEvidence);
        }
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task BothRequestPaths_RejectWrongContractType(string value)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(ValidSwingPayload(Now))!;
        payload["contract_version"] = System.Text.Json.Nodes.JsonNode.Parse(value);
        await AssertIncompatibleContractAsync(payload.ToJsonString());
    }

    [Fact]
    public async Task BothRequestPaths_DoNotAcceptTheRetiredContractField()
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(ValidSwingPayload(Now))!.AsObject();
        Assert.True(payload.Remove("contract_version"));
        payload["contract"] = "market_predictor.prediction.v4";
        await AssertIncompatibleContractAsync(payload.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not-json")]
    public async Task BothRequestPaths_RejectMalformedResponse(string payload)
    {
        await AssertIncompatibleContractAsync(payload);
    }

    [Fact]
    public async Task GoldenFixture_ProjectsTheContractNameToMobileJson()
    {
        var payload = await File.ReadAllTextAsync(FixturePath);
        var client = CreateClient(new StubHandler(_ => JsonResponse(payload)), now: FixtureTime);
        var result = await client.GetAsync("T060", "auto", CancellationToken.None);
        var mobile = SymbolIntelligenceService.MapEvidence(result);
        var json = System.Text.Json.JsonSerializer.Serialize(mobile,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("market_predictor.prediction.v4", root.GetProperty("contract").GetString());
        Assert.False(root.TryGetProperty("contractVersion", out _));
        Assert.Equal("available", root.GetProperty("availabilityStatus").GetString());
        Assert.Equal("abstain", root.GetProperty("finalSignal").GetString());
        Assert.Contains("live_inputs_incomplete", root.GetProperty("readinessReasons").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("swing").GetProperty("probability").ValueKind);
    }

    [Fact]
    public async Task BothRequestPaths_SendOnlyPublishedSwingRequestFields()
    {
        var payload = await File.ReadAllTextAsync(FixturePath);
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/predictions/swing", request.RequestUri!.AbsolutePath);
            return JsonResponse(payload);
        });
        var client = CreateClient(handler, now: FixtureTime);
        await client.GetAsync("t000", "auto", CancellationToken.None);
        await client.GetBatchAsync(["T000", "T059", "T060", "T061", "MISSING"], "auto", CancellationToken.None);

        var bodies = handler.RequestBodies;
        Assert.Equal(2, bodies.Count);
        for (var index = 0; index < bodies.Count; index++)
        {
            using var document = System.Text.Json.JsonDocument.Parse(bodies[index]);
            var root = document.RootElement;
            Assert.Equal(["as_of", "horizon", "mode", "tickers"], root.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
            Assert.Equal("swing", root.GetProperty("mode").GetString());
            Assert.Equal("auto", root.GetProperty("horizon").GetString());
            Assert.Equal(FixtureTime, root.GetProperty("as_of").GetDateTimeOffset());
            Assert.Equal(index == 0 ? ["T000"] : ["T000", "T059", "T060", "T061", "MISSING"],
                root.GetProperty("tickers").EnumerateArray().Select(item => item.GetString()));
        }
    }

    [Fact]
    public async Task GetBatchAsync_IncompatibleBatchDoesNotDiscardLaterEvidence()
    {
        var calls = 0;
        var client = CreateClient(new StubHandler(_ => JsonResponse(++calls == 1
            ? "{not-json"
            : ValidSwingPayload(Now, "AA100"))));
        var tickers = Enumerable.Range(0, 101).Select(index => $"AA{index:D3}").ToArray();
        var results = await client.GetBatchAsync(tickers, "auto", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(101, results.Count);
        Assert.All(tickers.Take(100), ticker => Assert.Equal("incompatible", results[ticker].AvailabilityStatus));
        Assert.Equal("available", results["AA100"].AvailabilityStatus);
    }

    private static MarketPredictorHttpClient CreateClient(StubHandler handler, bool configured = true, DateTimeOffset? now = null)
    {
        var resolvedBaseUri = new Uri("http://predictor.test/");
        var options = new MarketPredictorOptions(
            configured ? resolvedBaseUri : null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(15));
        return new MarketPredictorHttpClient(
            new HttpClient(handler) { BaseAddress = resolvedBaseUri },
            options,
            new FixedTimeProvider(now ?? Now),
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
          "contract_version": "market_predictor.prediction.v4",
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
        "final_signal": "positive_setup",
        "readiness_status": "valid",
        "errors": [],
        "swing": {
          "probability": 0.61, "decision_score": 0.61, "signal": "positive_setup", "rank": 4,
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
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return responseFactory(request);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
