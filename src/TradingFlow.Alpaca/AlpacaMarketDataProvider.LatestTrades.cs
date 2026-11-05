using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed partial class AlpacaMarketDataProvider
{
    /// <summary>
    /// Asks Alpaca's latest-trades endpoint for up to 100 stocks per request, on the configured feed. Each
    /// response is archived like every other Alpaca response. Throws when Alpaca answers with an error status.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LatestTrade>> GetLatestTradesAsync(
        IReadOnlyCollection<string> tickers,
        CancellationToken cancellationToken)
    {
        var requestedByProviderSymbol = tickers
            .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
            .DistinctBy(AlpacaSymbolMapper.ToProviderSymbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(AlpacaSymbolMapper.ToProviderSymbol, ticker => ticker, StringComparer.OrdinalIgnoreCase);
        var trades = new Dictionary<string, LatestTrade>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in requestedByProviderSymbol.Keys.Chunk(100))
        {
            var symbols = String.Join(",", batch.Select(Uri.EscapeDataString));
            var response = await SendWithRetriesAsync(
                $"/v2/stocks/trades/latest?symbols={symbols}&feed={_marketDataFeed}",
                "latest-trades",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Alpaca latest-trades request failed with HTTP {(int)response.StatusCode}: {response.ReadAsString().Trim()}",
                    null,
                    response.StatusCode);
            }

            foreach (var trade in ParseLatestTrades(response.ReadAsString(), requestedByProviderSymbol))
            {
                trades[trade.Ticker] = trade;
            }
        }

        return new ReadOnlyDictionary<string, LatestTrade>(trades);
    }

    /// <summary>
    /// Reads the "trades" object of a latest-trades response: price "p" and time "t" per Alpaca symbol. Symbols
    /// that were not requested are ignored; each trade is keyed by the ticker exactly as it was requested.
    /// </summary>
    internal static IEnumerable<LatestTrade> ParseLatestTrades(
        string payload,
        IReadOnlyDictionary<string, string> requestedByProviderSymbol)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("trades", out var tradesElement) ||
            tradesElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var trades = new List<LatestTrade>();
        foreach (var property in tradesElement.EnumerateObject())
        {
            if (requestedByProviderSymbol.TryGetValue(property.Name, out var ticker) &&
                property.Value.TryGetProperty("p", out var price) &&
                property.Value.TryGetProperty("t", out var time))
            {
                trades.Add(new LatestTrade(ticker, price.GetDecimal(), time.GetDateTimeOffset().ToUniversalTime()));
            }
        }

        return trades;
    }
}
