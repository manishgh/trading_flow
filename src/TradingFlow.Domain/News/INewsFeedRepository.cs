using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.News;

public interface INewsFeedRepository
{
    Task<NewsFeedPage> QueryAsync(NewsFeedQuery query, CancellationToken token) => throw new NotSupportedException();
    /// <summary>The newest matching publication time, or null when none is stored or lookup is unsupported.</summary>
    Task<DateTimeOffset?> FindNewestAsync(NewsFeedQuery filter, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    Task UpsertAsync(IReadOnlyCollection<CatalystEvent> items, CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedNewsItem>> GetRecentAsync(
        DateTimeOffset windowStart,
        int limit,
        string? ticker,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedNewsItem>> GetRecentForTickersAsync(
        DateTimeOffset windowStart,
        int limit,
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PersistedNewsItem>> GetIngestedSinceForTickersAsync(
        DateTimeOffset ingestedSinceUtc,
        int limit,
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);

}
