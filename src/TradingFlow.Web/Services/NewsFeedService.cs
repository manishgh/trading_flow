using Microsoft.Extensions.Hosting;
using TradingFlow.Alpaca;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Earnings;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Earnings;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;
using System.Text.RegularExpressions;

namespace TradingFlow.Web.Services;

/// <summary>
/// Maintains the rolling news cache for mobile and operator views. Strategy
/// execution still uses the configured catalyst provider directly, while this
/// service gives us a continuous, low-latency feed to inspect.
/// </summary>
public sealed class NewsFeedService : BackgroundService
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(72);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;
    private readonly SqliteNewsFeedRepository repository;
    private readonly ConfigCatalogService catalog;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly ArticleTextFetcher articleTextFetcher;
    private readonly IEarningsRepository earningsRepository;
    private readonly OfficialMarketNewsProvider officialNews;
    private readonly TimeProvider clock;
    private readonly ILogger<NewsFeedService> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public NewsFeedService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        SqliteNewsFeedRepository repository,
        ConfigCatalogService catalog,
        AlpacaCredentialProvider alpacaCredentials,
        ArticleTextFetcher articleTextFetcher,
        IEarningsRepository earningsRepository,
        OfficialMarketNewsProvider officialNews,
        TimeProvider clock,
        ILogger<NewsFeedService> logger)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
        this.repository = repository;
        this.catalog = catalog;
        this.alpacaCredentials = alpacaCredentials;
        this.articleTextFetcher = articleTextFetcher;
        this.earningsRepository = earningsRepository;
        this.officialNews = officialNews;
        this.clock = clock;
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
            officialNews.IsSecConfigured ? "finviz+alpaca+fed+sec" : "finviz+alpaca+fed",
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
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var nowUtc = clock.GetUtcNow();
            var evidenceWindow = EarningsNewsWindowPolicy.Resolve(nowUtc);
            var retentionStartUtc = nowUtc.Subtract(RetentionWindow);
            var earningsTickers = await GetCurrentEarningsTickersAsync(nowUtc, cancellationToken);
            var events = new List<CatalystEvent>();
            events.AddRange(await LoadFinvizNewsAsync(cancellationToken));
            events.AddRange(await LoadAlpacaNewsAsync(earningsTickers, evidenceWindow.StartUtc, nowUtc, cancellationToken));
            events.AddRange(await officialNews.GetEventsAsync(earningsTickers, evidenceWindow.StartUtc, cancellationToken));
            events = events
                .Where(item => item.Timestamp >= retentionStartUtc)
                .OrderByDescending(item => item.Timestamp)
                .Take(500)
                .ToList();

            var existing = await repository.GetRecentAsync(retentionStartUtc, 500, null, cancellationToken);
            var existingKeys = existing
                .Select(StorageDedupeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unseenEvents = events
                .Where(item => !existingKeys.Contains(StorageDedupeKey(item)))
                .ToArray();
            var enrichedEvents = await EnrichNewsAsync(unseenEvents, cancellationToken);
            await repository.UpsertAsync(enrichedEvents, cancellationToken);
            await repository.PruneOlderThanAsync(retentionStartUtc, cancellationToken);

            logger.LogInformation(
                "Rolling news feed refresh stored {EventCount} event(s). Providers={Providers}",
                enrichedEvents.Count,
                String.Join(",", enrichedEvents.Select(x => x.Provider ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<IReadOnlyList<EarningsNewsEvidenceResponse>> GetEarningsTimelineAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedTickers = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var window = EarningsNewsWindowPolicy.Resolve(nowUtc);
        var stored = await repository.GetRecentAsync(window.StartUtc, 500, null, cancellationToken);

        return stored
            .Where(item => normalizedTickers.Contains(item.Ticker) ||
                (item.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase) && IsRelevantMarketEvidence(item)))
            .GroupBy(ArticleDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToEarningsEvidence(group, normalizedTickers))
            .OrderByDescending(item => item.PublishedAtUtc)
            .ThenBy(item => item.Headline, StringComparer.OrdinalIgnoreCase)
            .Take(120)
            .ToArray();
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

    private async Task<IReadOnlyList<CatalystEvent>> LoadAlpacaNewsAsync(
        IReadOnlyCollection<string> earningsTickers,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        if (!alpacaCredentials.IsConfigured)
        {
            logger.LogDebug("Skipping Alpaca rolling news refresh because Alpaca credentials are not configured.");
            return Array.Empty<CatalystEvent>();
        }

        var tickers = catalog.GetPaperConfigs()
            .SelectMany(config => config.Config.Tickers)
            .Concat(earningsTickers)
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
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
            runtimeFactory.RawArchiveWriter,
            logger: null,
            sentimentAnalyzer: new VaderSentimentAnalyzer(),
            maxArticlesPerTicker: 25);

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
        return NormalizeHeadlineIdentity(item.Headline);
    }

    private static string MobileArticleDedupeKey(MobileNewsItem item)
    {
        return NormalizeHeadlineIdentity(item.Headline);
    }

    private static string NormalizeArticleIdentity(string? url, string? headline)
    {
        var identity = !String.IsNullOrWhiteSpace(url) ? url.Trim() : headline?.Trim() ?? "news";
        return identity.ToLowerInvariant();
    }

    private async Task<IReadOnlyList<string>> GetCurrentEarningsTickersAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var operatorDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(nowUtc, EarningsTimeZones.OperatorLocal).DateTime);
        var nextBusinessDate = EarningsCalendarDates.NextWeekday(operatorDate);
        var calendar = await earningsRepository.GetCalendarAsync(
            operatorDate,
            nextBusinessDate,
            tickers: null,
            cancellationToken);
        return calendar
            .Select(item => item.Ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StorageDedupeKey(CatalystEvent item) =>
        $"{item.Provider}|{item.Ticker}|{NormalizeArticleIdentity(item.Url, item.Headline)}";

    private static string StorageDedupeKey(PersistedNewsItem item) =>
        $"{item.Provider}|{item.Ticker}|{NormalizeArticleIdentity(item.Url, item.Headline)}";

    internal static string NormalizeHeadlineIdentity(string? headline)
    {
        var normalized = Regex.Replace(headline?.Trim().ToLowerInvariant() ?? "news", @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static EarningsNewsEvidenceResponse ToEarningsEvidence(
        IGrouping<string, PersistedNewsItem> group,
        IReadOnlySet<string> earningsTickers)
    {
        var items = group.OrderByDescending(item => item.Timestamp).ToArray();
        var first = items[0];
        var relatedTickers = items
            .Select(item => item.Ticker)
            .Where(ticker => earningsTickers.Contains(ticker))
            .ToArray();
        var category = ClassifyEvidence(first);
        return new EarningsNewsEvidenceResponse(
            first.Timestamp.ToUniversalTime(),
            FormatRelatedTickers(relatedTickers.Length > 0 ? relatedTickers : new[] { "MARKET" }),
            category,
            first.SentimentScore,
            first.SentimentScore >= 0.15m ? "Bullish" : first.SentimentScore <= -0.15m ? "Bearish" : "Neutral",
            NormalizeHeadline(first.Headline, first.Summary, first.Source, first.Provider),
            NormalizeSummary(first.Summary, first.Headline),
            String.Join(" + ", items.Select(item => item.Provider).Distinct(StringComparer.OrdinalIgnoreCase)),
            first.Source,
            first.Url,
            ExplainEvidence(category));
    }

    private static string ClassifyEvidence(PersistedNewsItem item)
    {
        if (item.Provider.Equals("sec_edgar", StringComparison.OrdinalIgnoreCase)) return "SEC filing";
        if (item.Provider.Equals("federal_reserve", StringComparison.OrdinalIgnoreCase)) return "Fed";
        if (!item.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase)) return "Company";
        return ContainsAny(item.Headline, MacroKeywords) ? "Macro" : "Market";
    }

    private static string ExplainEvidence(string category) => category switch
    {
        "Company" => "Direct company evidence can change earnings expectations, demand, guidance, or risk.",
        "SEC filing" => "Official filing; verify the form, publication time, and disclosed earnings or guidance changes.",
        "Fed" => "Federal Reserve policy can change rates, valuation multiples, liquidity, and market risk appetite.",
        "Macro" => "Macro conditions can affect sector demand, financing costs, currencies, and valuation.",
        _ => "Broad market context only; it is not a standalone entry signal."
    };

    private static bool IsRelevantMarketEvidence(PersistedNewsItem item) =>
        item.Provider.Equals("federal_reserve", StringComparison.OrdinalIgnoreCase) ||
        ContainsAny($"{item.Headline} {item.Summary}", MarketEvidenceKeywords);

    private static bool ContainsAny(string? text, IReadOnlyCollection<string> keywords)
    {
        var value = text ?? String.Empty;
        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] MacroKeywords =
    [
        "inflation", "cpi", "ppi", "jobs", "payroll", "unemployment", "gdp", "yield", "treasury",
        "interest rate", "currency", "dollar", "oil", "energy", "tariff"
    ];

    private static readonly string[] MarketEvidenceKeywords =
    [
        .. MacroKeywords, "federal reserve", "fed ", "fomc", "powell", "sec filing", "earnings",
        "guidance", "market", "nasdaq", "s&p 500"
    ];

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
