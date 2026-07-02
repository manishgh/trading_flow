using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class FinbertHttpSentimentAnalyzer : ISentimentAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ISentimentAnalyzer _fallback;
    private readonly ILogger<FinbertHttpSentimentAnalyzer> _logger;
    private readonly TimeSpan _requestTimeout;

    public FinbertHttpSentimentAnalyzer(
        HttpClient httpClient,
        Uri endpoint,
        TimeSpan? requestTimeout = null,
        ISentimentAnalyzer? fallback = null,
        ILogger<FinbertHttpSentimentAnalyzer>? logger = null)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= endpoint;
        _fallback = fallback ?? new VaderSentimentAnalyzer();
        _logger = logger ?? NullLogger<FinbertHttpSentimentAnalyzer>.Instance;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
    }

    public string AnalyzerName => "finbert-http";

    public async Task<SentimentResult> AnalyzeAsync(NewsArticle article, CancellationToken cancellationToken)
    {
        try
        {
            var text = String.Join(". ", new[] { article.Headline, article.Summary, article.Content }
                .Where(part => !String.IsNullOrWhiteSpace(part)));

            using var timeoutCts = new CancellationTokenSource(_requestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var response = await _httpClient.PostAsJsonAsync(
                "/analyze",
                new FinbertAnalyzeRequest(text, article.Headline, article.Symbols),
                linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FinBERT sentiment request failed with status {StatusCode}; falling back to {FallbackAnalyzer}.",
                    response.StatusCode,
                    _fallback.AnalyzerName);
                return await _fallback.AnalyzeAsync(article, cancellationToken);
            }

            var result = await response.Content.ReadFromJsonAsync<FinbertAnalyzeResponse>(cancellationToken: linkedCts.Token);
            if (result is null)
            {
                return await _fallback.AnalyzeAsync(article, cancellationToken);
            }

            return new SentimentResult(
                Math.Clamp(result.Score, -1m, 1m),
                result.Label,
                AnalyzerName,
                result.Confidence);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "FinBERT sentiment request failed; falling back to {FallbackAnalyzer}.",
                _fallback.AnalyzerName);
            return await _fallback.AnalyzeAsync(article, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "FinBERT sentiment request timed out after {TimeoutSeconds:F1} seconds; falling back to {FallbackAnalyzer}.",
                _requestTimeout.TotalSeconds,
                _fallback.AnalyzerName);
            return await _fallback.AnalyzeAsync(article, cancellationToken);
        }
    }

    private sealed record FinbertAnalyzeRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("headline")] string Headline,
        [property: JsonPropertyName("symbols")] IReadOnlyList<string> Symbols);

    private sealed record FinbertAnalyzeResponse(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("score")] decimal Score,
        [property: JsonPropertyName("confidence")] decimal Confidence);
}
