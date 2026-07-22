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
        string mode,
        string horizon,
        CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        var normalizedMode = NormalizeMode(mode);
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
        if (mode == "unified" && (prediction.Swing is null || prediction.Intraday is null))
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "invalid", "Unified prediction evidence is partial.");
        }
        if ((mode == "swing" && prediction.Swing is null) || (mode == "intraday" && prediction.Intraday is null))
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "invalid", $"{mode} prediction evidence is missing.");
        }
        if (prediction.Errors is null ||
            (prediction.Swing is not null && (prediction.Swing.Readiness is null || prediction.Swing.Catalyst is null || prediction.Swing.GlobalContext is null)) ||
            (prediction.Intraday is not null && (prediction.Intraday.Readiness is null || prediction.Intraday.Catalyst is null)))
        {
            return MarketPredictorResult.Unavailable(ticker, mode, horizon, "incompatible", "Prediction evidence is incomplete.");
        }

        var model = ResolveModel(response.Models, mode);
        var resolvedHorizon = ResolveHorizon(response, mode, horizon);
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
            prediction.Intraday,
            "available",
            null);
    }

    private static PredictorModelInfo? ResolveModel(
        IReadOnlyDictionary<string, PredictorModelInfo> models,
        string mode)
    {
        if (mode != "unified" && models.TryGetValue(mode, out var exact))
        {
            return exact;
        }
        return models.Values.FirstOrDefault();
    }

    private static string ResolveHorizon(PredictorResponse response, string mode, string requested)
    {
        if (mode != "unified" && response.ResolvedHorizons.TryGetValue(mode, out var exact))
        {
            return exact;
        }
        return response.ResolvedHorizons.Values.FirstOrDefault() ?? response.Horizon ?? requested;
    }

    internal static string NormalizeMode(string mode)
    {
        var normalized = (mode ?? String.Empty).Trim().ToLowerInvariant();
        return normalized is "swing" or "intraday" or "unified"
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(mode), "Prediction mode must be swing, intraday, or unified.");
    }

    internal static bool TryNormalizeMode(string? mode, out string normalized)
    {
        normalized = (mode ?? String.Empty).Trim().ToLowerInvariant();
        return normalized is "swing" or "intraday" or "unified";
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
    PredictorIntradayPrediction? Intraday,
    string AvailabilityStatus,
    string? AvailabilityReason)
{
    public bool IsValidPromotedEvidence =>
        AvailabilityStatus == "available" &&
        ReadinessStatus.Equals("valid", StringComparison.OrdinalIgnoreCase) &&
        String.Equals(Model?.Status, "promoted", StringComparison.OrdinalIgnoreCase);

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
    [property: JsonPropertyName("intraday")] PredictorIntradayPrediction? Intraday,
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

public sealed record PredictorIntradayPrediction(
    [property: JsonPropertyName("opportunity_probability")] decimal? OpportunityProbability,
    [property: JsonPropertyName("downside_probability")] decimal? DownsideProbability,
    [property: JsonPropertyName("decision_score")] decimal? DecisionScore,
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("rank")] int? Rank,
    [property: JsonPropertyName("relative_volume")] decimal? RelativeVolume,
    [property: JsonPropertyName("rsi_14")] decimal? Rsi14,
    [property: JsonPropertyName("macd_signal_diff")] decimal? MacdSignalDiff,
    [property: JsonPropertyName("entry_stop_pct")] decimal? EntryStopPct,
    [property: JsonPropertyName("entry_target_pct")] decimal? EntryTargetPct,
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
