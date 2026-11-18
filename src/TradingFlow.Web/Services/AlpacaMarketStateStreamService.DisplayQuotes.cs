using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services;

/// <summary>
/// Display-only quote membership on the shared SIP connection. Every symbol carries the connection and subscription
/// generation it was requested under and whether Alpaca acknowledged it, so a screen never treats a sent command,
/// silence, or a quote from a removed or earlier subscription as a live provider price.
/// </summary>
public sealed partial class AlpacaMarketStateStreamService
{
    // A symbol a screen stopped showing keeps its quote subscription this long, so page changes do not churn it.
    private static readonly TimeSpan DisplayQuoteLinger = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisplayQuoteAcknowledgementTimeout = TimeSpan.FromSeconds(5);
    private const int DisplayQuoteAttempts = 2;
    private readonly Dictionary<Guid, HashSet<string>> displayScopes = [];
    private readonly Dictionary<string, DateTimeOffset> displayLinger = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> displayChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, DisplayQuoteMembership> displayMembership = new(StringComparer.OrdinalIgnoreCase);
    private long displayConnectionGeneration;
    private long activeDisplayConnection;
    private long displaySubscriptionGeneration;

    // One symbol's requested subscription on one connection.
    private sealed record DisplayQuoteMembership(long ConnectionGeneration, long SubscriptionGeneration, DisplayQuoteState State);

