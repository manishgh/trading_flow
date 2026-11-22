using Microsoft.Extensions.Hosting;
using TradingFlow.Alpaca;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Domain.Earnings;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Earnings;
using TradingFlow.Finviz;
using TradingFlow.Web.Models;
using System.Text.RegularExpressions;

namespace TradingFlow.Web.Services;

/// <summary>
/// Maintains the rolling news view over retained records for mobile and operator views. Strategy
/// execution still uses the configured catalyst provider directly, while this
/// service gives us a continuous, low-latency feed to inspect.
/// </summary>
public sealed class NewsFeedService : BackgroundService
{
    /// <summary>How far back the News page and the Desk news panel look by default.</summary>
    internal static readonly TimeSpan DisplayWindow = TimeSpan.FromHours(72);
    // One re-scoring pass: at most this many items, this long, and this many fallback answers in a row.
    private const int RescoreLimit = 200;
    private const int RescoreFallbackStop = 3;
    private static readonly TimeSpan RescoreBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int EarningsStoriesPerStock = 5;
    // Bounded so the per-ticker REST sweep stays cheap at the poll cadence. This only has to
    // outlast a websocket outage, not the full display window.
    private static readonly TimeSpan AlpacaGapFillWindow = TimeSpan.FromHours(6);
    // Shared because the sweep now runs on every poll; a per-call client would leak a connection
    // pool every minute.
    private static readonly HttpClient AlpacaHttpClient = new();

    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;
    private readonly SqliteNewsFeedRepository repository;
    private readonly ConfigCatalogService catalog;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly ArticleTextFetcher articleTextFetcher;
    private readonly IEarningsRepository earningsRepository;
    private readonly OfficialMarketNewsProvider officialNews;
    private readonly FinvizClient finviz;
    private readonly TimeProvider clock;
    private readonly ILogger<NewsFeedService> logger;
    private readonly IWishlistRepository? wishlists;
    private readonly NewsSentimentAnalyzer sentimentAnalyzer;
    private int alpacaSweepOffset;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private volatile bool running;
    private sealed record RefreshObservation(string State, DateTimeOffset? CompletedAtUtc,
        IReadOnlyList<NewsSourceReading> Sources, IReadOnlyDictionary<string, NewsSourceReading> AlpacaSymbols, string Detail);
    private volatile RefreshObservation latest = new("pending", null, [], new Dictionary<string, NewsSourceReading>(),
        "The first provider refresh has not completed on this host.");

    // Return one immutable refresh observation. The worker state alone says nothing about its coverage.
    public NewsRefreshStatus GetRefreshStatus() => View(latest);

    // Copy the current worker state alongside the finished pass, whose provider facts are immutable.
    private NewsRefreshStatus View(RefreshObservation observation) =>
        new(running, observation.State, observation.CompletedAtUtc, observation.Sources, observation.Detail);

