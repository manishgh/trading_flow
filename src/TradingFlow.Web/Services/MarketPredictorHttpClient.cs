using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingFlow.Web.Services;

public sealed record MarketPredictorOptions(
    Uri? BaseUri,
    TimeSpan RequestTimeout,
    TimeSpan MaximumEvidenceAge)
{
    public bool IsConfigured => BaseUri is not null;
}

/// <summary>
/// Reads prediction evidence from Market Predictor. The adapter never converts
/// model output into a trade signal and never participates in order submission.
/// </summary>
public sealed class MarketPredictorHttpClient
{
    /// <summary>
    /// The service caps a request at 100 symbols (PredictionRequest.tickers).
    /// Batching to that cap keeps a full universe to as few round trips as the
    /// contract allows; exceeding it is a 422, not a truncation.
    /// </summary>
    internal const int MaximumTickersPerRequest = 100;

    private readonly HttpClient httpClient;
    private readonly MarketPredictorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MarketPredictorHttpClient> logger;

    public MarketPredictorHttpClient(
        HttpClient httpClient,
        MarketPredictorOptions options,
        TimeProvider timeProvider,
        ILogger<MarketPredictorHttpClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<MarketPredictorResult> GetAsync(
        string ticker,
        string horizon,
        CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        const string normalizedMode = "swing";
        var normalizedHorizon = String.IsNullOrWhiteSpace(horizon) ? "auto" : horizon.Trim().ToLowerInvariant();
        if (!options.IsConfigured)
        {
            return MarketPredictorResult.Unavailable(
                normalizedTicker,
                normalizedMode,
                normalizedHorizon,
                "not_configured",
                "Market Predictor endpoint is not configured.");
        }

        var requestTime = timeProvider.GetUtcNow();
        var request = new PredictorRequest([normalizedTicker], normalizedMode, normalizedHorizon, requestTime);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await httpClient.PostAsJsonAsync(
                $"v1/predictions/{normalizedMode}",
                request,
                PredictorJsonContext.Default.PredictorRequest,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Market Predictor request for {Ticker} returned HTTP {StatusCode}.",
                    normalizedTicker,
                    (int)response.StatusCode);
                return MarketPredictorResult.Unavailable(
                    normalizedTicker,
                    normalizedMode,
                    normalizedHorizon,
                    "unavailable",
                    $"Market Predictor returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync(
                PredictorJsonContext.Default.PredictorResponse,
                timeout.Token);
            return Validate(payload, normalizedTicker, normalizedMode, normalizedHorizon, requestTime);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Market Predictor request for {Ticker} timed out.", normalizedTicker);
            return MarketPredictorResult.Unavailable(
                normalizedTicker,
                normalizedMode,
                normalizedHorizon,
                "unavailable",
                "Market Predictor request timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Market Predictor request for {Ticker} failed.", normalizedTicker);
            return MarketPredictorResult.Unavailable(
                normalizedTicker,
                normalizedMode,
                normalizedHorizon,
                "unavailable",
                "Market Predictor is unavailable.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            logger.LogWarning(exception, "Market Predictor response for {Ticker} was incompatible.", normalizedTicker);
            return MarketPredictorResult.Unavailable(
                normalizedTicker,
                normalizedMode,
                normalizedHorizon,
                "incompatible",
                "Market Predictor response did not match the supported contract.");
        }
    }

    /// <summary>
    /// Scores a whole candidate universe in as few calls as the service allows.
    ///
    /// The desk ranks a universe, not a symbol, so asking per ticker would turn
    /// one screen load into N round trips against a service that already accepts
    /// a batch. Every ticker in <paramref name="tickers"/> comes back in the
    /// result: one that the service did not answer for is present as an
    /// unavailable result rather than absent, so a caller cannot mistake a
    /// missing answer for a negative one.
    /// </summary>
    /// <param name="tickers">Candidate symbols. Order is not significant.</param>
    /// <param name="horizon">Requested horizon, or auto.</param>
    public async Task<IReadOnlyDictionary<string, MarketPredictorResult>> GetBatchAsync(
        IReadOnlyCollection<string> tickers,
        string horizon,
        CancellationToken cancellationToken)
    {
        const string normalizedMode = "swing";
        var normalizedHorizon = String.IsNullOrWhiteSpace(horizon) ? "auto" : horizon.Trim().ToLowerInvariant();
        var results = new Dictionary<string, MarketPredictorResult>(StringComparer.OrdinalIgnoreCase);

        // A symbol the adapter cannot even normalise never reaches the service.
        // It is recorded as invalid here so the caller still sees a row for it.
        var normalized = new List<string>(tickers.Count);
        foreach (var ticker in tickers)
        {
            string candidate;
            try
            {
                candidate = NormalizeTicker(ticker);
            }
            catch (ArgumentException)
            {
                var raw = (ticker ?? String.Empty).Trim().ToUpperInvariant();
                results[raw] = MarketPredictorResult.Unavailable(
                    raw, normalizedMode, normalizedHorizon, "invalid", "Ticker is not a canonical US symbol.");
                continue;
            }

            if (!results.ContainsKey(candidate) && !normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(candidate);
            }
        }

        if (normalized.Count == 0)
        {
            return results;
        }

        if (!options.IsConfigured)
        {
            foreach (var ticker in normalized)
            {
                results[ticker] = MarketPredictorResult.Unavailable(
                    ticker, normalizedMode, normalizedHorizon, "not_configured",
                    "Market Predictor endpoint is not configured.");
            }
            return results;
        }

        foreach (var batch in normalized.Chunk(MaximumTickersPerRequest))
        {
            var requestTime = timeProvider.GetUtcNow();
            var response = await PostBatchAsync(batch, normalizedMode, normalizedHorizon, requestTime, cancellationToken);
            foreach (var ticker in batch)
            {
                results[ticker] = response is null
                    ? MarketPredictorResult.Unavailable(
                        ticker, normalizedMode, normalizedHorizon, "unavailable", "Market Predictor is unavailable.")
                    : Validate(response, ticker, normalizedMode, normalizedHorizon, requestTime);
            }
        }

        return results;
    }

    /// <summary>
    /// Sends one batch and returns the payload, or null when the call could not
    /// be completed. Failure is not thrown: one unreachable batch must degrade
    /// that batch to unavailable evidence, not fail the whole screen.
    /// </summary>
    private async Task<PredictorResponse?> PostBatchAsync(
        IReadOnlyList<string> tickers,
        string mode,
        string horizon,
        DateTimeOffset requestTime,
        CancellationToken cancellationToken)
    {
        var request = new PredictorRequest(tickers, mode, horizon, requestTime);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await httpClient.PostAsJsonAsync(
                $"v1/predictions/{mode}",
                request,
                PredictorJsonContext.Default.PredictorRequest,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Market Predictor batch of {Count} symbols returned HTTP {StatusCode}.",
                    tickers.Count,
                    (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync(
                PredictorJsonContext.Default.PredictorResponse,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Market Predictor batch of {Count} symbols timed out.", tickers.Count);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning(exception, "Market Predictor batch of {Count} symbols failed.", tickers.Count);
            return null;
        }
    }

    public async Task<MarketPredictorHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return new MarketPredictorHealth("not configured", "Market Predictor endpoint is not configured.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await httpClient.GetAsync("v1/health/ready", timeout.Token);
            var payload = await response.Content.ReadFromJsonAsync(
                PredictorJsonContext.Default.PredictorHealthResponse,
                timeout.Token);
            var status = payload?.Status?.Trim().ToLowerInvariant();
            if (response.IsSuccessStatusCode && status == "ready")
            {
                return new MarketPredictorHealth("ready", payload?.Reason ?? "Prediction service reports ready.");
            }

            return new MarketPredictorHealth(
                "attention",
                payload?.Reason ?? $"Prediction readiness returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MarketPredictorHealth("attention", "Prediction readiness check timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning(exception, "Market Predictor readiness check failed.");
            return new MarketPredictorHealth("attention", "Prediction readiness is unavailable.");
        }
    }

    private MarketPredictorResult Validate(
        PredictorResponse? response,
        string ticker,
        string mode,
        string horizon,
        DateTimeOffset requestTime)
    {
        if (response is null || response.GeneratedAtUtc == default || String.IsNullOrWhiteSpace(response.RequestId) ||
            String.IsNullOrWhiteSpace(response.Mode) ||
            response.Predictions is null || response.Errors is null || response.Models is null || response.ResolvedHorizons is null)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "incompatible", "Prediction evidence is incomplete.");
        }
        if (!response.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase))
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "incompatible", "Prediction mode does not match the request.");
        }
        if (response.GeneratedAtUtc > requestTime.AddMinutes(1))
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "invalid", "Prediction timestamp is in the future.");
        }
        if (requestTime - response.GeneratedAtUtc > options.MaximumEvidenceAge)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "stale", "Prediction evidence is stale.");
        }

        var prediction = response.Predictions.SingleOrDefault(item =>
            String.Equals(item.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        if (prediction is null)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "invalid", "No prediction was returned for the selected symbol.");
        }
        if (prediction.Swing is null)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "invalid", "Swing prediction evidence is missing.");
        }
        if (prediction.Errors is null ||
            prediction.Swing.Readiness is null ||
            prediction.Swing.Catalyst is null ||
            prediction.Swing.GlobalContext is null)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "incompatible", "Prediction evidence is incomplete.");
        }

        if (!response.ResolvedHorizons.TryGetValue("swing", out var resolvedHorizon) ||
            !String.Equals(resolvedHorizon, "10b", StringComparison.Ordinal) ||
            !response.Models.TryGetValue("swing", out var model) || model is null)
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "incompatible", "Ten-session swing model evidence is required.");
        }
        return new MarketPredictorResult(
            "market_predictor.prediction.v1",
            ticker,
            mode,
            horizon,
            resolvedHorizon,
            response.RequestId,
            response.SnapshotId,
            response.GeneratedAtUtc,
            prediction.FinalSignal,
            prediction.ReadinessStatus,
            prediction.Errors.Concat(response.Errors).ToArray(),
            model,
            prediction.Swing,
            "available",
            null);
    }

    private static string NormalizeTicker(string ticker)
    {
        var normalized = (ticker ?? String.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 16 || normalized.Any(character => !Char.IsLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new ArgumentException("Ticker is invalid.", nameof(ticker));
        }
        return normalized;
    }
}

