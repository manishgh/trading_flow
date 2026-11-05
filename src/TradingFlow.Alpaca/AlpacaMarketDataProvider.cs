using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Alpaca;

public sealed partial class AlpacaMarketDataProvider :
    IMarketDataProvider,
    IMarketDataCompletenessProvider,
    IMarketSessionScheduleProvider,
    IMarketDataProvenanceProvider,
    IHistoricalMarketDataEvidenceProvider,
    ILatestTradeProvider
{
    private readonly HttpClient _httpClient;
    private readonly AlpacaOptions _options;
    private readonly AlpacaRawResponseArchiver _responseArchiver;
    private readonly string _marketDataFeed;
    private readonly string _adjustmentPolicy;
    private readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> _bulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(10, 50);

    public bool OmittedSubDailyIntervalsMeanNoQualifyingTrades => true;

    public MarketDataProvenance MarketDataProvenance => new(
        "alpaca_historical_bars_v2",
        _marketDataFeed,
        _adjustmentPolicy);

    /// <summary>Requires durable response storage and one validated price basis for queries, bars and provenance.</summary>
    public AlpacaMarketDataProvider(
        HttpClient httpClient,
        AlpacaOptions options,
        IRawArchiveWriter rawArchiveWriter,
        ILogger<AlpacaMarketDataProvider>? logger = null,
        string adjustmentPolicy = "all")
    {
        if (adjustmentPolicy is not ("raw" or "all"))
            throw new ArgumentException("Alpaca price adjustment must be raw or all.", nameof(adjustmentPolicy));
        _httpClient = httpClient;
        _options = options;
        _responseArchiver = new AlpacaRawResponseArchiver(rawArchiveWriter);
        _marketDataFeed = options.ResolveMarketDataFeed();
        _adjustmentPolicy = adjustmentPolicy;
        _httpClient.BaseAddress = AlpacaEndpointResolver.Resolve(_options.Profile).MarketDataRest;
        _httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _options.KeyId);
        _httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _options.SecretKey);
        logger?.LogInformation("Alpaca REST market data feed configured as {MarketDataFeed}.", _marketDataFeed);
    }

    /// <summary>Streams only fully archived batches and stops emission when the caller cancels.</summary>
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
                var startStr = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFZ", CultureInfo.InvariantCulture);
                var endStr = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFZ", CultureInfo.InvariantCulture);
                var batchBars = await FetchBatchWithFallbackAsync(tickerBatch, timeframe, originalTimeframe, startStr, endStr, cancellationToken);
                foreach (var bar in batchBars)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return bar;
                }
            }
        }
    }

    /// <summary>
    /// Loads Alpaca's official market calendar from the profile-specific trading endpoint.
    /// Alpaca returns open sessions only, so omissions are converted to explicit closed dates
    /// after the complete response has been archived and validated against the requested range.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
        DateOnly startDateInclusive,
        DateOnly endDateInclusive,
        CancellationToken cancellationToken)
    {
        if (endDateInclusive < startDateInclusive)
        {
            throw new ArgumentException(
                "Market-session schedule end date must be on or after the start date.",
                nameof(endDateInclusive));
        }

        var start = startDateInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = endDateInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tradingEndpoint = AlpacaEndpointResolver.Resolve(_options.Profile).TradingRest;
        var requestUri = new Uri(
            tradingEndpoint,
            $"/v2/calendar?start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}");

        var response = await SendWithRetriesAsync(requestUri.AbsoluteUri, "calendar-rest-page", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            var error = response.ReadAsString();
            var providerDetail = String.IsNullOrWhiteSpace(error) ? "No provider detail." : error.Trim();
            throw new InvalidOperationException(
                $"Alpaca trading-calendar lookup failed with HTTP {(int)response.StatusCode}: {providerDetail}");
        }

        var payload = response.ReadAsString();
        Dictionary<DateOnly, MarketSessionSchedule> openSchedules;
        try
        {
            openSchedules = ParseCalendarResponse(payload, startDateInclusive, endDateInclusive);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Alpaca trading-calendar response was not valid JSON.",
                exception);
        }

        var completeRange = new Dictionary<DateOnly, MarketSessionSchedule>();
        var current = startDateInclusive;
        while (true)
        {
            completeRange[current] = openSchedules.TryGetValue(current, out var schedule)
                ? schedule
                : MarketSessionSchedule.Closed(current);

            if (current == endDateInclusive)
            {
                break;
            }

            current = current.AddDays(1);
        }

        return new ReadOnlyDictionary<DateOnly, MarketSessionSchedule>(completeRange);
    }

    /// <summary>Retries provider-rejected symbol batches individually; storage and parsing failures always propagate.</summary>
    private async Task<List<OhlcvBar>> FetchBatchWithFallbackAsync(
        string[] tickerBatch,
        string timeframe,
        string originalTimeframe,
        string startStr,
        string endStr,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchBatchExactAsync(tickerBatch, timeframe, originalTimeframe, startStr, endStr, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue && ex.Message.Contains("invalid symbol") && tickerBatch.Length > 1)
        {
            var results = new List<OhlcvBar>();
            foreach (var ticker in tickerBatch)
            {
                try
                {
                    results.AddRange(await FetchBatchExactAsync(new[] { ticker }, timeframe, originalTimeframe, startStr, endStr, cancellationToken));
                }
                catch (HttpRequestException innerEx) when (innerEx.StatusCode.HasValue && innerEx.Message.Contains("invalid symbol"))
                {
                    // Log or simply ignore the single invalid symbol
                }
            }
            return results;
        }
    }

    /// <summary>Parses only committed response pages and returns a batch only after all its pages were archived.</summary>
    private async Task<List<OhlcvBar>> FetchBatchExactAsync(
        string[] tickerBatch,
        string timeframe,
        string originalTimeframe,
        string startStr,
        string endStr,
        CancellationToken cancellationToken)
    {
        var results = new List<OhlcvBar>();
        var coverageVerifiedThroughUtc = DateTimeOffset.Parse(
            endStr,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var symbols = String.Join(
            ",",
            tickerBatch
                .Select(AlpacaSymbolMapper.ToProviderSymbol)
                .Select(Uri.EscapeDataString));
        string? nextPageToken = null;
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            var url = $"/v2/stocks/bars?symbols={symbols}&timeframe={timeframe}&start={startStr}&end={endStr}&feed={_marketDataFeed}&adjustment={_adjustmentPolicy}&limit=10000";
            if (!String.IsNullOrWhiteSpace(nextPageToken))
            {
                url += $"&page_token={Uri.EscapeDataString(nextPageToken)}";
            }

            var response = await SendWithRetriesAsync(url, "bars-rest-page", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                var error = response.ReadAsString();
                throw new HttpRequestException($"Alpaca API Error ({(int?)response.StatusCode}): {error}", null, response.StatusCode);
            }

            var content = response.ReadAsString();
            using var doc = JsonDocument.Parse(content);

            if (_historicalCapture.Value is not null &&
                (!doc.RootElement.TryGetProperty("bars", out var capturedBars) ||
                 capturedBars.ValueKind != JsonValueKind.Object ||
                 !doc.RootElement.TryGetProperty("next_page_token", out var capturedToken) ||
                 capturedToken.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw new InvalidDataException("Alpaca historical capture requires a bars object and explicit pagination completion token.");
            
            if (doc.RootElement.TryGetProperty("bars", out var barsElement))
            {
                foreach (var tickerProperty in barsElement.EnumerateObject())
                {
                    var ticker = AlpacaSymbolMapper.ToCanonicalSymbol(tickerProperty.Name);
                    foreach (var bar in tickerProperty.Value.EnumerateArray())
                    {
                        CountHistoricalRow();
                        results.Add(new OhlcvBar(
                            ticker,
                            bar.GetProperty("t").GetDateTimeOffset(),
                            originalTimeframe,
                            bar.GetProperty("o").GetDecimal(),
                            bar.GetProperty("h").GetDecimal(),
                            bar.GetProperty("l").GetDecimal(),
                            bar.GetProperty("c").GetDecimal(),
                            bar.GetProperty("v").GetDecimal(),
                            _marketDataFeed,
                            _adjustmentPolicy,
                            KnownAtUtc: null,
                            CoverageVerifiedThroughUtc: coverageVerifiedThroughUtc
                        ));
                    }
                }
            }

            nextPageToken = doc.RootElement.TryGetProperty("next_page_token", out var tokenElement) &&
                            tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;
            if (!String.IsNullOrWhiteSpace(nextPageToken) && !seenPageTokens.Add(nextPageToken))
                throw new InvalidDataException("Alpaca repeated a bars pagination token; the requested query is incomplete.");
        }
        while (!String.IsNullOrWhiteSpace(nextPageToken));

        return results;
    }

    /// <summary>Finishes archiving complete buffered HTTP bodies despite run cancellation, then honors cancellation before handling them.</summary>
    private async Task<ArchivedAlpacaResponse> SendWithRetriesAsync(
        string url, string artifactType, CancellationToken cancellationToken)
    {
        int retryCount = 0;
        while (retryCount < 3)
        {
            HttpResponseMessage response;
            try
            {
                response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Alpaca", url, "GET",
                    () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
            }
            catch (Polly.Bulkhead.BulkheadRejectedException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                retryCount++;
                continue;
            }

            using (response)
            {
                // GetAsync has buffered the full body; run cancellation must not discard this newly received original.
                var archivedResponse = await _responseArchiver.ArchiveAsync(
                    response, artifactType, cancellationToken: CancellationToken.None);
                _historicalCapture.Value?.Responses.Add(archivedResponse.Archive);
                cancellationToken.ThrowIfCancellationRequested();
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    response.Dispose();
                    await Task.Delay(retryAfter, cancellationToken);
                    retryCount++;
                    continue;
                }

                return archivedResponse;
            }
        }

        throw new HttpRequestException($"Alpaca API request failed after retries: {url}");
    }

    private static Dictionary<DateOnly, MarketSessionSchedule> ParseCalendarResponse(
        string payload,
        DateOnly startDateInclusive,
        DateOnly endDateInclusive)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Alpaca trading-calendar response must be a JSON array.");
        }

        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Every Alpaca trading-calendar entry must be a JSON object.");
            }

            var tradeDate = ParseRequiredDate(item);
            if (tradeDate < startDateInclusive || tradeDate > endDateInclusive)
            {
                throw new InvalidOperationException(
                    $"Alpaca trading-calendar returned {tradeDate:yyyy-MM-dd} outside the requested " +
                    $"range {startDateInclusive:yyyy-MM-dd} through {endDateInclusive:yyyy-MM-dd}.");
            }

            var open = ParseRequiredTime(item, "open", tradeDate);
            var close = ParseRequiredTime(item, "close", tradeDate);
            if (close <= open)
            {
                throw new InvalidOperationException(
                    $"Alpaca trading-calendar entry {tradeDate:yyyy-MM-dd} must close after it opens.");
            }

            if (!schedules.TryAdd(tradeDate, new MarketSessionSchedule(tradeDate, true, open, close)))
            {
                throw new InvalidOperationException(
                    $"Alpaca trading-calendar returned duplicate date {tradeDate:yyyy-MM-dd}.");
            }
        }

        return schedules;
    }

    private static DateOnly ParseRequiredDate(JsonElement item)
    {
        if (!item.TryGetProperty("date", out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(
                property.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var tradeDate))
        {
            throw new InvalidOperationException(
                "Every Alpaca trading-calendar entry requires a valid date in yyyy-MM-dd format.");
        }

        return tradeDate;
    }

    private static TimeOnly ParseRequiredTime(JsonElement item, string propertyName, DateOnly tradeDate)
    {
        if (!item.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !TimeOnly.TryParseExact(
                property.GetString(),
                ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            throw new InvalidOperationException(
                $"Alpaca trading-calendar entry {tradeDate:yyyy-MM-dd} requires a valid {propertyName} time.");
        }

        return time;
    }

}
