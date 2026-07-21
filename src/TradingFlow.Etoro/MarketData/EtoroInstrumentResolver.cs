using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.Models;

namespace TradingFlow.Etoro.MarketData;

public sealed class EtoroInstrumentResolver
{
    private readonly EtoroApiClient apiClient;
    private readonly ConcurrentDictionary<string, long> tickerToInstrumentId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, string> instrumentIdToTicker = new();

    public EtoroInstrumentResolver(EtoroApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public async Task<long> ResolveInstrumentIdAsync(string ticker, CancellationToken cancellationToken)
    {
        if (tickerToInstrumentId.TryGetValue(ticker, out var cached))
        {
            return cached;
        }

        var escapedTicker = Uri.EscapeDataString(ticker.ToUpperInvariant());
        var searchResponse = await apiClient.GetAsync<System.Text.Json.JsonElement>(
            $"/market-data/search?internalSymbolFull={escapedTicker}",
            cancellationToken);
        var instruments = ExtractInstruments(searchResponse);

        var match = instruments.FirstOrDefault(candidate =>
            candidate.InternalSymbolFull?.Equals(ticker, StringComparison.OrdinalIgnoreCase) == true ||
            candidate.SymbolFull?.Equals(ticker, StringComparison.OrdinalIgnoreCase) == true ||
            candidate.Symbol?.Equals(ticker, StringComparison.OrdinalIgnoreCase) == true) ??
            instruments.FirstOrDefault();

        var instrumentId = match?.InstrumentId ?? match?.InternalInstrumentId;
        if (instrumentId is null || instrumentId <= 0)
        {
            throw new InvalidOperationException($"Could not resolve eToro instrumentId for ticker {ticker}.");
        }

        tickerToInstrumentId[ticker] = instrumentId.Value;
        instrumentIdToTicker[instrumentId.Value] = ticker.ToUpperInvariant();
        return instrumentId.Value;
    }

    public string ResolveTicker(long instrumentId)
    {
        return instrumentIdToTicker.TryGetValue(instrumentId, out var ticker)
            ? ticker
            : instrumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<EtoroInstrument> ExtractInstruments(System.Text.Json.JsonElement response)
    {
        var items = response.ValueKind == System.Text.Json.JsonValueKind.Object &&
            response.TryGetProperty("items", out var wrappedItems)
            ? wrappedItems
            : response;

        if (items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new InvalidOperationException("Unexpected eToro instrument search response shape.");
        }

        return items.EnumerateArray()
            .Select(item => item.Deserialize<EtoroInstrument>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }
}