public sealed record MarketPredictorHealth(string Status, string Detail);

public sealed record MarketPredictorResult(
    string ContractVersion,
    string Ticker,
    string Mode,
    string RequestedHorizon,
    string ResolvedHorizon,
    string? RequestId,
    string? SnapshotId,
    DateTimeOffset? GeneratedAtUtc,
    string FinalSignal,
    string ReadinessStatus,
    IReadOnlyList<string> Errors,
    PredictorModelInfo? Model,
    PredictorSwingPrediction? Swing,
    string AvailabilityStatus,
    string? AvailabilityReason)
{
    public bool IsValidPromotedEvidence =>
        AvailabilityStatus == "available" &&
        ReadinessStatus.Equals("valid", StringComparison.OrdinalIgnoreCase) &&
        String.Equals(Model?.Status, "promoted", StringComparison.OrdinalIgnoreCase);

    public bool IsValidPaperEvidence =>
        AvailabilityStatus == "available" &&
        ReadinessStatus.Equals("valid", StringComparison.OrdinalIgnoreCase) &&
        (String.Equals(Model?.Status, "promoted", StringComparison.OrdinalIgnoreCase) ||
         String.Equals(Model?.Status, "candidate", StringComparison.OrdinalIgnoreCase));

    public static MarketPredictorResult Unavailable(
        string ticker,
        string mode,
        string horizon,
        string status,
        string reason) => new(
            "market_predictor.prediction.v1",
            ticker,
            mode,
            horizon,
            horizon,
            null,
            null,
            null,
            "not_ready",
            "invalid",
            [reason],
            null,
            null,
            status,
            reason);
}

