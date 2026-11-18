namespace TradingFlow.Web.Services.Desk;

/// <summary>
/// One stock's display state in a quote frame. <see cref="State"/> is the provider subscription state, kept apart
/// from the quote's age: <c>acknowledged</c>, <c>pending</c>, <c>unacknowledged</c>, <c>not_subscribed</c>,
/// <c>upstream_unavailable</c> or <c>refused</c> (over the shared display limit). Prices are present only for an
/// acknowledged subscription. <see cref="MidPrice"/> is the bid/ask midpoint, not a last-trade price.
/// </summary>
public sealed record DeskQuoteItem(string Symbol, string State, decimal? BidPrice = null, decimal? AskPrice = null,
    decimal? MidPrice = null, decimal? BidSize = null, decimal? AskSize = null, DateTimeOffset? Timestamp = null,
    DateTimeOffset? ReceivedAtUtc = null, string? Feed = null);

/// <summary>
/// A browser quote frame. <c>quotes</c> frames carry only changed stocks and stocks that left the view; an
/// <c>ended</c> frame says why the watch stopped. <see cref="Upstream"/> is <c>connected</c> while a leased
/// Alpaca SIP connection forwards display quotes, otherwise <c>unavailable</c>.
/// </summary>
public sealed record DeskQuoteFrame(string Kind, Guid View, long Revision, long Sequence, string Upstream,
    IReadOnlyList<DeskQuoteItem> Quotes, IReadOnlyList<string> Removed, string? Reason = null);

/// <summary>
/// Change detection for one bound socket. Each call compares the view's current stocks with what this socket last
/// sent and returns a frame only when something changed. A quote whose provider clock is more than five seconds
/// ahead of now is withheld again at emission, even though storage already refuses such quotes.
/// </summary>
public sealed class DeskQuoteWatch(Guid viewId)
{
    private readonly Dictionary<string, DeskQuoteItem> sent = new(StringComparer.Ordinal);
    private long sequence;
    private long revision = -1;
    private string upstream = "";

    /// <summary>Builds the next frame from the view's stocks and the provider readings, or null when nothing changed.</summary>
    public DeskQuoteFrame? Next(DeskQuoteViewState view, Func<string, DisplayQuoteReading> read, DateTimeOffset now)
    {
        var items = new List<DeskQuoteItem>();
        var current = new HashSet<string>(StringComparer.Ordinal);
        var unavailable = false;
        foreach (var symbol in view.Symbols)
        {
            current.Add(symbol);
            var reading = read(symbol);
            unavailable |= reading.State == DisplayQuoteState.UpstreamUnavailable;
            Collect(items, Describe(symbol, reading, now));
        }
        foreach (var symbol in view.Refused)
        {
            current.Add(symbol);
            Collect(items, new DeskQuoteItem(symbol, "refused"));
        }
        var removed = sent.Keys.Where(symbol => !current.Contains(symbol)).ToArray();
        foreach (var symbol in removed) sent.Remove(symbol);
        var nextUpstream = view.Symbols.Count == 0 ? upstream : unavailable ? "unavailable" : "connected";
        if (items.Count == 0 && removed.Length == 0 && nextUpstream == upstream && view.Revision == revision) return null;
        upstream = nextUpstream;
        revision = view.Revision;
        return new("quotes", viewId, view.Revision, ++sequence, upstream, items, removed);
    }

    /// <summary>The final frame, naming why this socket stops so the page can say so instead of guessing from silence.</summary>
    public DeskQuoteFrame End(string reason) => new("ended", viewId, revision, ++sequence, upstream, [], [], reason);

    // Add the item only when it differs from what this socket last sent for the stock.
    private void Collect(List<DeskQuoteItem> items, DeskQuoteItem item)
    {
        if (sent.TryGetValue(item.Symbol, out var previous) && previous == item) return;
        sent[item.Symbol] = item;
        items.Add(item);
    }

    // Map one reading to its wire form; prices only from this exact acknowledged subscription and a plausible clock.
    internal static DeskQuoteItem Describe(string symbol, DisplayQuoteReading reading, DateTimeOffset now)
    {
        var state = reading.State switch
        {
            DisplayQuoteState.Acknowledged => "acknowledged",
            DisplayQuoteState.Pending => "pending",
            DisplayQuoteState.Unacknowledged => "unacknowledged",
            DisplayQuoteState.NotSubscribed => "not_subscribed",
            _ => "upstream_unavailable"
        };
        if (reading.Entry is not { } entry || entry.Quote.Timestamp is not { } at || at > now + LiveQuoteBook.MaximumFutureSkew)
            return new(symbol, state);
        var quote = entry.Quote;
        return new(symbol, state, quote.BidPrice, quote.AskPrice, quote.MidPrice, quote.BidSize, quote.AskSize,
            at, entry.ReceivedAtUtc, quote.Feed);
    }
}
