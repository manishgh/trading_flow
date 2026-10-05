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
        var since = filter.SinceUtc.ToUniversalTime();
        var query = db.NewsItems.AsNoTracking().Where(x => x.Timestamp >= since);
        if (filter.ScopeTickers is { } tickers) query = query.Where(x => tickers.Contains(x.Ticker));
        if (!String.IsNullOrWhiteSpace(filter.Ticker)) {
            var ticker = filter.Ticker.Trim().ToUpperInvariant();
            query = query.Where(x => x.Ticker == ticker || x.Ticker == "MARKET");
        }
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
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.Timestamp).ThenBy(x => x.Id)
            .Skip(Math.Clamp(filter.Skip, 0, Int32.MaxValue - 200)).Take(Math.Clamp(filter.Take, 1, 200)).ToArrayAsync(token);
        return new(rows, total);
    }
    private readonly IDbContextFactory<TradingFlowDbContext> dbFactory;

    public SqliteNewsFeedRepository(IDbContextFactory<TradingFlowDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    public async Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var id = BuildId(item);
            var publishedAtUtc = item.Timestamp.ToUniversalTime();
            var ingestedAtUtc = (item.ReceivedAt ?? now).ToUniversalTime();
            var existing = await db.NewsItems.FindAsync([id], cancellationToken);
            if (existing is null)
            {
                db.NewsItems.Add(new PersistedNewsItem
                {
                    Id = id,
                    Ticker = item.Ticker.ToUpperInvariant(),
                    Timestamp = publishedAtUtc,
                    Headline = item.Headline,
                    SentimentScore = item.SentimentScore,
                    Provider = item.Provider ?? "unknown",
                    Source = item.Source,
                    Url = item.Url,
                    Summary = item.Summary,
                    IngestedAt = ingestedAtUtc
                });
            }
            else
            {
                existing.SentimentScore = item.SentimentScore;
                existing.Source = item.Source;
                existing.Url = item.Url;
                existing.Summary = item.Summary;
                existing.Timestamp = publishedAtUtc;
                existing.IngestedAt = ingestedAtUtc;
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

    public async Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var normalizedCutoff = cutoff.ToUniversalTime();
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var staleItems = await db.NewsItems
            .Where(item => item.Timestamp < normalizedCutoff)
            .Take(1000)
            .ToArrayAsync(cancellationToken);
        if (staleItems.Length == 0)
        {
            return;
        }

        db.NewsItems.RemoveRange(staleItems);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildId(CatalystEvent item)
    {
        var provider = item.Provider ?? "unknown";
        var key = !String.IsNullOrWhiteSpace(item.ExternalId)
            ? item.ExternalId
            : $"{item.Ticker}|{item.Timestamp.UtcDateTime:O}|{item.Url ?? item.Headline}";
        return $"{provider}:{key}".ToLowerInvariant();
    }
}
