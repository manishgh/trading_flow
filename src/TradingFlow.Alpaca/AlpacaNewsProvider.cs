using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Bulkhead;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

public sealed class AlpacaNewsProvider : ICatalystEvidenceProvider
{
    private sealed record CachedArticleSentiment(
        decimal Score,
        string AnalyzerName,
        DateTimeOffset ClassifiedAtUtc);
    private sealed record ReceivedNewsArticle(NewsArticle Article, DateTimeOffset FirstSeenAt);
    private const int PageLimit = 50;
    private const int MaxPages = 10;
    private const int MaxConcurrentSentimentRequests = 4;
    private const int MaxConcurrentProviderRequests = 3;
    private const int ProviderRequestQueueCapacity = 256;
    private static readonly ConcurrentDictionary<string, CachedArticleSentiment> ArticleSentimentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ArticleFirstSeenCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> ProviderRequestBulkhead =
        TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(
            MaxConcurrentProviderRequests,
            ProviderRequestQueueCapacity);

    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;
    private readonly ISentimentAnalyzer _sentimentAnalyzer;
    private readonly ILogger<AlpacaNewsProvider> _logger;
    private readonly AlpacaRawResponseArchiver _responseArchiver;
    private readonly int _maxArticlesPerTicker;

    public AlpacaNewsProvider(
        HttpClient httpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter,
        ILogger<AlpacaNewsProvider>? logger = null,
        ISentimentAnalyzer? sentimentAnalyzer = null,
        int maxArticlesPerTicker = 120)
    {
        _httpClient = httpClient;
        _options = options;
        _responseArchiver = new AlpacaRawResponseArchiver(rawArchiveWriter);
        _sentimentAnalyzer = sentimentAnalyzer ?? new VaderSentimentAnalyzer();
        _logger = logger ?? NullLogger<AlpacaNewsProvider>.Instance;
        _maxArticlesPerTicker = Math.Max(1, maxArticlesPerTicker);
        _httpClient.BaseAddress ??= AlpacaEndpointResolver.Resolve(_options.Profile).MarketDataRest;
        SetHeader("APCA-API-KEY-ID", _options.KeyId);
        SetHeader("APCA-API-SECRET-KEY", _options.SecretKey);
    }

    public string ProviderName => "alpaca";

    /// <summary>Retains the established event-list API; consumers needing coverage use the typed collection result.</summary>
    public async Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        (await GetCatalystEvidenceAsync(ticker, windowStart, windowEnd, cancellationToken)).Events;

