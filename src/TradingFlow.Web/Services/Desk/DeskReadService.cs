using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Data.News;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Desk;

public sealed record DeskArticle(string Headline, string? Url, string Provider, DateTimeOffset PublishedAtUtc, IReadOnlyList<string> Symbols,
    string? Source = null, string? Summary = null, decimal? SentimentScore = null, string? Classification = null);
public sealed record DeskNewsSentiment(int Positive, int Negative, int Neutral, int Unscored,
    DateTimeOffset WindowStartUtc, DateTimeOffset AsOfUtc, bool Available);
public sealed record DeskRow(DeskMembership Membership, AlpacaLatestQuote? Quote, string QuoteFreshness,
    IndicatorSnapshot? Technical, DeskHolding? Holding, bool HoldingsKnown, IReadOnlyList<DeskArticle> News, DailyObservationResult Observation, AlpacaPreviousClose? PreviousClose = null,
    StockStrategyAssessment? StrategyAssessment = null, DeskNewsSentiment? NewsSentiment = null, AlpacaSessionVolume? SessionVolume = null);
public sealed record DeskSnapshot(DeskSelection Selection, IReadOnlyList<DeskRow> Rows, DeskAccount Account,
    IReadOnlyList<DeskArticle> News, string NewsCoverage, string Session, DateTimeOffset ReadAtUtc, string Feed);

public sealed class DeskReadService(AlpacaQuoteService quotes, IDeskAccountReader account,
    IDailyObservationService daily, NewsFeedService newsFeed, OperationalStatusService operations,
    IMemoryCache cache, TimeProvider clock, ILogger<DeskReadService> logger)
{
    /// <summary>Joins observations and account facts with separately dated provider volume; daily indicators never substitute for session volume.</summary>
    public async Task<DeskSnapshot> ReadAsync(DeskSelection selection, CancellationToken ct)
    {
        var symbols = selection.Members.Select(x => x.Symbol).ToArray();
        var quoteTask = quotes.GetNewestQuotesAsync(symbols, ct);
        var accountTask = account.ReadAsync(ct);
        var displayTask = quotes.GetDisplaySnapshotsAsync(symbols, "sip", clock.GetUtcNow(), ct);
        var sessionTask = operations.GetTradingSessionAsync(ct);
        var newsTask = ReadNewsAsync(symbols, ct);
        await Task.WhenAll(quoteTask, accountTask, sessionTask, newsTask, displayTask);
        var holdings = (await accountTask).Holdings.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        var quoteMap = await quoteTask;
        var displayMap = await displayTask;
        var newsResult = await newsTask;
        var readAt = clock.GetUtcNow();
        var sentiments = SummarizeNews(newsResult.Items, symbols, readAt, newsResult.Available);
        var newsBySymbol = newsResult.Items.Where(article => article.PublishedAtUtc <= readAt)
            .SelectMany(article => article.Symbols.Select(symbol => (Symbol: symbol, Article: article)))
            .ToLookup(item => item.Symbol, item => item.Article, StringComparer.OrdinalIgnoreCase);
        var rows = new List<DeskRow>();
        foreach (var member in selection.Members)
        {
            var display = displayMap.GetValueOrDefault(member.Symbol);
            var quote = quoteMap.GetValueOrDefault(member.Symbol);
            var observation = await daily.GetAsync(member.Symbol, ct);
            var technical = observation.Snapshot;
            rows.Add(new(member, quote, Freshness(quote?.Timestamp, clock.GetUtcNow()), technical,
                holdings.GetValueOrDefault(member.Symbol), (await accountTask).Available,
                newsBySymbol[member.Symbol].Take(5).ToArray(), observation, display?.PreviousClose,
                NewsSentiment: sentiments[member.Symbol], SessionVolume: display?.SessionVolume));
        }
        return new(selection, rows, await accountTask, newsResult.Items.Where(article => article.PublishedAtUtc <= readAt).Take(40).ToArray(), newsResult.Detail,
            (await sessionTask)?.Session.ToString() ?? "Unknown", readAt, "SIP");
    }

    public static string Freshness(DateTimeOffset? timestamp, DateTimeOffset now) => timestamp is null ? "unavailable"
        : timestamp > now.AddSeconds(5) ? "clock mismatch" : now - timestamp <= TimeSpan.FromSeconds(20) ? "current"
        : now - timestamp <= TimeSpan.FromMinutes(2) ? "delayed" : "stale";

    private sealed record NewsResult(IReadOnlyList<DeskArticle> Items, string Detail, bool Available);
    // Read bounded stored stories once per cache window; distinguish failures from successful empty reads.
    private async Task<NewsResult> ReadNewsAsync(string[] symbols, CancellationToken ct) =>
        (await cache.GetOrCreateAsync("desk-news:" + String.Join(',', symbols), async entry => {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15);
            try
            {
                var stories = await newsFeed.GetStoriesAsync(new NewsStoryQuery(symbols, clock.GetUtcNow().AddHours(-72), 1000), ct);
                return new NewsResult(ToArticles(stories), "Matched stored news · last 72h · up to 1,000 symbol records. Refreshes while connected; historical coverage may be incomplete.", true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Desk news read failed."); return new NewsResult([], "News is unavailable. Retry when the connection recovers.", false); }
        }))!;
    // Count deduplicated stories per symbol in the inclusive trailing UTC hour before any display limits.
    public static IReadOnlyDictionary<string, DeskNewsSentiment> SummarizeNews(IEnumerable<DeskArticle> articles,
        IEnumerable<string> symbols, DateTimeOffset asOf, bool available)
    {
        var start = asOf.AddHours(-1);
        var counts = symbols.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(symbol => symbol, _ => new int[4], StringComparer.OrdinalIgnoreCase);
        if (available)
        {
            foreach (var article in articles)
            {
                if (article.PublishedAtUtc < start || article.PublishedAtUtc > asOf) continue;
                var bucket = article.SentimentScore is not decimal score ? 3 : score > 0.15m ? 0 : score < -0.15m ? 1 : 2;
                foreach (var symbol in article.Symbols.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (counts.TryGetValue(symbol, out var values)) values[bucket]++;
            }
        }
        return counts.ToDictionary(pair => pair.Key, pair => new DeskNewsSentiment(
            pair.Value[0], pair.Value[1], pair.Value[2], pair.Value[3], start, asOf, available), StringComparer.OrdinalIgnoreCase);
    }

    // Reuse stored-story identities so multiple ticker records do not inflate story counts.
    public static IReadOnlyList<DeskArticle> AggregateNews(IEnumerable<PersistedNewsItem> items) => ToArticles(NewsStories.Combine(items));

    // Preserve publication and classification provenance when projecting the deduplicated stored stories.
    private static IReadOnlyList<DeskArticle> ToArticles(IEnumerable<NewsStory> stories) => stories
        .Select(story => new DeskArticle(story.Lead.Headline, story.Lead.Url, story.Lead.Provider, story.Lead.Timestamp, story.Tickers,
            story.Lead.Source, story.Lead.Summary, story.Lead.SentimentScore, NewsEvidenceClassification.Classify(story.Lead)))
        .OrderByDescending(article => article.PublishedAtUtc).ToArray();
}