internal sealed record PredictorRequest(
    [property: JsonPropertyName("tickers")] IReadOnlyList<string> Tickers,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("horizon")] string Horizon,
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf);

internal sealed record PredictorResponse(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("horizon")] string? Horizon,
    [property: JsonPropertyName("resolved_horizons")] IReadOnlyDictionary<string, string> ResolvedHorizons,
    [property: JsonPropertyName("models")] IReadOnlyDictionary<string, PredictorModelInfo> Models,
    [property: JsonPropertyName("predictions")] IReadOnlyList<PredictorTickerPrediction> Predictions,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId);

internal sealed record PredictorHealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record PredictorModelInfo(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("model_type")] string? ModelType,
    [property: JsonPropertyName("schema_version")] string? SchemaVersion,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("artifact_sha256")] string? ArtifactSha256,
    [property: JsonPropertyName("training_data_end")] string? TrainingDataEnd);

internal sealed record PredictorTickerPrediction(
    [property: JsonPropertyName("ticker")] string Ticker,
    [property: JsonPropertyName("final_signal")] string FinalSignal,
    [property: JsonPropertyName("readiness_status")] string ReadinessStatus,
    [property: JsonPropertyName("swing")] PredictorSwingPrediction? Swing,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);

public sealed record PredictorSwingPrediction(
    [property: JsonPropertyName("probability")] decimal? Probability,
    [property: JsonPropertyName("decision_score")] decimal? DecisionScore,
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("rank")] int? Rank,
    [property: JsonPropertyName("return_1d")] decimal? Return1D,
    [property: JsonPropertyName("volume_z20")] decimal? VolumeZ20,
    [property: JsonPropertyName("global_context")] PredictorGlobalContext GlobalContext,
    [property: JsonPropertyName("catalyst")] PredictorCatalyst Catalyst,
    [property: JsonPropertyName("readiness")] PredictorReadiness Readiness);

public sealed record PredictorReadiness(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("latest_price_date")] string? LatestPriceDate,
    [property: JsonPropertyName("price_feed")] string PriceFeed,
    [property: JsonPropertyName("benchmark_status")] string BenchmarkStatus,
    [property: JsonPropertyName("market_context_status")] string MarketContextStatus,
    [property: JsonPropertyName("model_status")] string ModelStatus,
    [property: JsonPropertyName("source_status")] string SourceStatus);

public sealed record PredictorCatalyst(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("score")] decimal Score,
    [property: JsonPropertyName("event_count")] int EventCount,
    [property: JsonPropertyName("relevance")] decimal Relevance,
    [property: JsonPropertyName("minutes_since_latest")] decimal? MinutesSinceLatest,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

public sealed record PredictorGlobalContext(
    [property: JsonPropertyName("net_impact")] decimal NetImpact,
    [property: JsonPropertyName("active_flashpoints")] IReadOnlyList<string> ActiveFlashpoints);

[JsonSerializable(typeof(PredictorRequest))]
[JsonSerializable(typeof(PredictorResponse))]
[JsonSerializable(typeof(PredictorHealthResponse))]
internal sealed partial class PredictorJsonContext : JsonSerializerContext;