    public async Task ReplaceDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        await scopeGate.WaitAsync(cancellationToken);
        try { SetDisplayScope(scopeId, NormalizeDisplaySymbols(symbols)); }
        finally { scopeGate.Release(); }
        displayChanges.Writer.TryWrite(true);
    }

    public async Task<IReadOnlySet<string>> ReplaceBoundedDisplayQuotesAsync(Guid scopeId, IReadOnlyCollection<string> symbols,
        int maximumUnion, CancellationToken cancellationToken)
    {
        var requested = NormalizeDisplaySymbols(symbols);
        HashSet<string> admitted;
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            admitted = AdmitWithinUnion(scopeId, requested, maximumUnion);
            SetDisplayScope(scopeId, admitted);
        }
        finally { scopeGate.Release(); }
        displayChanges.Writer.TryWrite(true);
        return admitted;
    }

    // Symbols already in the union are shared freely; a new symbol is admitted only while the union has room.
    // Called under the scope gate.
    private HashSet<string> AdmitWithinUnion(Guid scopeId, IEnumerable<string> requested, int maximumUnion)
    {
        var union = displayScopes.Values.SelectMany(scope => scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
        union.UnionWith(displayLinger.Keys);
        union.UnionWith(displayMembership.Keys);
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in requested.Order(StringComparer.Ordinal))
        {
            if (!union.Contains(symbol))
            {
                if (union.Count >= maximumUnion) continue;
                union.Add(symbol);
            }
            admitted.Add(symbol);
        }
        return admitted;
    }

    // Symbols leaving a scope linger, so paging back does not churn the provider subscription. Called under the scope gate.
    private void SetDisplayScope(Guid scopeId, HashSet<string> next)
    {
        if (displayScopes.TryGetValue(scopeId, out var previous))
        {
            var until = timeProvider.GetUtcNow() + DisplayQuoteLinger;
            foreach (var symbol in previous.Except(next, StringComparer.OrdinalIgnoreCase)) displayLinger[symbol] = until;
        }
        displayScopes[scopeId] = next;
    }

    private static HashSet<string> NormalizeDisplaySymbols(IReadOnlyCollection<string> symbols) =>
        symbols.Where(symbol => !String.IsNullOrWhiteSpace(symbol)).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task RemoveDisplayQuotesAsync(Guid scopeId, CancellationToken cancellationToken)
    {
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            if (displayScopes.Remove(scopeId, out var previous))
            {
                foreach (var symbol in previous) displayLinger[symbol] = timeProvider.GetUtcNow() + DisplayQuoteLinger;
            }
        }
        finally
        {
            scopeGate.Release();
        }

        displayChanges.Writer.TryWrite(true);
    }

    /// <summary>Symbols currently shown on a screen plus those still inside their linger window.</summary>
    internal async Task<IReadOnlySet<string>> GetDesiredDisplayQuotesAsync(CancellationToken cancellationToken)
    {
        await scopeGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            foreach (var expired in displayLinger.Where(item => item.Value <= now).Select(item => item.Key).ToArray()) displayLinger.Remove(expired);
            var desired = displayScopes.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.OrdinalIgnoreCase);
            desired.UnionWith(displayLinger.Keys);
            return desired;
        }
        finally
        {
            scopeGate.Release();
        }
    }

    public DisplayQuoteReading ReadDisplayQuote(string symbol)
    {
        var connection = Interlocked.Read(ref activeDisplayConnection);
        if (connection == 0) return new(DisplayQuoteState.UpstreamUnavailable, null);
        if (!displayMembership.TryGetValue(symbol, out var membership) || membership.ConnectionGeneration != connection)
            return new(DisplayQuoteState.NotSubscribed, null);
        if (membership.State != DisplayQuoteState.Acknowledged) return new(membership.State, null);
        var entry = quotes.FindEntry(symbol);
        return entry is not null && entry.ConnectionGeneration == connection &&
               entry.SubscriptionGeneration == membership.SubscriptionGeneration
            ? new(DisplayQuoteState.Acknowledged, entry)
            : new(DisplayQuoteState.Acknowledged, null);
    }

    // Called from the read loop: keep only quotes for a symbol requested on this connection, stamped with its generation.
    private void AcceptDisplayQuote(AlpacaStreamQuote quote, DateTimeOffset receivedAtUtc)
    {
        var connection = Interlocked.Read(ref activeDisplayConnection);
        if (connection != 0 && displayMembership.TryGetValue(quote.Symbol, out var membership) &&
            membership.ConnectionGeneration == connection)
            quotes.Update(quote, connection, membership.SubscriptionGeneration, receivedAtUtc);
    }

    // Quote-only subscriptions for displayed symbols. Failures are logged and retried on the next change;
    // they never fault the connection, and symbols dropped here lose their book entry so no stale price lingers.
    private async Task ForwardDisplayQuotesAsync(IAlpacaMarketStateStreamClient stream, CancellationToken cancellationToken)
    {
        var connection = Interlocked.Increment(ref displayConnectionGeneration);
        Interlocked.Exchange(ref activeDisplayConnection, connection);
        var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var desired = await GetDesiredDisplayQuotesAsync(cancellationToken);
                    var additions = desired.Except(subscribed, StringComparer.OrdinalIgnoreCase).ToArray();
                    var removals = subscribed.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var removal in removals)
                    {
                        displayMembership.TryRemove(removal, out _);
                        quotes.Remove(removal);
                    }
                    if (additions.Length + removals.Length > 0)
                        await ApplyDisplayQuoteChangeAsync(stream, connection, additions, removals, cancellationToken);
                    subscribed = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Display quote subscription update failed; screens fall back to REST quotes.");
                }

                // Wake on the next screen change, or after the linger window to drop expired symbols.
                await Task.WhenAny(displayChanges.Reader.WaitToReadAsync(cancellationToken).AsTask(),
                    Task.Delay(TimeSpan.FromSeconds(30), timeProvider, cancellationToken));
                while (displayChanges.Reader.TryRead(out _)) { }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref activeDisplayConnection, 0, connection);
            foreach (var item in displayMembership.Where(item => item.Value.ConnectionGeneration == connection).ToArray())
                displayMembership.TryRemove(item);
        }
    }

    // Send the change and wait for a provider reply that shows it. Reissue once after a five-second silence, then
    // record the additions as unacknowledged instead of replacing the socket for a display-only failure.
    private async Task ApplyDisplayQuoteChangeAsync(IAlpacaMarketStateStreamClient stream, long connection,
        IReadOnlyCollection<string> additions, IReadOnlyCollection<string> removals, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref displaySubscriptionGeneration);
        foreach (var symbol in additions)
            displayMembership[symbol] = new(connection, generation, DisplayQuoteState.Pending);
        for (var attempt = 1; ; attempt++)
        {
            var boundary = stream.CurrentArrivalOrdinal;
            if (removals.Count > 0) await stream.UnsubscribeQuotesAsync(removals, cancellationToken);
            if (additions.Count > 0) await stream.SubscribeQuotesAsync(additions, cancellationToken);
            var confirmed = await stream.WaitForQuoteSubscriptionAsync(boundary, additions, removals,
                DisplayQuoteAcknowledgementTimeout, cancellationToken);
            if (confirmed == true)
            {
                MarkDisplayQuotes(additions, generation, DisplayQuoteState.Acknowledged);
                return;
            }
            if (confirmed is null || attempt >= DisplayQuoteAttempts)
            {
                MarkDisplayQuotes(additions, generation, DisplayQuoteState.Unacknowledged);
                logger.LogWarning("Alpaca did not acknowledge display quote subscriptions after {Attempts} attempt(s). Symbols={Symbols}",
                    attempt, String.Join(",", additions));
                return;
            }
        }
    }

    // Update only entries still owned by this exact subscription; a newer request for the symbol keeps its own state.
    private void MarkDisplayQuotes(IEnumerable<string> symbols, long generation, DisplayQuoteState state)
    {
        foreach (var symbol in symbols)
            if (displayMembership.TryGetValue(symbol, out var current) && current.SubscriptionGeneration == generation)
                displayMembership.TryUpdate(symbol, current with { State = state }, current);
    }
}
