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

public sealed class AlpacaNewsProvider : ICatalystProvider
{
    private sealed record CachedArticleSentiment(decimal Score);
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

    public async Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var events = new List<CatalystEvent>();
        var pendingArticles = new List<ReceivedNewsArticle>();
        var startStr = windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endStr = windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string? pageToken = null;
        var pagesRead = 0;
        var reachedArticleCap = false;

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
                return events;
            }

            var receivedAt = response.Archive.Manifest.ReceivedAtUtc;
            using var doc = JsonDocument.Parse(response.Payload);
            if (!doc.RootElement.TryGetProperty("news", out var newsArray))
            {
                return events;
            }

            foreach (var item in newsArray.EnumerateArray())
            {
                if (events.Count + pendingArticles.Count >= _maxArticlesPerTicker)
                {
                    reachedArticleCap = true;
                    break;
                }

                var article = ParseArticle(ticker, item);
                if (article is null)
                {
                    continue;
                }

                var cacheKey = $"{ProviderName}:{article.Id}";
                var firstSeenAt = ArticleFirstSeenCache.AddOrUpdate(
                    cacheKey,
                    receivedAt,
                    (_, existing) => existing <= receivedAt ? existing : receivedAt);
                if (ArticleSentimentCache.TryGetValue(cacheKey, out var cachedSentiment))
                {
                    events.Add(BuildCatalystEvent(ticker, article, cachedSentiment.Score, firstSeenAt));
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

        return events;
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
            return BuildCatalystEvent(ticker, article, cachedSentiment.Score, receivedArticle.FirstSeenAt);
        }

        await throttle.WaitAsync(cancellationToken);
        try
        {
            if (ArticleSentimentCache.TryGetValue(cacheKey, out cachedSentiment))
            {
                return BuildCatalystEvent(ticker, article, cachedSentiment.Score, receivedArticle.FirstSeenAt);
            }

            var sentiment = await _sentimentAnalyzer.AnalyzeAsync(article, cancellationToken);
            ArticleSentimentCache.TryAdd(cacheKey, new CachedArticleSentiment(sentiment.Score));
            return BuildCatalystEvent(ticker, article, sentiment.Score, receivedArticle.FirstSeenAt);
        }
        finally
        {
            throttle.Release();
        }
    }


    private static CatalystEvent BuildCatalystEvent(string ticker, NewsArticle article, decimal sentimentScore, DateTimeOffset receivedAt)
    {
        return new CatalystEvent(
            ticker.ToUpperInvariant(),
            article.CreatedAt,
            CatalystType.NewsReport,
            article.Headline,
            sentimentScore,
            article.Provider,
            article.Id,
            article.Summary,
            article.Source,
            article.Url,
            receivedAt,
            article.UpdatedAt,
            CatalystAvailabilityEvidence.ProviderTimestampOnly);
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

    private NewsArticle? ParseArticle(string requestedTicker, JsonElement item)
    {
        var headline = GetString(item, "headline");
        if (String.IsNullOrWhiteSpace(headline))
        {
            return null;
        }

        var createdAt = GetDate(item, "created_at") ?? DateTimeOffset.UtcNow;
        var id = GetString(item, "id")
            ?? GetString(item, "news_id")
            ?? $"{requestedTicker}:{createdAt.UtcDateTime:O}:{headline.GetHashCode(StringComparison.Ordinal)}";

        return new NewsArticle(
            ProviderName,
            id,
            headline,
            createdAt,
            GetSymbols(item, requestedTicker),
            GetString(item, "summary"),
            GetString(item, "content"),
            GetString(item, "source"),
            GetString(item, "url"),
            GetDate(item, "updated_at"));
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

    private static DateTimeOffset? GetDate(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
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
