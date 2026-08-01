using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;

namespace TradingFlow.Data.News;

public sealed class SqliteNewsFeedRepository : INewsFeedRepository
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
