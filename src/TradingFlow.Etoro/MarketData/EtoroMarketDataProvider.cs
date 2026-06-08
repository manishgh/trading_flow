using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.Models;

namespace TradingFlow.Etoro.MarketData;

public sealed class EtoroMarketDataProvider : IMarketDataProvider
{
    private readonly EtoroApiClient apiClient;
    private readonly EtoroInstrumentResolver instrumentResolver;

    public EtoroMarketDataProvider(EtoroApiClient apiClient, EtoroInstrumentResolver instrumentResolver)
    {
        this.apiClient = apiClient;
        this.instrumentResolver = instrumentResolver;
    }

    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var ticker in tickers)
        {
            var instrumentId = await instrumentResolver.ResolveInstrumentIdAsync(ticker, cancellationToken);
            foreach (var timeframe in timeframes)
            {
                var candles = await GetHistoricalCandlesAsync(
                    instrumentId,
                    MapInterval(timeframe),
                    CalculateCandlesCount(timeframe, start, end),
                    cancellationToken);

                foreach (var candle in candles
                    .Where(candle => candle.Timestamp >= start && candle.Timestamp <= end)
                    .OrderBy(candle => candle.Timestamp))
                {
                    yield return new OhlcvBar(
                        ticker.ToUpperInvariant(),
                        candle.Timestamp,
                        timeframe,
                        candle.Open,
                        candle.High,
                        candle.Low,
                        candle.Close,
                        candle.Volume);
                }
            }
        }
    }

    public Task<IReadOnlyList<EtoroRate>> GetRatesAsync(IReadOnlyCollection<long> instrumentIds, CancellationToken cancellationToken)
    {
        if (instrumentIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EtoroRate>>(Array.Empty<EtoroRate>());
        }

        var ids = String.Join(",", instrumentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return GetRatesCoreAsync(ids, cancellationToken);
    }

    private async Task<IReadOnlyList<EtoroRate>> GetRatesCoreAsync(string instrumentIds, CancellationToken cancellationToken)
    {
        var response = await apiClient.GetAsync<JsonElement>(
            $"/market-data/instruments/rates?instrumentIds={Uri.EscapeDataString(instrumentIds)}",
            cancellationToken);
        return ExtractArray<EtoroRate>(response, "rates");
    }

    private async Task<IReadOnlyList<EtoroCandle>> GetHistoricalCandlesAsync(
        long instrumentId,
        string interval,
        int candlesCount,
        CancellationToken cancellationToken)
    {
        var requestPath = String.Create(
            CultureInfo.InvariantCulture,
            $"/market-data/instruments/{instrumentId}/history/candles/desc/{interval}/{candlesCount}");
        var response = await apiClient.GetAsync<JsonElement>(requestPath, cancellationToken);
        return ExtractCandles(response);
    }

    private static string MapInterval(string timeframe)
    {
        return timeframe.ToLowerInvariant() switch
        {
            "1m" => "OneMinute",
            "5m" => "FiveMinutes",
            "15m" => "FifteenMinutes",
            "30m" => "ThirtyMinutes",
            "1h" => "OneHour",
            "4h" => "FourHours",
            "1d" => "OneDay",
            _ => throw new NotSupportedException($"Unsupported eToro candle timeframe: {timeframe}.")
        };
    }

    private static int CalculateCandlesCount(string timeframe, DateTimeOffset start, DateTimeOffset end)
    {
        var minutes = timeframe.ToLowerInvariant() switch
        {
            "1m" => 1,
            "5m" => 5,
            "15m" => 15,
            "30m" => 30,
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            _ => throw new NotSupportedException($"Unsupported eToro candle timeframe: {timeframe}.")
        };

        var count = (int)Math.Ceiling((end - start).TotalMinutes / minutes) + 10;
        return Math.Clamp(count, 1, 5000);
    }

    private static IReadOnlyList<T> ExtractArray<T>(JsonElement response, string wrappedProperty)
    {
        var items = response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty(wrappedProperty, out var wrappedItems)
            ? wrappedItems
            : response;

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Unexpected eToro {wrappedProperty} response shape.");
        }

        return items.EnumerateArray()
            .Select(item => item.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    private static IReadOnlyList<EtoroCandle> ExtractCandles(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("candles", out var instrumentGroups) ||
            instrumentGroups.ValueKind != JsonValueKind.Array)
        {
            return ExtractArray<EtoroCandle>(response, "candles");
        }

        var bars = new List<EtoroCandle>();
        foreach (var group in instrumentGroups.EnumerateArray())
        {
            if (!group.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var candle in candles.EnumerateArray())
            {
                var timestamp = candle.TryGetProperty("fromDate", out var fromDate)
                    ? fromDate.GetDateTimeOffset()
                    : candle.GetProperty("timestamp").GetDateTimeOffset();
                bars.Add(new EtoroCandle(
                    timestamp,
                    timestamp,
                    candle.GetProperty("open").GetDecimal(),
                    candle.GetProperty("high").GetDecimal(),
                    candle.GetProperty("low").GetDecimal(),
                    candle.GetProperty("close").GetDecimal(),
                    candle.TryGetProperty("volume", out var volume) ? volume.GetDecimal() : 0m));
            }
        }

        return bars;
    }
}
