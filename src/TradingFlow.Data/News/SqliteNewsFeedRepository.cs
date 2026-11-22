using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;

namespace TradingFlow.Data.News;

public sealed class SqliteNewsFeedRepository : INewsFeedRepository
{
    public async Task<NewsFeedPage> QueryAsync(NewsFeedQuery filter, CancellationToken token)
    {
        using var db = await dbFactory.CreateDbContextAsync(token);
        var query = Filter(db, filter);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.Timestamp).ThenBy(x => x.Id)
            .Skip(Math.Clamp(filter.Skip, 0, Int32.MaxValue - 200)).Take(Math.Clamp(filter.Take, 1, 200)).ToArrayAsync(token);
        return new(rows, total);
    }
    // Applies every News filter except paging; shared by the page query and the newest-article lookup.
    private static IQueryable<PersistedNewsItem> Filter(TradingFlowDbContext db, NewsFeedQuery filter)
    {
        var since = filter.SinceUtc.ToUniversalTime();
        var query = db.NewsItems.AsNoTracking().Where(x => x.Timestamp >= since);
        if (filter.UntilUtc is { } until) {
            var cutoff = until.ToUniversalTime();
            query = query.Where(x => x.Timestamp <= cutoff);
        }
        var includeMarket = filter.IncludeMarketNews && String.IsNullOrWhiteSpace(filter.Ticker) && !filter.StockLinkedOnly;
        if (filter.ScopeTickers is { } tickers)
            query = query.Where(x => tickers.Contains(x.Ticker) || (includeMarket && x.Ticker == "MARKET"));
        if (!String.IsNullOrWhiteSpace(filter.Ticker)) {
            var ticker = filter.Ticker.Trim().ToUpperInvariant();
            query = query.Where(x => x.Ticker == ticker);
        }
        if (filter.StockLinkedOnly) query = query.Where(x => x.Ticker != "MARKET");
        if (!String.IsNullOrWhiteSpace(filter.Category)) query = query.Where(NewsDisplayTaxonomy.CategoryPredicate(filter.Category));
        if (!String.IsNullOrWhiteSpace(filter.Sector)) query = query.Where(NewsDisplayTaxonomy.SectorPredicate(filter.Sector));
        if (!String.IsNullOrWhiteSpace(filter.Provider)) {
            var provider = filter.Provider.Trim().ToLowerInvariant(); query = query.Where(x => x.Provider.ToLower() == provider);
        }
        if (!String.IsNullOrWhiteSpace(filter.Source)) {
            var source = filter.Source.Trim().ToLowerInvariant(); query = query.Where(x => x.Source != null && x.Source.ToLower() == source);
        }
        if (!String.IsNullOrWhiteSpace(filter.Search)) {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Headline.ToLower().Contains(term) || (x.Summary != null && x.Summary.ToLower().Contains(term)) || x.Ticker.ToLower().Contains(term));
        }
        query = filter.Sentiment?.ToLowerInvariant() switch {
            "positive" => query.Where(x => x.SentimentScore > 0.15m),
            "negative" => query.Where(x => x.SentimentScore < -0.15m),
            "neutral" => query.Where(x => x.SentimentScore >= -0.15m && x.SentimentScore <= 0.15m),
            _ => query
        };
        if (filter.LinkedOnly) query = query.Where(x => x.Url != null && x.Url != "");
        if (!String.IsNullOrWhiteSpace(filter.Classification)) {
            var classification = filter.Classification.Trim().ToLowerInvariant();
            var words = NewsEvidenceClassification.MacroKeywords;
            query = query.Where(x => (x.Provider.ToLower() == "sec_edgar" ? "sec filing" :
                x.Provider.ToLower() == "federal_reserve" ? "fed" : x.Ticker.ToUpper() != "MARKET" ? "company" :
                words.Any(word => x.Headline.ToLower().Contains(word)) ? "macro" : "market") == classification);
        }
        return query;
    }

    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;

    public SqliteNewsFeedRepository(IDbContextFactory<TradingFlowDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    /// <summary>The scorer recorded for the in-process word-list scorer used while FinBERT has not scored an article.</summary>
    public const string FallbackScorer = "vader";

    /// <summary>The scorer recorded for an article stored before its first scoring finished.</summary>
    public const string PendingScorer = "pending";

    /// <summary>
    /// Stores articles whose ID is not stored yet and returns the IDs it added; existing rows are never changed. If the live
    /// feed adds one of them at the same moment, the insert is retried once without it.
    /// </summary>
    public async Task<IReadOnlySet<string>> InsertNewAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var byId = items.GroupBy(StorageId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var ids = byId.Keys.ToArray();
            var stored = (await db.NewsItems.Where(row => ids.Contains(row.Id)).Select(row => row.Id).ToArrayAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            var added = byId.Where(pair => !stored.Contains(pair.Key)).ToArray();
            db.NewsItems.AddRange(added.Select(pair => NewRow(pair.Key, pair.Value, pair.Value.ClassificationVersion ?? PendingScorer)));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return added.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // The live feed stored one of these IDs between the lookup and the save; look again.
            }
        }
    }

    /// <summary>
    /// Writes score, scorer and summary onto rows this refresh added, one conditional update per row: only while the receipt
    /// time still matches (the live feed has not replaced it since), and a fallback score never replaces a FinBERT score.
    /// Receipt and publication times, headline and link are not touched.
    /// </summary>
    public async Task ApplyEnrichmentAsync(IReadOnlyCollection<CatalystEvent> items, IReadOnlySet<string> addedIds, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var item in items)
        {
            var id = StorageId(item);
            if (!addedIds.Contains(id)) continue;
            var scorer = item.ClassificationVersion ?? PendingScorer;
            var isWeak = scorer is FallbackScorer or PendingScorer;
            var rows = db.NewsItems.Where(row => row.Id == id &&
                (!isWeak || row.SentimentAnalyzer == FallbackScorer || row.SentimentAnalyzer == PendingScorer));
            if (item.ReceivedAt?.ToUniversalTime() is { } received) rows = rows.Where(row => row.IngestedAt == received);
            await rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.SentimentScore, item.SentimentScore)
                .SetProperty(row => row.SentimentAnalyzer, scorer)
                .SetProperty(row => row.Summary, item.Summary), cancellationToken);
        }
    }

    /// <summary>Reads only the identifying fields of every item published since <paramref name="sinceUtc"/>, without a row limit.</summary>
    public async Task<IReadOnlyList<PersistedNewsItem>> ListIdentitiesSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var since = sinceUtc.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsItems.AsNoTracking().Where(row => row.Timestamp >= since)
            .Select(row => new PersistedNewsItem { Id = row.Id, Provider = row.Provider, Ticker = row.Ticker, Url = row.Url, Headline = row.Headline })
            .ToArrayAsync(cancellationToken);
    }

    // Builds one new row from a provider article.
    private static PersistedNewsItem NewRow(string id, CatalystEvent item, string? scorer) => new()
    {
        Id = id,
        Ticker = item.Ticker.ToUpperInvariant(),
        Timestamp = item.Timestamp.ToUniversalTime(),
        Headline = item.Headline,
        SentimentScore = item.SentimentScore,
        Provider = item.Provider ?? "unknown",
        Source = item.Source,
        Url = item.Url,
        Summary = item.Summary,
        IngestedAt = (item.ReceivedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        SentimentAnalyzer = scorer
    };

    /// <summary>
    /// Stores or replaces articles from the live feed. If the refresh adds one of them at the same moment, the write is
    /// retried once and then updates that row.
    /// </summary>
    public async Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken)
    {
        try { await UpsertOnceAsync(items, cancellationToken); }
        catch (DbUpdateException) { await UpsertOnceAsync(items, cancellationToken); }
    }

    // One attempt at storing or replacing the given articles.
    private async Task UpsertOnceAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var id = StorageId(item);
            var publishedAtUtc = item.Timestamp.ToUniversalTime();
            var ingestedAtUtc = (item.ReceivedAt ?? now).ToUniversalTime();
            var existing = await db.NewsItems.FindAsync([id], cancellationToken);
            if (existing is null)
            {
                db.NewsItems.Add(NewRow(id, item, item.ClassificationVersion));
            }
            else
            {
                existing.SentimentScore = item.SentimentScore;
                existing.Source = item.Source;
                existing.Url = item.Url;
                existing.Summary = item.Summary;
                existing.Timestamp = publishedAtUtc;
                existing.IngestedAt = ingestedAtUtc;
                existing.SentimentAnalyzer = item.ClassificationVersion;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedNewsItem>> GetRecentAsync(
        DateTimeOffset windowStart,
        int limit,
        string? ticker,
        CancellationToken cancellationToken)
    {
        var normalizedWindowStart = windowStart.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.NewsItems
            .AsNoTracking()
            .Where(item => item.Timestamp >= normalizedWindowStart);

        if (!String.IsNullOrWhiteSpace(ticker))
        {
            var normalizedTicker = ticker.Trim().ToUpperInvariant();
            query = query.Where(item => item.Ticker == normalizedTicker || item.Ticker == "MARKET");
        }

        return await query
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Provider)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedNewsItem>> GetRecentForTickersAsync(
        DateTimeOffset windowStart,
        int limit,
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        var normalized = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return Array.Empty<PersistedNewsItem>();
        }

        var normalizedWindowStart = windowStart.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsItems
            .AsNoTracking()
            .Where(item => item.Timestamp >= normalizedWindowStart && normalized.Contains(item.Ticker))
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Provider)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedNewsItem>> GetIngestedSinceForTickersAsync(
        DateTimeOffset ingestedSinceUtc,
        int limit,
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        var normalized = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return [];
        }

        var cutoff = ingestedSinceUtc.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsItems
            .AsNoTracking()
            .Where(item => item.IngestedAt >= cutoff && normalized.Contains(item.Ticker))
            .OrderByDescending(item => item.IngestedAt)
            .ThenByDescending(item => item.Timestamp)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the newest items since <paramref name="sinceUtc"/> whose recorded scorer is one of <paramref name="scorers"/>.
    /// Rows without a recorded scorer (stored before it was recorded) never match.
    /// </summary>
    public async Task<IReadOnlyList<PersistedNewsItem>> GetScoredByAsync(
        IReadOnlyCollection<string> scorers, DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken)
    {
        var since = sinceUtc.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsItems.AsNoTracking()
            .Where(item => item.SentimentAnalyzer != null && scorers.Contains(item.SentimentAnalyzer) && item.Timestamp >= since)
            .OrderByDescending(item => item.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces one item's score and scorer only while its receipt time and scorer are still those read, so a newer live
    /// update is never overwritten. Headline, link, summary and times stay unchanged. Returns true when it was replaced.
    /// </summary>
    public async Task<bool> UpdateSentimentAsync(PersistedNewsItem read, decimal score, string scorer, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NewsItems
            .Where(row => row.Id == read.Id && row.SentimentAnalyzer == read.SentimentAnalyzer && row.IngestedAt == read.IngestedAt)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.SentimentScore, score)
                .SetProperty(row => row.SentimentAnalyzer, scorer), cancellationToken) > 0;
    }

    /// <summary>The publication time of the newest item matching the filter, or null; counts nothing.</summary>
    public async Task<DateTimeOffset?> FindNewestAsync(NewsFeedQuery filter, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Filter(db, filter).OrderByDescending(row => row.Timestamp)
            .Select(row => (DateTimeOffset?)row.Timestamp).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The stored row's identity: provider and article ID, else provider, stock, time and link or headline.</summary>
    public static string StorageId(CatalystEvent item)
    {
        var provider = item.Provider ?? "unknown";
        var key = !String.IsNullOrWhiteSpace(item.ExternalId)
            ? item.ExternalId
            : $"{item.Ticker}|{item.Timestamp.UtcDateTime:O}|{item.Url ?? item.Headline}";
        return $"{provider}:{key}".ToLowerInvariant();
    }
}
