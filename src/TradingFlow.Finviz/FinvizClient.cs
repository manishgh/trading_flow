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
        var url = $"/export?{filterQuery}&auth={_options.AuthToken}";
        var response = await TradingFlow.Domain.Logging.ApiProfiler.ProfileAsync("Finviz", url, "GET", 
            () => _bulkhead.ExecuteAsync(ct => _httpClient.GetAsync(url, ct), cancellationToken));
        response.EnsureSuccessStatusCode();

        var csvContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseScreenerCsv(csvContent);
    }

    private static IReadOnlyList<string> ParseScreenerCsv(string csvContent)
    {
        var tickers = new List<string>();
        if (string.IsNullOrWhiteSpace(csvContent)) return tickers;
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
            if (!parser.EndOfData) parser.ReadFields(); // Header
            while (!parser.EndOfData)
            {
                try
                {
                    var fields = parser.ReadFields();
                    if (fields is not null && fields.Length >= 2)
                    {
                        tickers.Add(fields[1]);
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

        return tickers;
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
