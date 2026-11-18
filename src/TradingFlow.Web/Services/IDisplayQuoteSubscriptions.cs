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

    /// <summary>
    /// Replaces a scope's symbols but admits a symbol not already in the display union only while that union,
    /// counting every scope, in-flight or acknowledged subscription and the linger set, stays at or below
    /// <paramref name="maximumUnion"/>. Existing owners are never evicted. Returns the admitted symbols.
    /// </summary>
    Task<IReadOnlySet<string>> ReplaceBoundedDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols,
        int maximumUnion, CancellationToken cancellationToken);

    /// <summary>
    /// The provider subscription state for one symbol and, only when Alpaca acknowledged this exact connection's
    /// subscription, the latest quote received under it.
    /// </summary>
    DisplayQuoteReading ReadDisplayQuote(string symbol);
}

/// <summary>Provider subscription state of one display symbol, kept apart from quote age and market session.</summary>
public enum DisplayQuoteState
{
    /// <summary>No leased Alpaca SIP connection is forwarding display quotes (for example, the host's stream is stopped).</summary>
    UpstreamUnavailable,
    /// <summary>No screen currently asks for this symbol on the live connection.</summary>
    NotSubscribed,
    /// <summary>The subscribe command was sent; Alpaca's reply listing the symbol has not been read yet.</summary>
    Pending,
    /// <summary>A provider reply read after the command lists the symbol on the quote channel.</summary>
    Acknowledged,
    /// <summary>Two five-second attempts produced no reply listing the symbol; not retried until membership or connection changes.</summary>
    Unacknowledged
}

/// <summary>A symbol's subscription state and, when acknowledged, its latest quote from that subscription.</summary>
public sealed record DisplayQuoteReading(DisplayQuoteState State, LiveQuoteEntry? Entry);
