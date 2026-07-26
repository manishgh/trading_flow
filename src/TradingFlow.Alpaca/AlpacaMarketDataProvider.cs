using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class AlpacaMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;
    private readonly string _marketDataFeed;
    private readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> _bulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(10, 50);

    public AlpacaMarketDataProvider(
        HttpClient httpClient,
        AlpacaOptions options,
        ILogger<AlpacaMarketDataProvider>? logger = null)
    {
        _httpClient = httpClient;
        _options = options;
        _marketDataFeed = options.ResolveMarketDataFeed();
        _httpClient.BaseAddress = AlpacaEndpointResolver.Resolve(_options.Profile).MarketDataRest;
        _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _options.KeyId);
        _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _options.SecretKey);
        logger?.LogInformation("Alpaca REST market data feed configured as {MarketDataFeed}.", _marketDataFeed);
    }

    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (timeframes.Count == 0) timeframes = new[] { "1d" };

        foreach (var originalTimeframe in timeframes)
        {
            var timeframe = originalTimeframe.ToLowerInvariant();
            if (timeframe == "1d") timeframe = "1Day";
            else if (timeframe == "1h") timeframe = "1Hour";
            else if (timeframe == "5m") timeframe = "5Min";
            else if (timeframe == "1m") timeframe = "1Min";
            else if (timeframe == "15m") timeframe = "15Min";

            foreach (var tickerBatch in tickers
                .Select(AlpacaSymbolMapper.ToCanonicalSymbol)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Chunk(100))
            {
                var startStr = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var endStr = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var symbols = String.Join(
                    ",",
                    tickerBatch
                        .Select(AlpacaSymbolMapper.ToProviderSymbol)
                        .Select(Uri.EscapeDataString));
                string? nextPageToken = null;

                do
                {
                    var url = $"/v2/stocks/bars?symbols={symbols}&timeframe={timeframe}&start={startStr}&end={endStr}&feed={_marketDataFeed}&adjustment=all&limit=10000";
                    if (!String.IsNullOrWhiteSpace(nextPageToken))
                    {
                        url += $"&page_token={Uri.EscapeDataString(nextPageToken)}";
                    }

                    using var response = await SendWithRetriesAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new Exception($"Alpaca API Error ({(int?)response.StatusCode}): {error}");
                    }

                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(content);
                    
                    if (doc.RootElement.TryGetProperty("bars", out var barsElement))
                    {
                        foreach (var tickerProperty in barsElement.EnumerateObject())
                        {
                            var ticker = AlpacaSymbolMapper.ToCanonicalSymbol(tickerProperty.Name);
                            foreach (var bar in tickerProperty.Value.EnumerateArray())
                            {
                                yield return new OhlcvBar(
                                    ticker,
                                    bar.GetProperty("t").GetDateTimeOffset(),
                                    originalTimeframe,
                                    bar.GetProperty("o").GetDecimal(),
                                    bar.GetProperty("h").GetDecimal(),
                                    bar.GetProperty("l").GetDecimal(),
                                    bar.GetProperty("c").GetDecimal(),
                                    bar.GetProperty("v").GetDecimal()
                                );
                            }
                        }
                    }

                    nextPageToken = doc.RootElement.TryGetProperty("next_page_token", out var tokenElement) &&
                                    tokenElement.ValueKind == JsonValueKind.String
                        ? tokenElement.GetString()
                        : null;
                }
                while (!String.IsNullOrWhiteSpace(nextPageToken));
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(string url, CancellationToken cancellationToken)
    {
        int retryCount = 0;
        while (retryCount < 3)
        {
            try
            {
                var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Alpaca", url, "GET",
                    () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    response.Dispose();
                    await Task.Delay(retryAfter, cancellationToken);
                    retryCount++;
                    continue;
                }

                return response;
            }
            catch (Polly.Bulkhead.BulkheadRejectedException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                retryCount++;
            }
        }

        throw new HttpRequestException($"Alpaca API request failed after retries: {url}");
    }

}
