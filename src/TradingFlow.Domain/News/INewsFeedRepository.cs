using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.News;

public interface INewsFeedRepository
{
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

    Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
