using Microsoft.Extensions.Hosting;
using TradingFlow.Alpaca;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Maintains a small rolling news cache for mobile and operator views. Strategy
/// execution still uses the configured catalyst provider directly, while this
/// service gives us a continuous, low-latency feed to inspect.
/// </summary>
public sealed class NewsFeedService : BackgroundService
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;
    private readonly SqliteNewsFeedRepository repository;
    private readonly ConfigCatalogService catalog;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly ArticleTextFetcher articleTextFetcher;
    private readonly ILogger<NewsFeedService> logger;

    public NewsFeedService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        SqliteNewsFeedRepository repository,
        ConfigCatalogService catalog,
        AlpacaCredentialProvider alpacaCredentials,
        ArticleTextFetcher articleTextFetcher,
        ILogger<NewsFeedService> logger)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
        this.repository = repository;
        this.catalog = catalog;
        this.alpacaCredentials = alpacaCredentials;
        this.articleTextFetcher = articleTextFetcher;
        this.logger = logger;
    }

    public async Task<MobileNewsFeedResponse> GetRollingAsync(
        int hours,
        string? ticker,
        CancellationToken cancellationToken)
    {
        var clampedHours = Math.Clamp(hours, 1, (int)RetentionWindow.TotalHours);
        var windowStart = DateTimeOffset.UtcNow.AddHours(-clampedHours);
        var items = await repository.GetRecentAsync(windowStart, 200, ticker, cancellationToken);
        return new MobileNewsFeedResponse(
            true,
            "finviz+alpaca",
            $"Rolling {clampedHours}h feed. Items older than {RetentionWindow.TotalHours:F0}h are pruned.",
            items
                .GroupBy(ArticleDedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(ToAggregatedMobileNewsItem)
                .OrderByDescending(item => item.Timestamp)
                .ToArray());
    }

    public async Task<MobileNewsFeedResponse> GetLatestAsync(
        string configPath,
        IReadOnlyList<string> tickers,
        int hours,
        CancellationToken cancellationToken)
    {
        var resolvedConfigPath = paths.ResolveRepositoryPath(configPath);
        var run = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(resolvedConfigPath));
        if (!run.News.Enabled)
        {
            return new MobileNewsFeedResponse(false, run.News.ProviderName, "News is disabled in this run config.", Array.Empty<MobileNewsItem>());
        }

        var provider = runtimeFactory.CreateNewsProvider(run);
        if (provider is null)
        {
            return new MobileNewsFeedResponse(false, run.News.ProviderName, "No news provider is configured.", Array.Empty<MobileNewsItem>());
        }

        var symbols = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (symbols.Length == 0)
        {
            symbols = run.Tickers.Take(20).ToArray();
        }

        var windowEnd = DateTimeOffset.UtcNow;
        var windowStart = windowEnd.AddHours(-Math.Clamp(hours, 1, 168));
        var tasks = symbols.Select(async ticker =>
        {
            var catalysts = await provider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
            return catalysts.Select(ToMobileNewsItem);
        });

        var items = (await Task.WhenAll(tasks))
            .SelectMany(item => item)
            .GroupBy(MobileArticleDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(ToAggregatedMobileNewsItem)
            .OrderByDescending(item => item.Timestamp)
            .Take(80)
            .ToArray();

        return new MobileNewsFeedResponse(true, provider.ProviderName, null, items);
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var windowStart = DateTimeOffset.UtcNow.Subtract(RetentionWindow);
        var events = new List<CatalystEvent>();
        events.AddRange(await LoadFinvizNewsAsync(cancellationToken));
        events.AddRange(await LoadAlpacaNewsAsync(cancellationToken));
        events = events
            .Where(item => item.Timestamp >= windowStart)
            .OrderByDescending(item => item.Timestamp)
            .Take(120)
            .ToList();

        var enrichedEvents = await EnrichNewsAsync(events, cancellationToken);
        await repository.UpsertAsync(enrichedEvents, cancellationToken);
        await repository.PruneOlderThanAsync(windowStart, cancellationToken);

        logger.LogInformation(
            "Rolling news feed refresh stored {EventCount} event(s). Providers={Providers}",
            enrichedEvents.Count,
            String.Join(",", enrichedEvents.Select(x => x.Provider ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DelayStartupAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Rolling news feed refresh failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private static async Task DelayStartupAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
    }

    private async Task<IReadOnlyList<CatalystEvent>> LoadFinvizNewsAsync(CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("FINVIZ_API_KEY");
        if (String.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User);
        }

        if (String.IsNullOrWhiteSpace(token))
        {
            logger.LogDebug("Skipping Finviz rolling news refresh because FINVIZ_API_KEY is not configured.");
            return Array.Empty<CatalystEvent>();
        }

        using var client = new FinvizClient(
            new HttpClient(),
            FinvizOptions.CreateDefault() with { AuthToken = token },
            runtimeFactory.RawArchiveWriter);

        var marketNews = await client.GetNewsExportAsync(1, cancellationToken);
        var stockNews = await client.GetNewsExportAsync(3, cancellationToken);
        return marketNews.Concat(stockNews)
            .GroupBy(NewsDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalystEvent>> LoadAlpacaNewsAsync(CancellationToken cancellationToken)
    {
        if (!alpacaCredentials.IsConfigured)
        {
            logger.LogDebug("Skipping Alpaca rolling news refresh because Alpaca credentials are not configured.");
            return Array.Empty<CatalystEvent>();
        }

        var tickers = catalog.GetPaperConfigs()
            .SelectMany(config => config.Config.Tickers)
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
        if (tickers.Length == 0)
        {
            return Array.Empty<CatalystEvent>();
        }

        var provider = new AlpacaNewsProvider(
            new HttpClient(),
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = alpacaCredentials.KeyId,
                SecretKey = alpacaCredentials.SecretKey
            },
            logger: null,
            sentimentAnalyzer: new VaderSentimentAnalyzer(),
            maxArticlesPerTicker: 25);

        var windowEnd = DateTimeOffset.UtcNow;
        var windowStart = windowEnd.Subtract(RetentionWindow);
        var tasks = tickers.Select(ticker => provider.GetCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken));
        return (await Task.WhenAll(tasks))
            .SelectMany(x => x)
            .GroupBy(NewsDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalystEvent>> EnrichNewsAsync(
        IReadOnlyList<CatalystEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return events;
        }

        using var analyzer = CreateSentimentAnalyzer();
        using var throttle = new SemaphoreSlim(4);
        var tasks = events.Select(item => EnrichSingleNewsAsync(item, analyzer.Value, throttle, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<CatalystEvent> EnrichSingleNewsAsync(
        CatalystEvent item,
        ISentimentAnalyzer sentimentAnalyzer,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            var linkedText = await articleTextFetcher.FetchTextAsync(item.Url, cancellationToken);
            var preview = BuildPreview(item.Summary, linkedText);
            var article = new NewsArticle(
                item.Provider ?? "unknown",
                item.ExternalId ?? $"{item.Ticker}:{item.Timestamp.UtcDateTime:O}:{item.Headline}",
                item.Headline,
                item.Timestamp,
                new[] { item.Ticker },
                item.Summary,
                linkedText,
                item.Source,
                item.Url);
            var sentiment = await sentimentAnalyzer.AnalyzeAsync(article, cancellationToken);

            return item with
            {
                SentimentScore = sentiment.Score,
                Summary = preview
            };
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                exception,
                "News enrichment timed out for {Provider} {Ticker} {Url}.",
                item.Provider,
                item.Ticker,
                item.Url);
            return item;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(
                exception,
                "News enrichment failed for {Provider} {Ticker} {Url}.",
                item.Provider,
                item.Ticker,
                item.Url);
            return item;
        }
        finally
        {
            throttle.Release();
        }
    }

    private static string? BuildPreview(string? summary, string? linkedText)
    {
        var parts = new List<string>();
        if (!String.IsNullOrWhiteSpace(summary))
        {
            parts.Add(summary.Trim());
        }

        if (!String.IsNullOrWhiteSpace(linkedText))
        {
            var cleaned = linkedText.Trim();
            parts.Add(cleaned.Length <= 700 ? cleaned : $"{cleaned[..700]}...");
        }

        return parts.Count == 0 ? null : String.Join(" - ", parts);
    }

    private DisposableSentimentAnalyzer CreateSentimentAnalyzer()
    {
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL")
            ?? Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL", EnvironmentVariableTarget.User);
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            var httpClient = new HttpClient();
            return new DisposableSentimentAnalyzer(
                new FinbertHttpSentimentAnalyzer(httpClient, uri, fallback: new VaderSentimentAnalyzer()),
                httpClient);
        }

        return new DisposableSentimentAnalyzer(new VaderSentimentAnalyzer(), null);
    }

    private sealed class DisposableSentimentAnalyzer : IDisposable
    {
        private readonly IDisposable? disposable;

        public DisposableSentimentAnalyzer(ISentimentAnalyzer value, IDisposable? disposable)
        {
            Value = value;
            this.disposable = disposable;
        }

        public ISentimentAnalyzer Value { get; }

        public void Dispose()
        {
            disposable?.Dispose();
        }
    }

    private static MobileNewsItem ToMobileNewsItem(CatalystEvent item)
    {
        var headline = NormalizeHeadline(item.Headline, item.Summary, item.Source, item.Provider);
        var summary = NormalizeSummary(item.Summary, headline);
        return new MobileNewsItem(
            item.Ticker,
            item.Timestamp,
            headline,
            item.SentimentScore,
            item.Provider,
            item.Source,
            item.Url,
            summary);
    }

    private static MobileNewsItem ToMobileNewsItem(PersistedNewsItem item)
    {
        var headline = NormalizeHeadline(item.Headline, item.Summary, item.Source, item.Provider);
        var summary = NormalizeSummary(item.Summary, headline);
        return new MobileNewsItem(
            item.Ticker,
            item.Timestamp,
            headline,
            item.SentimentScore,
            item.Provider,
            item.Source,
            item.Url,
            summary);
    }

    private static MobileNewsItem ToAggregatedMobileNewsItem(IGrouping<string, PersistedNewsItem> group)
    {
        var items = group
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
        var first = ToMobileNewsItem(items[0]);
        return first with { Ticker = FormatRelatedTickers(items.Select(item => item.Ticker)) };
    }

    private static MobileNewsItem ToAggregatedMobileNewsItem(IGrouping<string, MobileNewsItem> group)
    {
        var items = group
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
        return items[0] with { Ticker = FormatRelatedTickers(items.Select(item => item.Ticker)) };
    }

    private static string NewsDedupeKey(CatalystEvent item)
    {
        var identity = !String.IsNullOrWhiteSpace(item.Url) ? item.Url : item.Headline;
        return $"{item.Provider}|{item.Ticker}|{item.Timestamp.UtcDateTime:yyyyMMddHHmm}|{identity}";
    }

    private static string NewsDedupeKey(PersistedNewsItem item)
    {
        var identity = !String.IsNullOrWhiteSpace(item.Url) ? item.Url : item.Headline;
        return $"{item.Provider}|{item.Ticker}|{item.Timestamp.UtcDateTime:yyyyMMddHHmm}|{identity}";
    }

    private static string ArticleDedupeKey(PersistedNewsItem item)
    {
        return $"{item.Provider}|{NormalizeArticleIdentity(item.Url, item.Headline)}";
    }

    private static string MobileArticleDedupeKey(MobileNewsItem item)
    {
        return $"{item.Provider}|{NormalizeArticleIdentity(item.Url, item.Headline)}";
    }

    private static string NormalizeArticleIdentity(string? url, string? headline)
    {
        var identity = !String.IsNullOrWhiteSpace(url) ? url.Trim() : headline?.Trim() ?? "news";
        return identity.ToLowerInvariant();
    }

    internal static string FormatRelatedTickers(IEnumerable<string> tickers)
    {
        var values = tickers
            .SelectMany(ticker => ticker.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ticker => ticker == "MARKET" ? "ZZZZ" : ticker, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return values.Length == 0 ? "MARKET" : String.Join(", ", values);
    }

    private static string NormalizeHeadline(string? headline, string? summary, string? source, string? provider)
    {
        foreach (var candidate in new[] { headline, ExtractUsefulSummaryLead(summary), source, provider })
        {
            var value = candidate?.Trim();
            if (!String.IsNullOrWhiteSpace(value) && !IsGenericNewsLabel(value))
            {
                return value;
            }
        }

        return "News update";
    }

    private static string? NormalizeSummary(string? summary, string headline)
    {
        var value = summary?.Trim();
        if (String.IsNullOrWhiteSpace(value) ||
            value.Equals(headline, StringComparison.OrdinalIgnoreCase) ||
            IsGenericNewsLabel(value))
        {
            return null;
        }

        return value;
    }

    private static string? ExtractUsefulSummaryLead(string? summary)
    {
        var value = summary?.Trim();
        if (String.IsNullOrWhiteSpace(value) || IsGenericNewsLabel(value))
        {
            return null;
        }

        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && IsGenericNewsLabel(value[..separator]))
        {
            value = value[(separator + 3)..].Trim();
        }

        return value.Length <= 160 ? value : $"{value[..160].TrimEnd()}...";
    }

    private static bool IsGenericNewsLabel(string value)
    {
        var normalized = value.Trim();
        return normalized.Equals("Market", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Stock", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("News", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Blog", StringComparison.OrdinalIgnoreCase);
    }
}
