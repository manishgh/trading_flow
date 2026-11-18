using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TradingFlow.Alpaca;

public sealed class AlpacaStreamClient : IAlpacaMarketStateStreamClient
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    // Every provider reply is queued; quote-command replies between execution commands are skipped by the next
    // execution wait, and the oldest entries are dropped beyond this bound so the queue cannot grow without limit.
    private const int MaximumQueuedAcknowledgements = 64;
    private readonly Channel<(long Ordinal, DateTimeOffset Received, SubscriptionAcknowledgement Acknowledgement)> subscriptionAcknowledgements =
        Channel.CreateBounded<(long Ordinal, DateTimeOffset Received, SubscriptionAcknowledgement Acknowledgement)>(
        new BoundedChannelOptions(MaximumQueuedAcknowledgements)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly AlpacaOptions _options;
    private readonly Uri _streamUrl;
    private readonly ILogger<AlpacaStreamClient>? _logger;
    private IAlpacaMarketDataReceiptObserver? receiptObserver;
    private string? diagnosticSymbol;
    private long currentArrivalOrdinal;
    // Taken just before the latest bars/trades/statuses command was sent: the arrival ordinal, and the provider's
    // execution channels as last reported. Published together so a waiter never mixes two commands' boundaries.
    private MarketCommandBoundary marketBoundary = new(0, SubscriptionAcknowledgement.Empty);
    // Newest provider subscription reply of any kind, published whole so readers never see a half-updated set.
    private SubscriptionObservation? latestObservation;
    private TaskCompletionSource observationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AlpacaStreamClient(AlpacaOptions options, ILogger<AlpacaStreamClient>? logger = null)
    {
        _options = options;
        _streamUrl = options.ResolveMarketDataStreamUrl();
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Connecting to Alpaca {MarketDataFeed} market-data stream at {StreamUrl}.",
            _options.ResolveMarketDataFeed(),
            _streamUrl);
        await _webSocket.ConnectAsync(_streamUrl, cancellationToken);

        // Alpaca sends a connection acknowledgement before accepting authentication.
        _ = await ReceiveMessageAsync(cancellationToken);

        // Send Auth
        var authPayload = new
        {
            action = "auth",
            key = _options.KeyId,
            secret = _options.SecretKey
        };
        await SendMessageAsync(authPayload, cancellationToken);

        // Wait for auth response
        var authResponse = await ReceiveMessageAsync(cancellationToken);
        if (!IsAuthorizedResponse(authResponse))
        {
            throw new InvalidOperationException("Alpaca market-data WebSocket authentication was rejected.");
        }
    }

    public Task SubscribeBarsAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken)
    {
        var subPayload = new
        {
            action = "subscribe",
            bars = tickers
        };
        return SendMessageAsync(subPayload, cancellationToken);
    }

    /// <summary>
    /// Adds symbols to the shared market-state subscription. The acknowledgement is
    /// consumed by the normal read loop so this method never races a second receive.
    /// </summary>
    public Task SubscribeMarketStateAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        var symbols = tickers
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (symbols.Length == 0)
        {
            return Task.CompletedTask;
        }

        MarkMarketCommand();
        return SendMessageAsync(
            new
            {
                action = "subscribe",
                bars = symbols,
                updatedBars = symbols,
                trades = symbols,
                statuses = symbols
            },
            cancellationToken);
    }

    /// <summary>
    /// Adds display-only quote subscriptions. They never take part in the market-state acknowledgement,
    /// so a rejected or slow quote subscription cannot reset the execution connection.
    /// </summary>
    public Task SubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) =>
        SendChannelAsync("subscribe", tickers, cancellationToken);

    public Task UnsubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) =>
        SendChannelAsync("unsubscribe", tickers, cancellationToken);
    // Quote commands need no bookkeeping: their replies are queued like any other and recognised by the execution
    // wait from their unchanged execution channels.
    private Task SendChannelAsync(string action, IReadOnlyCollection<string> tickers, CancellationToken cancellationToken)
    {
        var symbols = tickers
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return symbols.Length == 0 ? Task.CompletedTask : SendMessageAsync(new { action, quotes = symbols }, cancellationToken);
    }

    // Record the boundary before an execution command: replies read up to here answer earlier commands, and a later
    // reply still showing these execution channels answers a quote command, not this one.
    // The ordinal is read first so a reply published in between is both considered and part of the prior state.
    internal void MarkMarketCommand()
    {
        var ordinal = CurrentArrivalOrdinal;
        var before = Volatile.Read(ref latestObservation)?.Acknowledgement ?? SubscriptionAcknowledgement.Empty;
        Volatile.Write(ref marketBoundary, new MarketCommandBoundary(ordinal, before));
    }

    // Replies queued and not yet read by an execution wait.
    internal int PendingExecutionAcknowledgements => subscriptionAcknowledgements.Reader.Count;

    // Test seam for the arrival ordinal normally advanced by the read loop.
    internal void SetArrivalOrdinal(long ordinal) => Interlocked.Exchange(ref currentArrivalOrdinal, ordinal);

    /// <summary>
    /// Waits on published replies only: the quote-channel set must contain every subscribed symbol and no removed one,
    /// in a reply read after the command boundary. The execution acknowledgement queue is never read here.
    /// </summary>
    public async Task<bool?> WaitForQuoteSubscriptionAsync(long afterArrivalOrdinal, IReadOnlyCollection<string> subscribed,
        IReadOnlyCollection<string> removed, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var changed = Volatile.Read(ref observationChanged).Task;
                if (Volatile.Read(ref latestObservation) is { } latest && latest.ArrivalOrdinal > afterArrivalOrdinal &&
                    subscribed.All(symbol => latest.Acknowledgement.Quotes.Contains(symbol)) &&
                    !removed.Any(symbol => latest.Acknowledgement.Quotes.Contains(symbol)))
                    return true;
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }


    /// <summary>
    /// Removes symbols from every channel owned by the shared connection. The
    /// single read loop consumes Alpaca acknowledgements.
    /// </summary>
    public Task UnsubscribeMarketStateAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        var symbols = tickers
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (symbols.Length == 0)
        {
            return Task.CompletedTask;
        }

        MarkMarketCommand();
        return SendMessageAsync(
            new
            {
                action = "unsubscribe",
                bars = symbols,
                updatedBars = symbols,
                trades = symbols,
                statuses = symbols
            },
            cancellationToken);
    }

    public DateTimeOffset? CurrentBatchReceivedAtUtc { get; private set; }
    public long CurrentArrivalOrdinal => Interlocked.Read(ref currentArrivalOrdinal);

    // Only the single connection owner installs an observer, before the market-data receive loop.
    public bool TryInstallReceiptObserver(IAlpacaMarketDataReceiptObserver observer)
    {
        receiptObserver = observer;
        return true;
    }

    // Every item in one WebSocket message shares its original receipt time before JSON decoding.
    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                break;
            }

            CurrentBatchReceivedAtUtc = TimeProvider.System.GetUtcNow();
            var batch = ms.GetBuffer().AsMemory(0, checked((int)ms.Length));
            using var doc = JsonDocument.Parse(batch);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var offset = receiptObserver is not null ? batch.Span.IndexOf((byte)'[') + 1 : -1;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var ordinal = Interlocked.Increment(ref currentArrivalOrdinal);
                    if (receiptObserver is { IsCapturing: true } observer)
                        offset = ObserveEvent(observer, element, batch.Span, offset, CurrentBatchReceivedAtUtc.Value, ordinal);
                    else if (receiptObserver is not null)
                        offset = -1;
                    var clone = element.Clone();
                    QueueSubscriptionAcknowledgement(clone, ordinal, CurrentBatchReceivedAtUtc.Value);

                    yield return clone;
                }
            }
        }
    }

    // Route only subscription replies through the single command reader, with their original receive identity.
    // Every reply is also published whole for quote confirmation and for the next execution command's boundary.
    internal void QueueSubscriptionAcknowledgement(JsonElement element, long ordinal, DateTimeOffset received)
    {
        if (!TryParseSubscriptionAcknowledgement(element, out var acknowledgement)) return;
        PublishObservation(new(ordinal, received, acknowledgement!));
        subscriptionAcknowledgements.Writer.TryWrite((ordinal, received, acknowledgement!));
    }

    // Replace the observation and wake every quote waiter; a waiter rechecks the newest observation itself.
    private void PublishObservation(SubscriptionObservation observation)
    {
        Volatile.Write(ref latestObservation, observation);
        Interlocked.Exchange(ref observationChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    // The four channels an execution subscription owns; quotes and diagnostic mutation channels are not compared.
    internal static bool ExecutionChannelsEqual(SubscriptionAcknowledgement left, SubscriptionAcknowledgement right) =>
        left.Bars.SetEquals(right.Bars) && left.UpdatedBars.SetEquals(right.UpdatedBars) &&
        left.Trades.SetEquals(right.Trades) && left.Statuses.SetEquals(right.Statuses);

    // Read the existing receive buffer's exact value boundaries without copying or re-encoding the event.
    internal static int ObserveEvent(IAlpacaMarketDataReceiptObserver observer, JsonElement element,
        ReadOnlySpan<byte> batch, int offset, DateTimeOffset receivedAtUtc, long ordinal)
    {
        // A session that starts mid-batch begins at the next complete receipt; returning -1 keeps that batch excluded.
        if (offset < 0) return -1;
        while (offset < batch.Length && batch[offset] is (byte)',' or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
            offset++;
        var reader = new Utf8JsonReader(batch[offset..]);
        reader.Read();
        reader.Skip();
        var length = checked((int)reader.BytesConsumed);
        observer.Observe(element, batch.Slice(offset, length), receivedAtUtc, ordinal);
        return offset + length;
    }

    // Preserve normal exact membership checks, isolating late AAPL-only responses after a diagnostic command.
    public Task WaitForSubscriptionAcknowledgementAsync(
        IReadOnlyCollection<string> expectedSymbols,
        CancellationToken cancellationToken) =>
        WaitForAcknowledgementAsync(expectedSymbols, Volatile.Read(ref diagnosticSymbol), null, cancellationToken);

    // Quote-only and pre-command acknowledgements cannot be claimed as the dedicated scope's provider reply.
    public Task<AlpacaDiagnosticSubscriptionAcknowledgement> WaitForDiagnosticSubscriptionAcknowledgementAsync(IReadOnlyCollection<string> expectedSymbols,
        string diagnosticSymbol, long afterArrivalOrdinal, CancellationToken cancellationToken)
    {
        Volatile.Write(ref this.diagnosticSymbol, diagnosticSymbol);
        return WaitForAcknowledgementAsync(expectedSymbols, diagnosticSymbol, afterArrivalOrdinal, cancellationToken);
    }

    // Restore the ordinary strict subscription path once a normal owner has verified AAPL.
    public void ExitDiagnosticAcknowledgementIsolation() => Volatile.Write(ref diagnosticSymbol, null);

    // A single command reader shares the normal bounded acknowledgement deadline.
    private async Task<AlpacaDiagnosticSubscriptionAcknowledgement> WaitForAcknowledgementAsync(
        IReadOnlyCollection<string> expectedSymbols, string? diagnostic,
        long? afterArrivalOrdinal, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var expected = expectedSymbols
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (true)
            {
                var (ordinal, received, acknowledgement) = await subscriptionAcknowledgements.Reader.ReadAsync(timeout.Token);
                // A reply read before the latest execution command was sent answers an earlier command.
                var boundary = Volatile.Read(ref marketBoundary);
                if (ordinal <= (afterArrivalOrdinal ?? boundary.ArrivalOrdinal)) continue;
                if (SubscriptionAcknowledgementMatchesExpected(acknowledgement, expected))
                {
                    var mutationCoverage = diagnostic is not null && afterArrivalOrdinal is not null &&
                        HasExpectedDiagnosticMutations(acknowledgement, diagnostic, expected.Contains(diagnostic));
                    return new(ordinal, received, mutationCoverage);
                }
                if (afterArrivalOrdinal is not null) continue;
                // A reply still showing the execution channels from before this command answers a display quote
                // command sent earlier; it neither satisfies nor fails this wait.
                if (ExecutionChannelsEqual(acknowledgement, boundary.Before)) continue;
                if (diagnostic is not null && DiffersOnlyByDiagnostic(acknowledgement, expected, diagnostic))
                {
                    // A normal owner requesting AAPL still needs the exact AAPL acknowledgement.
                    if (expected.Contains(diagnostic)) continue;
                    return default;
                }
                throw new InvalidOperationException(
                    $"Alpaca subscription acknowledgement does not match the complete expected symbol set [{String.Join(", ", expected)}]. " +
                    DescribeAcknowledgementDifference(acknowledgement, expected));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Alpaca did not acknowledge the market-state subscription within 10 seconds.");
        }
    }

    // Check mutation coverage on the same acknowledgement selected for bars, trades and statuses.
    internal static bool HasExpectedDiagnosticMutations(SubscriptionAcknowledgement acknowledgement, string symbol, bool subscribed) =>
        ChannelContainsExactlyRequestedSymbol(acknowledgement.Corrections, symbol, subscribed) &&
        ChannelContainsExactlyRequestedSymbol(acknowledgement.CancelErrors, symbol, subscribed);

    // Missing and wildcard entries cannot prove this symbol's coverage or removal.
    private static bool ChannelContainsExactlyRequestedSymbol(IReadOnlySet<string>? channel, string symbol, bool subscribed) =>
        channel is not null && !channel.Contains("*") && channel.Contains(symbol) == subscribed;

    // A scoped diagnostic fault may leave only its optional symbol ambiguous; every normal symbol stays exact.
    internal static bool DiffersOnlyByDiagnostic(SubscriptionAcknowledgement acknowledgement,
        IReadOnlySet<string> expected, string diagnostic) =>
        MatchesOutsideDiagnostic(acknowledgement.Bars, expected, diagnostic) &&
        MatchesOutsideDiagnostic(acknowledgement.UpdatedBars, expected, diagnostic) &&
        MatchesOutsideDiagnostic(acknowledgement.Trades, expected, diagnostic) &&
        MatchesOutsideDiagnostic(acknowledgement.Statuses, expected, diagnostic);

    // Compare all entries in either set without allocating filtered copies on the normal command path.
    private static bool MatchesOutsideDiagnostic(IReadOnlySet<string> channel, IReadOnlySet<string> expected, string diagnostic) =>
        channel.All(symbol => symbol == diagnostic || expected.Contains(symbol)) &&
        expected.All(symbol => symbol == diagnostic || channel.Contains(symbol));

    private async Task SendMessageAsync(object payload, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static bool IsAuthorizedResponse(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement
                    .EnumerateArray()
                    .Any(IsAuthorizedElement),
                JsonValueKind.Object => IsAuthorizedElement(document.RootElement),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAuthorizedElement(JsonElement element)
    {
        var status = element.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (status is "authorized" or "authenticated")
        {
            return true;
        }

        var type = element.TryGetProperty("T", out var typeElement)
            ? typeElement.GetString()
            : null;
        var message = element.TryGetProperty("msg", out var messageElement)
            ? messageElement.GetString()
            : null;

        return String.Equals(type, "success", StringComparison.OrdinalIgnoreCase) &&
               String.Equals(message, "authenticated", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSubscriptionAcknowledgement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("T", out var type) &&
        String.Equals(type.GetString(), "subscription", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseSubscriptionAcknowledgement(
        JsonElement element,
        out SubscriptionAcknowledgement? acknowledgement)
    {
        acknowledgement = null;
        if (!IsSubscriptionAcknowledgement(element))
        {
            return false;
        }

        acknowledgement = new SubscriptionAcknowledgement(
            ReadSymbolSet(element, "bars"),
            ReadSymbolSet(element, "updatedBars"),
            ReadSymbolSet(element, "trades"),
            ReadSymbolSet(element, "quotes"),
            ReadSymbolSet(element, "statuses"),
            ReadDiagnosticSymbolSet(element, "corrections"),
            ReadDiagnosticSymbolSet(element, "cancelErrors"));
        return true;
    }

    internal static bool SubscriptionAcknowledgementMatchesExpected(
        SubscriptionAcknowledgement acknowledgement,
        IReadOnlySet<string> expected) =>
        acknowledgement.Bars.SetEquals(expected) &&
        acknowledgement.UpdatedBars.SetEquals(expected) &&
        acknowledgement.Trades.SetEquals(expected) &&
        acknowledgement.Statuses.SetEquals(expected);

    /// <summary>
    /// For a mismatched acknowledgement, names per channel (bars, updatedBars, trades, statuses) the symbols Alpaca left out and
    /// the extra symbols it listed, so a failed subscription can be diagnosed from the log without guessing.
    /// </summary>
    internal static string DescribeAcknowledgementDifference(SubscriptionAcknowledgement acknowledgement, IReadOnlySet<string> expected)
    {
        var channels = new (string Name, IReadOnlySet<string> Symbols)[]
        {
            ("bars", acknowledgement.Bars), ("updatedBars", acknowledgement.UpdatedBars),
            ("trades", acknowledgement.Trades), ("statuses", acknowledgement.Statuses)
        };
        var parts = channels.Where(channel => !channel.Symbols.SetEquals(expected)).Select(channel =>
            $"{channel.Name}: missing [{String.Join(", ", expected.Except(channel.Symbols, StringComparer.OrdinalIgnoreCase).Order())}] " +
            $"extra [{String.Join(", ", channel.Symbols.Except(expected, StringComparer.OrdinalIgnoreCase).Order())}]");
        return "Acknowledged differences: " + String.Join("; ", parts) + ".";
    }

    private static IReadOnlySet<string> ReadSymbolSet(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return property
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !String.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Capture explicit mutation channels while leaving the ordinary acknowledgement contract untouched.
    private static IReadOnlySet<string>? ReadDiagnosticSymbolSet(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var field) || field.ValueKind != JsonValueKind.Array ||
            field.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(value.GetString()))) return null;
        return ReadSymbolSet(element, propertyName);
    }

    internal sealed record SubscriptionAcknowledgement(
        IReadOnlySet<string> Bars,
        IReadOnlySet<string> UpdatedBars,
        IReadOnlySet<string> Trades,
        IReadOnlySet<string> Quotes,
        IReadOnlySet<string> Statuses,
        IReadOnlySet<string>? Corrections = null,
        IReadOnlySet<string>? CancelErrors = null)
    {
        private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The provider state before any subscription on a new connection.
        internal static readonly SubscriptionAcknowledgement Empty = new(None, None, None, None, None);
    }

    // One whole provider reply with the order and time it was read; the connection is this client instance.
    private sealed record SubscriptionObservation(long ArrivalOrdinal, DateTimeOffset ReceivedAtUtc, SubscriptionAcknowledgement Acknowledgement);

    // The arrival ordinal and execution channels recorded just before an execution command was sent.
    private sealed record MarketCommandBoundary(long ArrivalOrdinal, SubscriptionAcknowledgement Before);

    public void Dispose()
    {
        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _webSocket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disposed", timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            _webSocket.Abort();
        }
        finally
        {
            _webSocket.Dispose();
            sendLock.Dispose();
        }
    }
}
