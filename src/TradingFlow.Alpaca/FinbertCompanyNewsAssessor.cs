using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.News;

namespace TradingFlow.Alpaca;

/// <summary>Evidence inference requires the exact pinned FinBERT identity; failures never invoke VADER.</summary>
public sealed class FinbertCompanyNewsAssessor(HttpClient client, Uri endpoint, CompanyNewsPinnedModel pinned,
    TimeProvider clock)
{
    /// <summary>Scores only the issuer-bound evidence text, retaining its exact hash and actual completion time.</summary>
    public async Task<CompanyNewsModelEvidence> AssessAsync(CompanyNewsExtraction extraction, CancellationToken cancellationToken = default)
    {
        if (!extraction.CanAssess || String.IsNullOrWhiteSpace(extraction.ModelInputText) || extraction.ModelInputText.Length > 8000)
            throw new InvalidDataException("Issuer evidence is absent or exceeds the sidecar's exact-text limit; truncation is not permitted.");
        if (pinned.Model != "ProsusAI/finbert" || pinned.Version.Length != 40 || !pinned.Version.All(Uri.IsHexDigit))
            throw new InvalidDataException("Company evidence requires an immutable ProsusAI/finbert revision.");
        using var response = await client.PostAsJsonAsync(new Uri(endpoint, "/analyze"),
            new { text = extraction.ModelInputText, headline = extraction.Revision.Headline, symbols = new[] { extraction.IssuerSymbol },
                require_full_text = true }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<Response>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("FinBERT returned no inference.");
        if (!value.Loaded || value.Model != pinned.Model || value.Revision != pinned.Version || value.Confidence is < 0 or > 1 ||
            !value.FullTextProcessed || value.InputSha256 != extraction.ModelInputSha256 ||
            value.ArtifactSha256 != pinned.ArtifactSha256 ||
            value.InputTokenCount is not > 0 || value.ModelMaxTokens is not > 0 || value.InputTokenCount > value.ModelMaxTokens ||
            value.Label is not ("positive" or "negative" or "neutral") ||
            value.Score != (value.Label == "positive" ? value.Confidence : value.Label == "negative" ? -value.Confidence : 0m))
            throw new InvalidDataException("FinBERT model, complete-input proof or three-label probability contract differs from the frozen policy.");
        var label = value.Label switch { "positive" => CompanyNewsPolarity.Positive, "negative" => CompanyNewsPolarity.Negative, _ => CompanyNewsPolarity.Neutral };
        return new(pinned, extraction.ModelInputSha256, label, value.Confidence, clock.GetUtcNow());
    }

    private sealed record Response([property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("score")] decimal Score, [property: JsonPropertyName("confidence")] decimal Confidence,
        [property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("model_revision")] string Revision,
        [property: JsonPropertyName("model_loaded")] bool Loaded,
        [property: JsonPropertyName("input_sha256")] string? InputSha256,
        [property: JsonPropertyName("full_text_processed")] bool FullTextProcessed,
        [property: JsonPropertyName("input_token_count")] int? InputTokenCount,
        [property: JsonPropertyName("model_max_tokens")] int? ModelMaxTokens,
        [property: JsonPropertyName("model_artifact_sha256")] string? ArtifactSha256);
}
