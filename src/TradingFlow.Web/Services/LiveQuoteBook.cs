using System.Collections.Concurrent;
using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services;

/// <summary>One streamed quote with the connection and subscription it arrived under and when it was read.</summary>
public sealed record LiveQuoteEntry(AlpacaLatestQuote Quote, long ConnectionGeneration, long SubscriptionGeneration,
    DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Latest streamed best bid and offer per displayed symbol from the shared Alpaca SIP connection.
/// Writes come from the stream's read loop and must stay cheap; screens read the newest quote.
/// Within one subscription an older frame never replaces a newer one; a newer subscription replaces
/// whatever an earlier one left. A provider clock more than five seconds ahead of receipt is refused,
/// so a bad timestamp cannot pin the displayed price. An unsubscribed symbol loses its entry, and a lost
/// connection clears the book, so a price shown from here is from a live subscription.
/// </summary>
public sealed class LiveQuoteBook
{
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, LiveQuoteEntry> quotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stores the quote unless its provider clock is implausibly ahead of receipt or it is older within the same subscription.</summary>
    public bool Update(AlpacaStreamQuote quote, long connectionGeneration, long subscriptionGeneration, DateTimeOffset receivedAtUtc)
    {
        if (quote.Timestamp > receivedAtUtc + MaximumFutureSkew) return false;
        var next = new LiveQuoteEntry(
            new AlpacaLatestQuote(quote.Symbol, quote.BidPrice, quote.AskPrice, quote.BidSize, quote.AskSize, quote.Timestamp),
            connectionGeneration, subscriptionGeneration, receivedAtUtc);
        quotes.AddOrUpdate(quote.Symbol, next, (_, current) => Keep(current, next) ? current : next);
        return true;
    }

    // An entry from an older connection or subscription never outranks a newer one; within one, the newer provider time wins.
    private static bool Keep(LiveQuoteEntry current, LiveQuoteEntry next) =>
        (current.ConnectionGeneration, current.SubscriptionGeneration) == (next.ConnectionGeneration, next.SubscriptionGeneration)
            ? current.Quote.Timestamp >= next.Quote.Timestamp
            : current.ConnectionGeneration > next.ConnectionGeneration ||
              (current.ConnectionGeneration == next.ConnectionGeneration && current.SubscriptionGeneration > next.SubscriptionGeneration);

    public AlpacaLatestQuote? Find(string symbol) => quotes.GetValueOrDefault(symbol)?.Quote;

    /// <summary>The stored quote together with the subscription identity it was received under.</summary>
    public LiveQuoteEntry? FindEntry(string symbol) => quotes.GetValueOrDefault(symbol);

    public void Remove(string symbol) => quotes.TryRemove(symbol, out _);

    public void Clear() => quotes.Clear();

    /// <summary>The newer of a streamed quote and a polled one; either may be missing.</summary>
    public AlpacaLatestQuote? Newest(string symbol, AlpacaLatestQuote? polled) =>
        Find(symbol) is { } streamed && (polled?.Timestamp is null || streamed.Timestamp > polled.Timestamp) ? streamed : polled;
}
