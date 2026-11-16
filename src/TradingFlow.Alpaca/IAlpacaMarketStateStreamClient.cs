using System.Text.Json;

namespace TradingFlow.Alpaca;

/// <summary>
/// Provider stream contract used by the single-owner market-state service.
/// Keeping the transport behind this boundary makes lease loss and reconnect
/// behavior deterministic to test without opening an external socket.
/// </summary>
public interface IAlpacaMarketStateStreamClient : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task SubscribeMarketStateAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);

    Task UnsubscribeMarketStateAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken);

    Task WaitForSubscriptionAcknowledgementAsync(
        IReadOnlyCollection<string> expectedSymbols,
        CancellationToken cancellationToken);

    /// <summary>Verifies the dedicated scope against a new acknowledgement after its command boundary.</summary>
    Task<AlpacaDiagnosticSubscriptionAcknowledgement> WaitForDiagnosticSubscriptionAcknowledgementAsync(
        IReadOnlyCollection<string> expectedSymbols, string diagnosticSymbol, long afterArrivalOrdinal,
        CancellationToken cancellationToken) =>
        Task.FromException<AlpacaDiagnosticSubscriptionAcknowledgement>(
            new InvalidOperationException("This transport does not expose causal diagnostic acknowledgement evidence."));

    /// <summary>A normally owned symbol again requires the ordinary full acknowledgement contract.</summary>
    void ExitDiagnosticAcknowledgementIsolation() { }

    IAsyncEnumerable<JsonElement> ReadMessagesAsync(CancellationToken cancellationToken);

    /// <summary>Receipt time of the current batch before parsing; alternate in-process streams may supply their own clock.</summary>
    DateTimeOffset? CurrentBatchReceivedAtUtc => null;

    /// <summary>Available only when this stream can surface exact original event spans.</summary>
    bool TryInstallReceiptObserver(IAlpacaMarketDataReceiptObserver observer) => false;

    /// <summary>The transport ingress ordinal for the currently yielded event, if supported.</summary>
    long CurrentArrivalOrdinal => 0;

    /// <summary>Display-only quote subscriptions, outside the market-state acknowledgement.</summary>
    Task SubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Task.CompletedTask;

    Task UnsubscribeQuotesAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken) => Task.CompletedTask;
}
