using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;

namespace TradingFlow.Data.News;

public sealed class SqliteNewsFeedRepository
{
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
            var existing = await db.NewsItems.FindAsync([id], cancellationToken);
            if (existing is null)
            {
                db.NewsItems.Add(new PersistedNewsItem
                {
                    Id = id,
                    Ticker = item.Ticker.ToUpperInvariant(),
                    Timestamp = item.Timestamp,
                    Headline = item.Headline,
                    SentimentScore = item.SentimentScore,
                    Provider = item.Provider ?? "unknown",
                    Source = item.Source,
                    Url = item.Url,
                    Summary = item.Summary,
                    IngestedAt = item.ReceivedAt ?? now
                });
            }
            else
            {
                existing.SentimentScore = item.SentimentScore;
                existing.Source = item.Source;
                existing.Url = item.Url;
                existing.Summary = item.Summary;
                existing.IngestedAt = item.ReceivedAt ?? now;
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
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.NewsItems
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        var query = items
            .Where(item => item.Timestamp >= windowStart);

        if (!String.IsNullOrWhiteSpace(ticker))
        {
            var normalizedTicker = ticker.Trim().ToUpperInvariant();
            query = query.Where(item => item.Ticker == normalizedTicker || item.Ticker == "MARKET");
        }

        return query
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Provider)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();
    }

    public async Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allItems = await db.NewsItems.ToArrayAsync(cancellationToken);
        var staleItems = allItems
            .Where(item => item.Timestamp < cutoff)
            .Take(1000)
            .ToArray();
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
