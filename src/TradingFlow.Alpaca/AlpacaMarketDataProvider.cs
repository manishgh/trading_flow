using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Alpaca;

public sealed class AlpacaMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;
    private readonly string _marketDataFeed;
    private readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> _bulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(10, 50);

    public AlpacaMarketDataProvider(HttpClient httpClient, AlpacaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _marketDataFeed = NormalizeFeed(options.MarketDataFeed);
        _httpClient.BaseAddress = new Uri("https://data.alpaca.markets");
        _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _options.KeyId);
        _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _options.SecretKey);
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

            foreach (var ticker in tickers)
            {
                var startStr = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var endStr = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var url = $"/v2/stocks/bars?symbols={ticker.ToUpperInvariant()}&timeframe={timeframe}&start={startStr}&end={endStr}&feed={_marketDataFeed}";
                
                int retryCount = 0;
                HttpResponseMessage? response = null;
                while (retryCount < 3)
                {
                    try
                    {
                        response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Alpaca", url, "GET", 
                            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
                        
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                            await Task.Delay(retryAfter, cancellationToken);
                            retryCount++;
                            continue;
                        }
                        break;
                    }
                    catch (Polly.Bulkhead.BulkheadRejectedException)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        retryCount++;
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    var error = response != null ? await response.Content.ReadAsStringAsync(cancellationToken) : "Unknown error";
                    throw new Exception($"Alpaca API Error ({(int?)response?.StatusCode}): {error}");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(content);
                
                if (doc.RootElement.TryGetProperty("bars", out var barsElement) && barsElement.TryGetProperty(ticker.ToUpperInvariant(), out var tickerBars))
                {
                    foreach (var bar in tickerBars.EnumerateArray())
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
        }
    }

    private static string NormalizeFeed(string feed)
    {
        var normalized = String.IsNullOrWhiteSpace(feed)
            ? "sip"
            : feed.Trim().ToLowerInvariant();

        return normalized is "sip" or "iex" or "otc"
            ? normalized
            : throw new ArgumentException($"Unsupported Alpaca market data feed '{feed}'. Use sip, iex, or otc.", nameof(feed));
    }
}
