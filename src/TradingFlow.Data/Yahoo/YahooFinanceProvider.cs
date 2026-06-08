using System.Globalization;
using System.Net;
using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Data.Yahoo;

public sealed class YahooFinanceProvider(
    HttpClient httpClient,
    YahooProviderConfig config,
    string rawRoot,
    string normalizedRoot,
    string cachePolicy) : IMarketDataProvider
{
    private readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> _bulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(5, 50);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        IReadOnlyCollection<string> tickers,
        IReadOnlyCollection<string> timeframes,
        DateTimeOffset start,
        DateTimeOffset end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var ticker in tickers)
        {
            foreach (var timeframe in timeframes)
            {
                var bars = await LoadChartAsync(ticker, timeframe, start, end, cancellationToken);
                foreach (var bar in bars)
                {
                    yield return bar;
                }
            }
        }
    }

    public static HttpClient CreateBrowserLikeClient(YahooProviderConfig config)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(config.Headers.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(config.Headers.Accept);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(config.Headers.AcceptLanguage);
        return client;
    }

    private async Task<IReadOnlyList<OhlcvBar>> LoadChartAsync(
        string ticker,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var normalizedPath = GetNormalizedPath(ticker, timeframe);
        if (CanReadCache() && File.Exists(normalizedPath))
        {
            var cachedBars = await ReadNormalizedBarsAsync(normalizedPath, timeframe, start, end, cancellationToken);
            if (HasRequestedCoverage(cachedBars, start, end))
            {
                return cachedBars;
            }
        }

        if (cachePolicy.Equals("offline_only", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"Offline cache not found for {ticker} {timeframe}.", normalizedPath);
        }

        var rawPath = GetRawPath(ticker, timeframe, start, end);
        var json = CanReadCache() && File.Exists(rawPath)
            ? await File.ReadAllTextAsync(rawPath, cancellationToken)
            : await DownloadAndCacheRawJsonAsync(rawPath, ticker, timeframe, start, end, cancellationToken);

        var bars = ParseChart(json, ticker, timeframe)
            .Where(x => x.Timestamp >= start && x.Timestamp <= end)
            .ToArray();
        await WriteNormalizedBarsAsync(normalizedPath, bars, cancellationToken);
        return bars;
    }

    private async Task<string> DownloadAndCacheRawJsonAsync(
        string rawPath,
        string ticker,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var json = await DownloadChartJsonAsync(ticker, timeframe, start, end, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        await File.WriteAllTextAsync(rawPath, json, cancellationToken);
        return json;
    }

    private async Task<string> DownloadChartJsonAsync(
        string ticker,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var yahooInterval = MapInterval(timeframe);
        var requestPath = UseYahooSixtyDayRange(timeframe, start, end)
            ? String.Create(
                CultureInfo.InvariantCulture,
                $"/v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=60d&interval={yahooInterval}&includePrePost=false&events=div%2Csplits")
            : String.Create(
                CultureInfo.InvariantCulture,
                $"/v8/finance/chart/{Uri.EscapeDataString(ticker)}?period1={start.ToUnixTimeSeconds()}&period2={end.ToUnixTimeSeconds()}&interval={yahooInterval}&includePrePost=false&events=div%2Csplits");

        return await SendWithRetriesAsync(requestPath, cancellationToken);
    }

    private async Task<string> SendWithRetriesAsync(string requestPath, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, config.MaxRetries + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await RespectThrottleAsync(cancellationToken);

                using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
                using var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Yahoo", requestPath, "GET", 
                    () => _bulkhead.ExecuteAsync(ct => httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), cancellationToken));
                _lastRequestAt = DateTimeOffset.UtcNow;

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }

                if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"Yahoo request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                }
            }
            catch (Polly.Bulkhead.BulkheadRejectedException)
            {
                // Let the retry loop handle it
            }

            var backoff = TimeSpan.FromMilliseconds(Math.Max(1, config.ThrottleMs) * attempt);
            await Task.Delay(backoff, cancellationToken);
        }

        throw new InvalidOperationException("Yahoo retry loop exited unexpectedly.");
    }

    private async Task RespectThrottleAsync(CancellationToken cancellationToken)
    {
        if (config.ThrottleMs <= 0 || _lastRequestAt == DateTimeOffset.MinValue)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
        var requiredDelay = TimeSpan.FromMilliseconds(config.ThrottleMs);
        if (elapsed < requiredDelay)
        {
            await Task.Delay(requiredDelay - elapsed, cancellationToken);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static IReadOnlyList<OhlcvBar> ParseChart(string json, string ticker, string timeframe)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement
            .GetProperty("chart")
            .GetProperty("result")[0];

        var timestamps = root.GetProperty("timestamp").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var quote = root.GetProperty("indicators").GetProperty("quote")[0];

        var opens = ReadNullableDecimals(quote, "open");
        var highs = ReadNullableDecimals(quote, "high");
        var lows = ReadNullableDecimals(quote, "low");
        var closes = ReadNullableDecimals(quote, "close");
        var volumes = ReadNullableDecimals(quote, "volume");

        var rowCount = new[] { timestamps.Length, opens.Length, highs.Length, lows.Length, closes.Length, volumes.Length }.Min();
        var bars = new List<OhlcvBar>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            if (opens[i] is null || highs[i] is null || lows[i] is null || closes[i] is null || volumes[i] is null)
            {
                continue;
            }

            bars.Add(new OhlcvBar(
                ticker.ToUpperInvariant(),
                DateTimeOffset.FromUnixTimeSeconds(timestamps[i]),
                timeframe,
                opens[i]!.Value,
                highs[i]!.Value,
                lows[i]!.Value,
                closes[i]!.Value,
                volumes[i]!.Value));
        }

        return bars;
    }

    private static decimal?[] ReadNullableDecimals(JsonElement parent, string propertyName)
    {
        return parent
            .GetProperty(propertyName)
            .EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Null ? null : (decimal?)value.GetDecimal())
            .ToArray();
    }

    private static async Task<IReadOnlyList<OhlcvBar>> ReadNormalizedBarsAsync(
        string path,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var tickerDirectory = Directory.GetParent(path) ??
            throw new DirectoryNotFoundException($"Could not resolve ticker directory for {path}.");
        var normalizedRootDirectory = tickerDirectory.Parent ??
            throw new DirectoryNotFoundException($"Could not resolve normalized root for {path}.");
        var provider = new CsvMarketDataProvider(normalizedRootDirectory.FullName);
        var ticker = tickerDirectory.Name;
        var bars = new List<OhlcvBar>();

        await foreach (var bar in provider.GetBarsAsync([ticker], [timeframe], start, end, cancellationToken))
        {
            bars.Add(bar);
        }

        return bars;
    }

    private static async Task WriteNormalizedBarsAsync(
        string path,
        IReadOnlyList<OhlcvBar> bars,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("ticker,timestamp,open,high,low,close,volume".AsMemory(), cancellationToken);

        foreach (var bar in bars.OrderBy(x => x.Timestamp))
        {
            var line = String.Create(
                CultureInfo.InvariantCulture,
                $"{bar.Ticker},{bar.Timestamp:O},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume}");
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static bool HasRequestedCoverage(IReadOnlyList<OhlcvBar> bars, DateTimeOffset start, DateTimeOffset end)
    {
        if (bars.Count == 0)
        {
            return false;
        }

        var ordered = bars.OrderBy(x => x.Timestamp).ToArray();
        var firstAllowed = start.AddDays(5);
        var lastAllowed = end.AddDays(-5);
        return ordered[0].Timestamp <= firstAllowed && ordered[^1].Timestamp >= lastAllowed;
    }

    private bool CanReadCache()
    {
        return cachePolicy.Equals("prefer_cache", StringComparison.OrdinalIgnoreCase) ||
            cachePolicy.Equals("offline_only", StringComparison.OrdinalIgnoreCase);
    }

    private string GetRawPath(string ticker, string timeframe, DateTimeOffset start, DateTimeOffset end)
    {
        var fileName = String.Create(
            CultureInfo.InvariantCulture,
            $"yahoo_chart_{timeframe}_{start:yyyyMMddHHmm}_{end:yyyyMMddHHmm}.json");
        return Path.Combine(rawRoot, ticker.ToUpperInvariant(), fileName);
    }

    private string GetNormalizedPath(string ticker, string timeframe)
    {
        return Path.Combine(normalizedRoot, ticker.ToUpperInvariant(), $"bars_{timeframe}.csv");
    }

    private static string MapInterval(string timeframe)
    {
        return timeframe.ToLowerInvariant() switch
        {
            "1h" => "60m",
            _ => timeframe.ToLowerInvariant()
        };
    }

    private static bool UseYahooSixtyDayRange(string timeframe, DateTimeOffset start, DateTimeOffset end)
    {
        return timeframe.Equals("5m", StringComparison.OrdinalIgnoreCase) &&
            (end - start).TotalDays >= 59.9;
    }
}
