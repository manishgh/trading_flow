using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

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
