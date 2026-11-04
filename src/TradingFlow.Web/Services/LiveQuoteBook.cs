using System.Collections.Concurrent;
using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services;

/// <summary>
/// Latest streamed best bid and offer per displayed symbol from the shared Alpaca SIP connection.
/// Writes come from the stream's read loop and must stay cheap; screens read the newest quote.
/// An older frame never replaces a newer one, an unsubscribed symbol loses its entry, and a lost
/// connection clears the book, so a price shown from here is from a live subscription.
/// </summary>
public sealed class LiveQuoteBook
{
    private readonly ConcurrentDictionary<string, AlpacaLatestQuote> quotes = new(StringComparer.OrdinalIgnoreCase);

    public void Update(AlpacaStreamQuote quote)
    {
        var next = new AlpacaLatestQuote(quote.Symbol, quote.BidPrice, quote.AskPrice, quote.BidSize, quote.AskSize, quote.Timestamp);
        quotes.AddOrUpdate(quote.Symbol, next, (_, current) => current.Timestamp >= next.Timestamp ? current : next);
    }

    public AlpacaLatestQuote? Find(string symbol) => quotes.GetValueOrDefault(symbol);

    public void Remove(string symbol) => quotes.TryRemove(symbol, out _);

    public void Clear() => quotes.Clear();

    /// <summary>The newer of a streamed quote and a polled one; either may be missing.</summary>
    public AlpacaLatestQuote? Newest(string symbol, AlpacaLatestQuote? polled) =>
        Find(symbol) is { } streamed && (polled?.Timestamp is null || streamed.Timestamp > polled.Timestamp) ? streamed : polled;
}