    /// <summary>Returns actual query coverage so HTTP failures and collection caps cannot be interpreted as an absence of news.</summary>
    public async Task<CatalystQueryResult> GetCatalystEvidenceAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var events = new List<CatalystEvent>();
        var pendingArticles = new List<ReceivedNewsArticle>();
        var startStr = windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endStr = windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string? pageToken = null;
        var pagesRead = 0;
        var reachedArticleCap = false;
        string? malformedArticleReason = null;
        var malformedArticles = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = BuildNewsUrl(ticker, startStr, endStr, pageToken);
            var response = await SendNewsRequestAsync(ticker, url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Alpaca news request failed for {Ticker} with status {StatusCode}. RawArchiveId={RawArchiveId}",
                    ticker,
                    response.StatusCode,
                    response.Archive.Manifest.ArchiveId);
                return new(events, false, $"Alpaca news returned HTTP {(int)response.StatusCode}.", pagesRead, windowStart, windowEnd);
            }

            var receivedAt = response.Archive.Manifest.ReceivedAtUtc;
            using var doc = JsonDocument.Parse(response.Payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("news", out var newsArray) || newsArray.ValueKind != JsonValueKind.Array)
            {
                return new(events, false, "Alpaca news response did not contain the news array.", pagesRead, windowStart, windowEnd);
            }

            foreach (var item in newsArray.EnumerateArray())
            {
                if (events.Count + pendingArticles.Count >= _maxArticlesPerTicker)
                {
                    reachedArticleCap = true;
                    break;
                }

                var article = ParseArticle(ticker, item, out var invalidReason);
                if (article is null)
                {
                    malformedArticles++;
                    malformedArticleReason ??= invalidReason;
                    continue;
                }

                var articleIdentity = $"{ProviderName}:{article.Id}";
                var sentimentCacheKey = BuildSentimentCacheKey(article);
                var firstSeenAt = ArticleFirstSeenCache.AddOrUpdate(
                    articleIdentity,
                    receivedAt,
                    (_, existing) => existing <= receivedAt ? existing : receivedAt);
                if (ArticleSentimentCache.TryGetValue(sentimentCacheKey, out var cachedSentiment))
                {
                    events.Add(BuildCatalystEvent(ticker, article, cachedSentiment, firstSeenAt));
                    continue;
                }

                pendingArticles.Add(new ReceivedNewsArticle(article, firstSeenAt));
            }

            pageToken = doc.RootElement.TryGetProperty("next_page_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            pagesRead++;

            if (reachedArticleCap)
            {
                _logger.LogInformation(
                    "Reached Alpaca news article cap for {Ticker}. MaxArticlesPerTicker={MaxArticlesPerTicker}.",
                    ticker,
                    _maxArticlesPerTicker);
                break;
            }
        }
        while (!String.IsNullOrWhiteSpace(pageToken) && pagesRead < MaxPages);

        if (pendingArticles.Count > 0)
        {
            var analyzedEvents = await AnalyzeArticlesAsync(ticker, pendingArticles, cancellationToken);
            events.AddRange(analyzedEvents);
        }

        var complete = malformedArticles == 0 && !reachedArticleCap && String.IsNullOrWhiteSpace(pageToken);
        return new(events, complete, malformedArticles > 0 ? $"Alpaca news contained {malformedArticles} malformed article(s): {malformedArticleReason}. Coverage is incomplete." :
            reachedArticleCap ? "Alpaca news article limit was reached; coverage is incomplete." :
            !String.IsNullOrWhiteSpace(pageToken) ? "Alpaca news pagination limit was reached; coverage is incomplete." :
            "Alpaca news window was fully queried.", pagesRead, windowStart, windowEnd);
    }

    private async Task<IReadOnlyList<CatalystEvent>> AnalyzeArticlesAsync(
        string ticker,
        IReadOnlyList<ReceivedNewsArticle> articles,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentSentimentRequests);
        var tasks = articles.Select(article => AnalyzeArticleAsync(ticker, article, throttle, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<CatalystEvent> AnalyzeArticleAsync(
        string ticker,
        ReceivedNewsArticle receivedArticle,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        var article = receivedArticle.Article;
        var cacheKey = BuildSentimentCacheKey(article);
        if (ArticleSentimentCache.TryGetValue(cacheKey, out var cachedSentiment))
        {
            return BuildCatalystEvent(ticker, article, cachedSentiment, receivedArticle.FirstSeenAt);
        }

        await throttle.WaitAsync(cancellationToken);
        try
        {
            if (ArticleSentimentCache.TryGetValue(cacheKey, out cachedSentiment))
            {
                return BuildCatalystEvent(ticker, article, cachedSentiment, receivedArticle.FirstSeenAt);
            }

            var sentiment = await _sentimentAnalyzer.AnalyzeAsync(article, cancellationToken);
            var assessed = new CachedArticleSentiment(
                sentiment.Score,
                sentiment.AnalyzerName,
                DateTimeOffset.UtcNow);
            ArticleSentimentCache.TryAdd(cacheKey, assessed);
            return BuildCatalystEvent(ticker, article, assessed, receivedArticle.FirstSeenAt);
        }
        finally
        {
            throttle.Release();
        }
    }


    private static CatalystEvent BuildCatalystEvent(
        string ticker,
        NewsArticle article,
        CachedArticleSentiment sentiment,
        DateTimeOffset receivedAt)
    {
        var decisionAvailableAt = receivedAt >= sentiment.ClassifiedAtUtc
            ? receivedAt
            : sentiment.ClassifiedAtUtc;
        return new CatalystEvent(
            ticker.ToUpperInvariant(),
            article.CreatedAt,
            CatalystType.NewsReport,
            article.Headline,
            sentiment.Score,
            article.Provider,
            article.Id,
            article.Summary,
            article.Source,
            article.Url,
            receivedAt,
            article.UpdatedAt,
            CatalystAvailabilityEvidence.ObservedReceiptTime,
            decisionAvailableAt,
            CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime,
            sentiment.AnalyzerName);
    }

    private string BuildSentimentCacheKey(NewsArticle article) =>
        $"{ProviderName}:{article.Id}:{article.UpdatedAt?.UtcTicks ?? article.CreatedAt.UtcTicks}";

    private void SetHeader(string name, string value)
    {
        if (_httpClient.DefaultRequestHeaders.Contains(name))
        {
            _httpClient.DefaultRequestHeaders.Remove(name);
        }

        if (!String.IsNullOrWhiteSpace(value))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    private static string BuildNewsUrl(string ticker, string startStr, string endStr, string? pageToken)
    {
        var symbol = Uri.EscapeDataString(AlpacaSymbolMapper.ToProviderSymbol(ticker));
        var url = $"/v1beta1/news?symbols={symbol}&start={Uri.EscapeDataString(startStr)}&end={Uri.EscapeDataString(endStr)}&limit={PageLimit}&include_content=false&exclude_contentless=false";
        return String.IsNullOrWhiteSpace(pageToken)
            ? url
            : $"{url}&page_token={Uri.EscapeDataString(pageToken)}";
    }

    private async Task<ArchivedAlpacaResponse> SendNewsRequestAsync(string ticker, string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync(
                    "Alpaca",
                    url,
                    "GET",
                    () => ProviderRequestBulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
                var archivedResponse = await _responseArchiver.ArchiveAsync(
                    response,
                    "news-rest-page",
                    correlationId: ticker.ToUpperInvariant(),
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["ticker"] = ticker.ToUpperInvariant()
                    },
                    cancellationToken: cancellationToken);

                if ((Int32)archivedResponse.StatusCode != 429 && (Int32)archivedResponse.StatusCode < 500)
                {
                    return archivedResponse;
                }

                if (attempt == maxAttempts)
                {
                    return archivedResponse;
                }

                var delay = ResolveRetryDelay(archivedResponse, attempt);
                _logger.LogInformation(
                    "Retrying Alpaca news request for {Ticker} after {DelayMs} ms because status was {StatusCode}. Attempt {Attempt}/{MaxAttempts}.",
                    ticker,
                    delay.TotalMilliseconds,
                    archivedResponse.StatusCode,
                    attempt,
                    maxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
            catch (BulkheadRejectedException) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(250 * attempt);
                _logger.LogWarning(
                    "Alpaca news bulkhead rejected request for {Ticker}; retrying after {DelayMs} ms. Attempt {Attempt}/{MaxAttempts}.",
                    ticker,
                    delay.TotalMilliseconds,
                    attempt,
                    maxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Alpaca news request retry loop exited unexpectedly.");
    }

    private static TimeSpan ResolveRetryDelay(ArchivedAlpacaResponse response, int attempt)
    {
        var retryAfter = response.GetHeaderValues("Retry-After").FirstOrDefault();
        if (Int32.TryParse(retryAfter, out var retryAfterSeconds) && retryAfterSeconds > 0)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        if (DateTimeOffset.TryParse(retryAfter, out var retryAfterAt))
        {
            var retryAfterDelay = retryAfterAt - DateTimeOffset.UtcNow;
            if (retryAfterDelay > TimeSpan.Zero)
            {
                return retryAfterDelay;
            }
        }

        if (Int64.TryParse(response.GetHeaderValues("X-RateLimit-Reset").FirstOrDefault(), out var resetUnixSeconds))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
            var delay = reset - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(30))
            {
                return delay;
            }
        }

        return TimeSpan.FromMilliseconds(Math.Min(5000, 300 * Math.Pow(2, attempt - 1)));
    }

    /// <summary>Accepts only provider-identified, timestamped headlines; malformed rows make strict coverage incomplete without fabricating events.</summary>
    private NewsArticle? ParseArticle(string requestedTicker, JsonElement item, out string? invalidReason)
    {
        invalidReason = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            invalidReason = "article is not an object";
            return null;
        }
        var headline = item.TryGetProperty("headline", out var headlineValue) && headlineValue.ValueKind == JsonValueKind.String
            ? headlineValue.GetString() : null;
        if (String.IsNullOrWhiteSpace(headline))
        {
            invalidReason = "missing headline";
            return null;
        }
        var id = GetString(item, "id") ?? GetString(item, "news_id");
        if (String.IsNullOrWhiteSpace(id))
        {
            invalidReason = "missing provider article ID";
            return null;
        }
        var createdAt = GetDate(item, "created_at");
        if (createdAt is null)
        {
            invalidReason = "missing or invalid created_at publication timestamp";
            return null;
        }
        var updatedAt = GetDate(item, "updated_at");
        if (item.TryGetProperty("updated_at", out var update) && update.ValueKind != JsonValueKind.Null && updatedAt is null)
        {
            invalidReason = "invalid updated_at revision timestamp";
            return null;
        }

        return new NewsArticle(
            ProviderName,
            id,
            headline,
            createdAt.Value,
            GetSymbols(item, requestedTicker),
            GetString(item, "summary"),
            GetString(item, "content"),
            GetString(item, "source"),
            GetString(item, "url"),
            updatedAt);
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    /// <summary>Requires an ISO provider timestamp with an explicit UTC/offset designator; local-clock defaults are never evidence.</summary>
    private static DateTimeOffset? GetDate(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        var timeSeparator = text?.IndexOf('T') ?? -1;
        if (timeSeparator < 0 || !(text!.EndsWith('Z') || text.IndexOf('+', timeSeparator) >= 0 || text.IndexOf('-', timeSeparator) >= 0)) return null;
        return value.TryGetDateTimeOffset(out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static IReadOnlyList<string> GetSymbols(JsonElement item, string requestedTicker)
    {
        if (!item.TryGetProperty("symbols", out var symbolsElement) || symbolsElement.ValueKind != JsonValueKind.Array)
        {
            return new[] { requestedTicker.ToUpperInvariant() };
        }

        var symbols = new List<string>();
        foreach (var symbol in symbolsElement.EnumerateArray())
        {
            if (symbol.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(symbol.GetString()))
            {
                symbols.Add(symbol.GetString()!.ToUpperInvariant());
            }
        }

        return symbols.Count == 0 ? new[] { requestedTicker.ToUpperInvariant() } : symbols;
    }
}
