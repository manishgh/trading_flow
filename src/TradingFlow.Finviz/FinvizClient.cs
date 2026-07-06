using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using TradingFlow.Domain.Market;

namespace TradingFlow.Finviz;

public sealed class FinvizClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly FinvizOptions _options;
    private readonly Polly.Bulkhead.AsyncBulkheadPolicy<HttpResponseMessage> _bulkhead = TradingFlow.Domain.Http.RateLimiterFactory.CreateBulkhead(10, 50);

    public FinvizClient(HttpClient httpClient, FinvizOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = _options.BaseUrl;
    }

    /// <summary>
    /// Fetches tickers from a Finviz Screener export URL.
    /// Example: /export?v=111&f=fa_div_pos,sec_technology
    /// </summary>
    public async Task<IReadOnlyList<string>> GetScreenerTickersAsync(string filterQuery, CancellationToken cancellationToken = default)
    {
        var rows = await GetScreenerRowsAsync(filterQuery, cancellationToken);
        return rows.Select(x => x.Ticker).ToArray();
    }

    public async Task<IReadOnlyList<FinvizScreenerRow>> GetScreenerRowsAsync(string filterQuery, CancellationToken cancellationToken = default)
    {
        var normalizedFilterQuery = NormalizeScreenerFilterQuery(filterQuery);
        var separator = String.IsNullOrWhiteSpace(normalizedFilterQuery) ? "" : "&";
        var url = $"/export?{normalizedFilterQuery}{separator}auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET",
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();

        var csvContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseScreenerCsv(csvContent);
    }

    private static IReadOnlyList<FinvizScreenerRow> ParseScreenerCsv(string csvContent)
    {
        var rows = new List<FinvizScreenerRow>();
        if (string.IsNullOrWhiteSpace(csvContent)) return rows;
        if (csvContent.TrimStart().StartsWith("<", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("Finviz API returned HTML instead of CSV. Check Auth Token.");
        }

        using var reader = new StringReader(csvContent);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            Delimiters = new[] { "," },
            HasFieldsEnclosedInQuotes = true
        };

        try
        {
            var header = parser.EndOfData ? Array.Empty<string>() : parser.ReadFields() ?? Array.Empty<string>();
            var tickerIndex = FindHeaderIndex(header, "ticker");
            var relativeVolumeIndex = FindHeaderIndex(header, "relvolume", "relativevolume", "relvol", "rvol");

            while (!parser.EndOfData)
            {
                try
                {
                    var fields = parser.ReadFields();
                    if (fields is not null && fields.Length > tickerIndex)
                    {
                        var ticker = fields[tickerIndex].Trim().ToUpperInvariant();
                        if (ticker.Length == 0)
                        {
                            continue;
                        }

                        var relativeVolume = relativeVolumeIndex >= 0 && fields.Length > relativeVolumeIndex
                            ? ParseNullableDecimal(fields[relativeVolumeIndex])
                            : null;
                        rows.Add(new FinvizScreenerRow(ticker, relativeVolume));
                    }
                }
                catch (MalformedLineException)
                {
                    // Ignore malformed lines (e.g. trailing empty lines or Finviz footers)
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to parse Finviz CSV: {ex.Message}", ex);
        }

        return rows;
    }

    private static string NormalizeScreenerFilterQuery(string filterQuery)
    {
        if (String.IsNullOrWhiteSpace(filterQuery))
        {
            return String.Empty;
        }

        var trimmed = filterQuery.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            trimmed = absoluteUri.Query.TrimStart('?');
        }
        else if (trimmed.StartsWith("/export?", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("export?", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("/screener.ashx?", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("screener.ashx?", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[(trimmed.IndexOf('?', StringComparison.Ordinal) + 1)..];
        }
        else
        {
            trimmed = trimmed.TrimStart('?');
        }

        var parts = trimmed
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("auth=", StringComparison.OrdinalIgnoreCase));
        return String.Join('&', parts);
    }

    private static int FindHeaderIndex(IReadOnlyList<string> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var normalized = NormalizeHeader(header[i]);
            if (names.Any(name => normalized.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return names.Contains("ticker", StringComparer.OrdinalIgnoreCase) ? 1 : -1;
    }

    private static int FindHeaderIndexStrict(IReadOnlyList<string> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var normalized = NormalizeHeader(header[i]);
            if (names.Any(name => normalized.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeHeader(string value)
    {
        return new string(value.Where(Char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static decimal? ParseNullableDecimal(string value)
    {
        var normalized = value.Trim().TrimEnd('%').TrimEnd('x', 'X');
        return Decimal.TryParse(
            normalized,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;
    }

    private static DateTimeOffset ParseFinvizTimestamp(string value)
    {
        if (!DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed))
        {
            return DateTimeOffset.UtcNow;
        }

        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        var marketTimeZone = ResolveMarketTimeZone();
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, marketTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveMarketTimeZone()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    public async Task<string> GetStockDataAsync(string ticker, string period, CancellationToken cancellationToken = default)
    {
        // Example: /stock?t=MSFT&p=d&auth=xxx
        var url = $"/stock?t={ticker.ToUpperInvariant()}&p={period}&auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET",
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> GetNewsAsync(string ticker, CancellationToken cancellationToken = default)
    {
        // "v=3" - Stocks feed (no-ETFs)
        // "t=MSFT,AAPL" - Filter out only for specified tickers
        var url = $"/news?t={ticker.ToUpperInvariant()}&v=3&auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET",
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalystEvent>> GetNewsExportAsync(int view, CancellationToken cancellationToken = default)
    {
        var url = $"/export/news?v={view}&auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET",
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();

        var csvContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNewsExportCsv(csvContent, $"finviz-export-v{view}");
    }

    public static IReadOnlyList<CatalystEvent> ParseNewsExportCsv(string csvContent, string externalIdPrefix)
    {
        var rows = new List<CatalystEvent>();
        var receivedAt = DateTimeOffset.UtcNow;
        if (String.IsNullOrWhiteSpace(csvContent))
        {
            return rows;
        }

        if (csvContent.TrimStart().StartsWith("<", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("Finviz API returned HTML instead of CSV. Check Auth Token.");
        }

        using var reader = new StringReader(csvContent);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            Delimiters = new[] { "," },
            HasFieldsEnclosedInQuotes = true
        };

        var header = parser.EndOfData ? Array.Empty<string>() : parser.ReadFields() ?? Array.Empty<string>();
        var titleIndex = FindHeaderIndexStrict(header, "title", "headline", "news");
        var sourceIndex = FindHeaderIndex(header, "source");
        var dateIndex = FindHeaderIndex(header, "date");
        var urlIndex = FindHeaderIndex(header, "url");
        var categoryIndex = FindHeaderIndex(header, "category");
        var tickerIndex = FindHeaderIndexStrict(header, "ticker", "tickers", "symbol", "symbols");

        while (!parser.EndOfData)
        {
            string[]? fields;
            try
            {
                fields = parser.ReadFields();
            }
            catch (MalformedLineException)
            {
                continue;
            }

            if (fields is null)
            {
                continue;
            }

            var title = titleIndex >= 0 && fields.Length > titleIndex ? fields[titleIndex].Trim() : String.Empty;
            var source = sourceIndex >= 0 && fields.Length > sourceIndex ? fields[sourceIndex].Trim() : null;
            var articleUrl = urlIndex >= 0 && fields.Length > urlIndex ? fields[urlIndex].Trim() : null;
            var category = categoryIndex >= 0 && fields.Length > categoryIndex ? fields[categoryIndex].Trim() : null;
            if (String.IsNullOrWhiteSpace(title))
            {
                title = BuildFallbackNewsTitle(source, category, articleUrl);
            }

            var timestamp = dateIndex >= 0 && fields.Length > dateIndex
                ? ParseFinvizTimestamp(fields[dateIndex])
                : DateTimeOffset.UtcNow;

            var tickers = tickerIndex >= 0 && fields.Length > tickerIndex
                ? fields[tickerIndex]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.ToUpperInvariant())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();

            if (tickers.Length == 0)
            {
                tickers = new[] { "MARKET" };
            }

            foreach (var ticker in tickers)
            {
                rows.Add(new CatalystEvent(
                    ticker,
                    timestamp,
                    CatalystType.NewsReport,
                    title,
                    0m,
                    Provider: "finviz",
                    ExternalId: $"{externalIdPrefix}:{ticker}:{timestamp.UtcDateTime:O}:{title.GetHashCode(StringComparison.Ordinal)}",
                    Summary: category,
                    Source: source,
                    Url: articleUrl,
                    ReceivedAt: receivedAt));
            }
        }

        return rows
            .GroupBy(x => $"{x.Ticker}|{x.Url ?? x.Headline}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => x.Timestamp)
            .ToArray();
    }

    private static string BuildFallbackNewsTitle(string? source, string? category, string? articleUrl)
    {
        if (!String.IsNullOrWhiteSpace(source))
        {
            return $"{source.Trim()} update";
        }

        if (!String.IsNullOrWhiteSpace(category))
        {
            return $"{category.Trim()} update";
        }

        if (Uri.TryCreate(articleUrl, UriKind.Absolute, out var uri) && !String.IsNullOrWhiteSpace(uri.Host))
        {
            return $"{uri.Host.Replace("www.", String.Empty, StringComparison.OrdinalIgnoreCase)} update";
        }

        return "Finviz update";
    }

    public async Task<string> GetFilingsAsync(string ticker, string filter, CancellationToken cancellationToken = default)
    {
        // "o=-filingDate" - order descending by filing date
        // filter e.g. "annual-quarterly-current"
        var url = $"/stock?t={ticker.ToUpperInvariant()}&f={filter}&o=-filingDate&auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET",
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
