using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class AlpacaMarketDataStreamer : IMarketDataStreamer, IDisposable
{
    private readonly AlpacaStreamClient _client;

    public AlpacaMarketDataStreamer(
        AlpacaOptions options,
        ILogger<AlpacaStreamClient>? streamLogger = null)
    {
        _client = new AlpacaStreamClient(options, streamLogger);
    }

    public async IAsyncEnumerable<OhlcvBar> SubscribeBarsAsync(
        IReadOnlyCollection<string> tickers, 
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken);
        await _client.SubscribeBarsAsync(tickers, cancellationToken);

        await foreach (var element in _client.ReadMessagesAsync(cancellationToken))
        {
            if (element.TryGetProperty("T", out var typeProp) && typeProp.GetString() == "b")
            {
                var ticker = element.GetProperty("S").GetString() ?? "UNKNOWN";
                var timestamp = element.GetProperty("t").GetDateTimeOffset();
                var open = element.GetProperty("o").GetDecimal();
                var high = element.GetProperty("h").GetDecimal();
                var low = element.GetProperty("l").GetDecimal();
                var close = element.GetProperty("c").GetDecimal();
                var volume = element.GetProperty("v").GetDecimal();

                // WebSockets usually stream 1m bars in real-time. We'll label it "1m" implicitly 
                // or just "stream" depending on aggregation needs.
                yield return new OhlcvBar(ticker, timestamp, "1m", open, high, low, close, volume);
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