    // Claim an empty result only for the selected Alpaca ticker and actual covered portion of the window.
    public NewsWindowStatus DescribeWindow(string? provider, string? ticker, DateTimeOffset sinceUtc, int matching,
        bool maySeeTickerCoverage)
    {
        var observation = latest;
        var refresh = View(observation);
        if (maySeeTickerCoverage && matching == 0 && refresh.State is ("success" or "partial") &&
            String.Equals(provider, "alpaca", StringComparison.OrdinalIgnoreCase) && ticker is not null &&
            observation.AlpacaSymbols.TryGetValue(ticker, out var evidence) &&
            evidence is { State: "success", WindowStartUtc: { } start, WindowEndUtc: { } end } &&
            start <= sinceUtc && end >= sinceUtc && clock.GetUtcNow() - end <= TimeSpan.FromMinutes(2))
            return new("No matching news through the last refresh",
                $"Alpaca checked {ticker} from {sinceUtc:yyyy-MM-dd HH:mm:ss} to {end:yyyy-MM-dd HH:mm:ss} UTC. Later articles may still arrive.", refresh);
        var heading = refresh.State switch
        {
            "pending" when refresh.Running => "First refresh pending",
            "pending" => "News refresh has not run on this host",
            "failed" => "News refresh failed",
            "partial" => "News refresh has partial results",
            _ when matching == 0 => "No matching stored headlines",
            _ => "News results updated"
        };
        var detail = matching > 0
            ? "Rows are from the retained news store and can predate the latest feed attempt."
            : "No stored headlines match these filters. Coverage of this source, ticker and time window has not been established.";
        return new(heading, detail, refresh);
    }

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
        FinvizClient finviz,
        TimeProvider clock,
        NewsSentimentAnalyzer sentimentAnalyzer,
        ILogger<NewsFeedService> logger,
        IWishlistRepository? wishlists = null)
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
        this.finviz = finviz;
        this.clock = clock;
        this.sentimentAnalyzer = sentimentAnalyzer;
        this.logger = logger;
        this.wishlists = wishlists;
    }

    public async Task<MobileNewsFeedResponse> GetRollingAsync(
        int hours,
        string? ticker,
        CancellationToken cancellationToken)
    {
        var clampedHours = Math.Clamp(hours, 1, (int)DisplayWindow.TotalHours);
        var windowStart = DateTimeOffset.UtcNow.AddHours(-clampedHours);
        var items = await repository.GetRecentAsync(windowStart, 200, ticker, cancellationToken);
        return new MobileNewsFeedResponse(
            true,
            officialNews.IsSecConfigured ? "finviz+alpaca+fed+sec" : "finviz+alpaca+fed",
            $"Rolling {clampedHours}h feed. Older stored headlines remain available for research.",
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

    // Collect and store each successful source independently; publish attempt evidence only after the store write.
    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        var readings = new List<NewsSourceReading>();
        IReadOnlyDictionary<string, NewsSourceReading> alpacaSymbols = new Dictionary<string, NewsSourceReading>();
        try
        {
            var nowUtc = clock.GetUtcNow();
            var evidenceWindow = EarningsNewsWindowPolicy.Resolve(nowUtc);
            var displayStartUtc = nowUtc.Subtract(DisplayWindow);
            var earningsTickers = await GetCurrentEarningsTickersAsync(nowUtc, cancellationToken);
            var collections = new List<NewsCollection>();
            collections.AddRange(await LoadFinvizNewsAsync(cancellationToken));
            // The Alpaca websocket delivers live items only and cannot replay what it missed, so a
            // disconnect silently loses every article published while it was down. This rolling REST
            // sweep is the gap-fill, and the dedupe below drops whatever the socket already delivered.
            var alpaca = await LoadAlpacaNewsAsync(earningsTickers, nowUtc.Subtract(AlpacaGapFillWindow), nowUtc, cancellationToken);
            collections.Add(alpaca.Collection);
            alpacaSymbols = alpaca.Symbols;
            collections.AddRange(await officialNews.ReadSourcesAsync(earningsTickers, evidenceWindow.StartUtc, cancellationToken));
            readings.AddRange(collections.Select(x => x.Reading));

            // Dedupe before capping. Capping first would let already-stored items crowd out the
            // gap-filled ones, which are the whole point of the Alpaca sweep.
            var existingKeys = (await repository.ListIdentitiesSinceAsync(displayStartUtc, cancellationToken))
                .Select(StorageDedupeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unseen = collections.SelectMany(item => item.Events)
                .Where(item => item.Timestamp >= displayStartUtc && !existingKeys.Contains(StorageDedupeKey(item)))
                .DistinctBy(StorageDedupeKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Timestamp)
                .Take(501)
                .ToArray();
            var unseenEvents = unseen.Take(500).ToArray();
            // Publish provider headlines immediately; article enrichment must not hold up the live feed.
            var added = await repository.InsertNewAsync(unseenEvents, cancellationToken);
            var capped = unseen.Length > unseenEvents.Length;
            var state = ClassifyRefresh(readings, capped);
            latest = new(state, clock.GetUtcNow(), readings.ToArray(),
                capped ? new Dictionary<string, NewsSourceReading>() : alpacaSymbols,
                capped ? "The 500-new-headline storage limit was reached; this pass does not prove empty coverage."
                    : "Source readings reflect the latest completed refresh; retained headlines are displayed separately.");
            var enrichedEvents = await EnrichNewsAsync(
                unseenEvents.Where(item => added.Contains(SqliteNewsFeedRepository.StorageId(item))).ToArray(), cancellationToken);
            await repository.ApplyEnrichmentAsync(enrichedEvents, added, cancellationToken);
            // The rolling window limits this view; original stored news must not expire automatically.

            logger.LogInformation(
                "Rolling news feed refresh stored {EventCount} event(s). Providers={Providers}",
                enrichedEvents.Count,
                String.Join(",", enrichedEvents.Select(x => x.Provider ?? "unknown").Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            latest = new("failed", clock.GetUtcNow(), readings.ToArray(), new Dictionary<string, NewsSourceReading>(),
                "The refresh did not finish. See the source attempts below; stored rows can still be read.");
            throw;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    // A failure or a cap cannot become an all-sources success, even when other sources returned rows.
    internal static string ClassifyRefresh(IReadOnlyList<NewsSourceReading> readings, bool storageCapped)
    {
        var attempted = readings.Where(reading => reading.State is not ("not queried" or "cached")).ToArray();
        if (attempted.Length == 0) return "partial";
        if (attempted.All(reading => reading.State == "failed")) return "failed";
        return storageCapped || attempted.Any(reading => reading.State != "success") ? "partial" : "success";
    }

/// <summary>
    /// Stories for the given stocks since a time, merged across stocks and providers. Each screen states its
    /// own filters; market-wide context is included only when asked for and only when it bears on stocks.
    /// </summary>
    public async Task<IReadOnlyList<NewsStory>> GetStoriesAsync(NewsStoryQuery query, CancellationToken cancellationToken)
    {
        var symbols = query.Symbols
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (query.IncludeMarketContext) symbols.Add("MARKET");
        if (symbols.Count == 0) return [];
        var records = await repository.GetRecentForTickersAsync(query.SinceUtc, query.Limit, symbols, cancellationToken);
        return NewsStories.Combine(
            records.Where(item => !item.Ticker.Equals("MARKET", StringComparison.OrdinalIgnoreCase) || IsRelevantMarketEvidence(item)),
            query.PreferredProvider);
    }

    /// <summary>Earnings news leads with Alpaca's copy of a story, because its stream carries results first.</summary>
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
        // Only stories about a stock reporting earnings, never market-wide context. Each stock keeps its newest
        // stories rather than the whole page sharing one newest-N cap: on a busy evening a shared cap pushed a
        // result headline off the page within the hour. A result headline ("EPS $4.30 Beats $4.07 Estimate")
        // always stays, however many newer stories follow it.
        var stories = await GetStoriesAsync(new NewsStoryQuery(normalizedTickers, window.StartUtc, 5000,
            IncludeMarketContext: false, PreferredProvider: NewsStories.RealTimeProvider), cancellationToken);
        var keptPerTicker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return stories
            .Where(story => story.Tickers.Any(normalizedTickers.Contains))
            .OrderByDescending(story => story.Lead.Timestamp)
            .ThenBy(story => story.Lead.Headline, StringComparer.OrdinalIgnoreCase)
            .Where(story =>
            {
                var related = story.Tickers.Where(normalizedTickers.Contains).ToArray();
                if (related.All(ticker => keptPerTicker.GetValueOrDefault(ticker) >= EarningsStoriesPerStock) &&
                    !TradingFlow.Earnings.EarningsNewsResultExtractor.TryExtract(story.Lead.Headline, out _)) return false;
                foreach (var ticker in related) keptPerTicker[ticker] = keptPerTicker.GetValueOrDefault(ticker) + 1;
                return true;
            })
            .Select(story => ToEarningsEvidence(story.Lead, story.Tickers, normalizedTickers))
            .ToArray();
    }

    // Track worker lifetime separately from the freshness and coverage of an individual pass.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        running = true;
        try
        {
            await DelayStartupAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RefreshOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogWarning(error, "Rolling news feed refresh failed."); }
                // Outside the refresh gate, so on-demand refreshes never wait for scoring.
                try
                {
                    await sentimentAnalyzer.ReadHealthAsync(stoppingToken);
                    await RescoreAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogWarning(error, "News re-scoring failed; it is tried again after the next refresh."); }
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        finally { running = false; }
    }

    private static async Task DelayStartupAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
    }

    // The two Finviz exports are independent attempts and do not establish rolling date-window coverage.
    private async Task<NewsCollection[]> LoadFinvizNewsAsync(CancellationToken cancellationToken)
    {
        if (!finviz.IsConfigured)
        {
            return [NotQueried("Finviz market", "FINVIZ_API_KEY is not configured."),
                NotQueried("Finviz stocks", "FINVIZ_API_KEY is not configured.")];
        }

        const string detail = "Rolling export returned; date-window and pagination coverage are unverified.";
        return [await ReadSourceAsync("Finviz market", () => finviz.GetNewsExportAsync(1, cancellationToken), detail, cancellationToken),
            await ReadSourceAsync("Finviz stocks", () => finviz.GetNewsExportAsync(3, cancellationToken), detail, cancellationToken)];
    }

    // Preserve each queried ticker's coverage; a rotating batch cannot establish market-wide coverage.
    private async Task<(NewsCollection Collection, IReadOnlyDictionary<string, NewsSourceReading> Symbols)> LoadAlpacaNewsAsync(
        IReadOnlyCollection<string> earningsTickers,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var none = new Dictionary<string, NewsSourceReading>(StringComparer.OrdinalIgnoreCase);
        if (!alpacaCredentials.IsConfigured)
            return (NotQueried("Alpaca REST", "Alpaca credentials are not configured."), none);

        var lists = wishlists is null ? [] : await wishlists.ListAsync(cancellationToken);
        var (tickers, nextOffset) = SelectNewsTargets(lists.SelectMany(x => x.Items).Where(x => x.Active).Select(x => x.Ticker), earningsTickers, alpacaSweepOffset);
        alpacaSweepOffset = nextOffset;
        if (tickers.Count == 0)
            return (NotQueried("Alpaca REST", "No followed or earnings tickers were selected for this sweep."), none);

        var provider = new AlpacaNewsProvider(
            AlpacaHttpClient,
            AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
            {
                KeyId = alpacaCredentials.KeyId,
                SecretKey = alpacaCredentials.SecretKey
            },
            runtimeFactory.RawArchiveWriter,
            logger: null,
            sentimentAnalyzer: new VaderSentimentAnalyzer(),
            maxArticlesPerTicker: 25);

        using var throttle = new SemaphoreSlim(8);
        var rows = await Task.WhenAll(tickers.Select(ticker =>
            ReadAlpacaSymbolAsync(provider, ticker, windowStart, windowEnd, throttle, cancellationToken)));
        var evidence = rows.ToDictionary(row => row.Reading.Source, row => row.Reading, StringComparer.OrdinalIgnoreCase);
        var events = rows.SelectMany(row => row.Events).GroupBy(NewsDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var complete = rows.Count(row => row.Reading.State == "success");
        var failures = rows.Count(row => row.Reading.State == "failed");
        var state = failures == rows.Length ? "failed" : complete == rows.Length ? "success" : "partial";
        var detail = $"{rows.Length} selected ticker(s) checked; {complete} complete, {rows.Length - complete - failures} partial, {failures} failed. Coverage is ticker-specific.";
        return (new(events, new("Alpaca REST", state, clock.GetUtcNow(), events.Length, detail, windowStart, windowEnd)), evidence);
    }

    // Report the actual pages read; only a finished query with no cap or error establishes coverage.
    private async Task<NewsCollection> ReadAlpacaSymbolAsync(AlpacaNewsProvider provider, string ticker,
        DateTimeOffset start, DateTimeOffset end, SemaphoreSlim throttle, CancellationToken token)
    {
        await throttle.WaitAsync(token);
        try
        {
            var result = await provider.GetCatalystEvidenceAsync(ticker, start, end, token);
            var state = result.Complete ? "success" : result.PagesRead > 0 ? "partial" : "failed";
            return new(result.Events, new(ticker, state, clock.GetUtcNow(), result.Events.Count,
                $"{ticker}: {result.Detail} Pages received: {result.PagesRead}.", start, end));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Alpaca news collection failed for {Ticker}.", ticker);
            return new([], new(ticker, "failed", clock.GetUtcNow(), 0, $"{ticker}: {NewsRequestFailure(error)}", start, end));
        }
        finally { throttle.Release(); }
    }

    internal static (IReadOnlyList<string> Symbols, int NextOffset) SelectNewsTargets(
        IEnumerable<string> wishlistSymbols, IEnumerable<string> earningsSymbols, int offset)
    {
        var all = wishlistSymbols.Concat(earningsSymbols).Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (all.Length == 0) return ([], 0);
        var start = offset % all.Length;
        var batch = all.Skip(start).Concat(all.Take(start)).Take(200).ToArray();
        return (batch, (start + batch.Length) % all.Length);
    }

    // Keep provider failures distinct from a successful attempt that returned zero rows.
    internal async Task<NewsCollection> ReadSourceAsync(string provider,
        Func<Task<IReadOnlyList<CatalystEvent>>> load, string detail, CancellationToken cancellationToken)
    {
        try
        {
            var events = await load();
            return new(events, new(provider, "success", clock.GetUtcNow(), events.Count, detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "News collection failed for {Provider}; other provider feeds continue.", provider);
            return new([], new(provider, "failed", clock.GetUtcNow(), 0, NewsRequestFailure(error)));
        }
    }

    // Do not expose provider exception messages, which can contain credential-bearing URLs.
    internal static string NewsRequestFailure(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => $"Provider returned HTTP {(int)status}; no coverage established.",
        OperationCanceledException => "The provider request timed out; no coverage established.",
        _ => "The request or response could not be read; no coverage established."
    };

    // No request is a different observation from a request that failed.
    internal static NewsCollection NotQueried(string source, string detail) =>
        new([], new(source, "not queried", null, 0, detail));

    private async Task<IReadOnlyList<CatalystEvent>> EnrichNewsAsync(
        IReadOnlyList<CatalystEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return events;
        }

        using var throttle = new SemaphoreSlim(4);
        var tasks = events.Select(item => EnrichSingleNewsAsync(item, throttle, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    /// <summary>Bind enriched sentiment to the analyzer actually used and the time its assessment became available.</summary>
    private async Task<CatalystEvent> EnrichSingleNewsAsync(
        CatalystEvent item,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            // Text from a page that may be a different story must not reach the summary or the score.
            var linkedText = NewsLinkMatch.Classify(item.Provider, item.Headline, item.Url) == NewsLinkMatch.Article
                ? await articleTextFetcher.FetchTextAsync(item.Url, cancellationToken)
                : null;
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
            var sentiment = await sentimentAnalyzer.AnalyzeInBackgroundAsync(article, cancellationToken);
            var assessedAt = DateTimeOffset.UtcNow;
            var previousAvailable = item.DecisionAvailableAt ?? item.ReceivedAt ?? item.Timestamp;

            return item with
            {
                SentimentScore = sentiment.Score,
                Summary = preview,
                ClassificationVersion = sentiment.AnalyzerName,
                DecisionAvailableAt = assessedAt > previousAvailable ? assessedAt : previousAvailable,
                DecisionAvailabilityEvidence = item.ReceivedAt is null
                    ? CatalystAvailabilityEvidence.ProviderTimestampAndAssessmentObservedTime
                    : CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime
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

    /// <summary>
    /// Scores recent items whose recorded scorer is VADER or still pending with FinBERT, from their headline and summary. A
    /// pending item takes whatever scorer answers. Stops after a time budget or several fallback answers in a row, so a
    /// stopped FinBERT costs a few quick requests. Items without a recorded scorer are left alone.
    /// </summary>
    internal async Task<int> RescoreAsync(CancellationToken cancellationToken)
    {
        // Without FinBERT on this host a VADER score cannot improve, so only unfinished ones are scored.
        string[] scorers = sentimentAnalyzer.IsConfigured
            ? [NewsSentimentAnalyzer.FallbackName, SqliteNewsFeedRepository.PendingScorer]
            : [SqliteNewsFeedRepository.PendingScorer];
        var pending = await repository.GetScoredByAsync(scorers,
            clock.GetUtcNow().Subtract(DisplayWindow), RescoreLimit, cancellationToken);
        var started = clock.GetTimestamp();
        int updated = 0, fallbacks = 0;
        foreach (var item in pending)
        {
            if (fallbacks >= RescoreFallbackStop || clock.GetElapsedTime(started) >= RescoreBudget) break;
            var result = await sentimentAnalyzer.AnalyzeInBackgroundAsync(new NewsArticle(item.Provider, item.Id, item.Headline,
                item.Timestamp, new[] { item.Ticker }, item.Summary, null, item.Source, item.Url), cancellationToken);
            var isFallback = NewsSentimentAnalyzer.IsFallback(result.AnalyzerName);
            fallbacks = isFallback ? fallbacks + 1 : 0;
            if (isFallback && NewsSentimentAnalyzer.IsFallback(item.SentimentAnalyzer)) continue;
            if (await repository.UpdateSentimentAsync(item, result.Score, result.AnalyzerName, cancellationToken)) updated++;
        }

        if (fallbacks >= RescoreFallbackStop)
            logger.LogWarning("News re-scoring stopped after {Count} articles in a row without a FinBERT score (FinBERT failed or is paused); {Waiting} recent item(s) keep their current score.",
                fallbacks, pending.Count - updated);
        if (updated > 0) logger.LogInformation("Re-scored {Count} news item(s) that had a VADER or pending score.", updated);
        return updated;
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
            summary, ClassifyEvidence(new PersistedNewsItem {Ticker=item.Ticker,Provider=item.Provider ?? "",Headline=item.Headline}),
            LinkKind: NewsLinkMatch.Classify(item.Provider, item.Headline, item.Url));
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
            summary, ClassifyEvidence(item),
            LinkKind: NewsLinkMatch.Classify(item.Provider, item.Headline, item.Url));
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

    // Collapses copies within one read; Alpaca articles by article ID per stock, because different articles share links.
    internal static string NewsDedupeKey(CatalystEvent item)
    {
        if (IsAlpaca(item.Provider) && !String.IsNullOrWhiteSpace(item.ExternalId)) return $"{SqliteNewsFeedRepository.StorageId(item)}|{item.Ticker}";
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

    // Alpaca articles are the same only when their article ID is the same, because different headlines can share a link.
    // Other providers keep stock plus link: Finviz IDs contain a hash that changes at every restart.
    internal static string StorageDedupeKey(CatalystEvent item) => IsAlpaca(item.Provider)
        ? SqliteNewsFeedRepository.StorageId(item)
        : $"{item.Provider}|{item.Ticker}|{NormalizeArticleIdentity(item.Url, item.Headline)}";

    internal static string StorageDedupeKey(PersistedNewsItem item) => IsAlpaca(item.Provider)
        ? item.Id
        : $"{item.Provider}|{item.Ticker}|{NormalizeArticleIdentity(item.Url, item.Headline)}";

    // True for items delivered by Alpaca, live or through the gap-fill read.
    private static bool IsAlpaca(string? provider) => String.Equals(provider, "alpaca", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeHeadlineIdentity(string? headline)
    {
        var normalized = Regex.Replace(headline?.Trim().ToLowerInvariant() ?? "news", @"[^a-z0-9]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static EarningsNewsEvidenceResponse ToEarningsEvidence(
        PersistedNewsItem first,
        IReadOnlyList<string> storyTickers,
        IReadOnlySet<string> earningsTickers)
    {
        var relatedTickers = storyTickers
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
            first.Provider ?? "system",
            first.Source,
            first.Url,
            ExplainEvidence(category),
            NewsLinkMatch.Classify(first.Provider, first.Headline, first.Url));
    }

    private static string ClassifyEvidence(PersistedNewsItem item) => TradingFlow.Domain.News.NewsEvidenceClassification.Classify(item);

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

    private static readonly string[] MacroKeywords = TradingFlow.Domain.News.NewsEvidenceClassification.MacroKeywords;

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
