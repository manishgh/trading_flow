using System.Text.Json.Serialization;

namespace TradingFlow.Web.Services;

public sealed record MarketPredictorHealth(string Status, string Detail);

public sealed record MarketPredictorResult(
    string Contract,
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
    public string ErrorSummary => String.Join("; ", Errors.Select(
        TradingFlow.Contracts.Evidence.PredictorEvidenceDisplay.FormatReason));

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
            String.Empty, // No accepted response contract is available.
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
    [property: JsonPropertyName("contract_version")] string? Contract,
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
