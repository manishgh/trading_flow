namespace TradingFlow.Web.Services;

/// <summary>
/// Quote-only subscriptions for screens that display prices. They ride the shared Alpaca SIP connection
/// but never warm, evict or acknowledge execution market state, so opening or paging a screen cannot
/// disturb trading data. Removed symbols linger briefly so page changes do not churn the subscription.
/// </summary>
public interface IDisplayQuoteSubscriptions
{
    Task ReplaceDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken);

    Task RemoveDisplayQuotesAsync(Guid scopeId, CancellationToken cancellationToken);
}
