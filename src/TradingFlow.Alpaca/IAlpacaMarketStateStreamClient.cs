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

    IAsyncEnumerable<JsonElement> ReadMessagesAsync(CancellationToken cancellationToken);
}
