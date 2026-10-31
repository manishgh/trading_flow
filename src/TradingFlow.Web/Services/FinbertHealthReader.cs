using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services;

public sealed record FinbertHealthResult(bool IsReady, string Detail);

/// <summary>Read actual model-load evidence; a configured URL or HTTP 200 alone does not prove the model is loaded.</summary>
public sealed class FinbertHealthReader(HttpClient httpClient)
{
    /// <summary>Probe health within the sentiment request budget without starting loading or inference.</summary>
    public async Task<FinbertHealthResult> ReadAsync(Uri endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(FinbertHttpSentimentAnalyzer.DefaultRequestTimeout);
        try
        {
            using var response = await httpClient.GetAsync(new Uri(endpoint, "/health"), timeout.Token);
            var payload = await response.Content.ReadFromJsonAsync<ModelHealth>(cancellationToken: timeout.Token);
            var ready = response.IsSuccessStatusCode && payload is { Status: "ready" } &&
                FinbertHttpSentimentAnalyzer.IsLoadedPinnedModel(payload.Model, payload.ModelRevision, payload.ModelLoaded) &&
                String.Equals(payload.ModelRevision, payload.ConfiguredRevision, StringComparison.OrdinalIgnoreCase);
            if (ready)
                return new(true, $"Loaded {payload!.Model}@{payload.ModelRevision}; model and tokenizer use the configured immutable revision.");
            return new(false, $"FinBERT health returned HTTP {(int)response.StatusCode}: " +
                (payload?.Detail ?? "a loaded model and matching immutable revision were not confirmed.") +
                " Sentiment requests use the named VADER fallback when FinBERT is unavailable.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(false, "FinBERT health timed out; a loaded model was not confirmed. Sentiment requests can use the named VADER fallback.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            return new(false, $"FinBERT health could not be read ({error.GetType().Name}); a loaded model was not confirmed.");
        }
    }

    private sealed record ModelHealth(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("model_loaded")] bool ModelLoaded,
        [property: JsonPropertyName("model_revision")] string? ModelRevision,
        [property: JsonPropertyName("configured_revision")] string? ConfiguredRevision,
        [property: JsonPropertyName("detail")] string? Detail);
}
