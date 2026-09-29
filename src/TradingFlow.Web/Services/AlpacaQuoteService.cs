using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using TradingFlow.Alpaca;

namespace TradingFlow.Web.Services;

public sealed record AlpacaLatestQuote(
    string Ticker,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? BidSize,
    decimal? AskSize,
    DateTimeOffset? Timestamp)
{
    public decimal? MidPrice => BidPrice is { } bid && AskPrice is { } ask ? (bid + ask) / 2m : AskPrice ?? BidPrice;

    public string DisplayPrice => Format(MidPrice);

    public string DisplayBid => Format(BidPrice);

    public string DisplayAsk => Format(AskPrice);

    public string BuyCaption => AskPrice is { } ask ? $"Buy {Format(ask)}" : "Buy";

    public string SellCaption => BidPrice is { } bid ? $"Sell {Format(bid)}" : "Sell";

    public static string Format(decimal? value)
    {
        if (value is null)
        {
            return "--";
        }

        return value.Value >= 1m
            ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The last completed regular-session close before today in New York, from
/// Alpaca's daily bars. The desk's Change and Change % read against it.
/// </summary>
/// <param name="Ticker">Upper-case symbol.</param>
/// <param name="Close">That session's closing price.</param>
/// <param name="SessionDate">The New York date of that session.</param>
public sealed record AlpacaPreviousClose(string Ticker, decimal Close, DateOnly SessionDate);

/// <summary>
/// Reads Alpaca's latest stock quotes for the selected wishlist. This is kept
/// separate from the paper runner because watchlist display needs bid/ask now,
/// while strategy execution still owns its own warmed candle pipeline.
/// </summary>
public sealed class AlpacaQuoteService
{
    internal const string HttpClientName = "AlpacaLatestQuotes";

    private readonly AlpacaCredentialProvider credentials;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<AlpacaQuoteService> logger;

    /// <summary>
    /// Previous closes by symbol and New York date. A close only changes once a
    /// day, so a symbol is read from Alpaca once per day; failures are not cached.
    /// </summary>
    private readonly ConcurrentDictionary<(string Symbol, DateOnly Date), AlpacaPreviousClose> previousCloses = new();

    private const int SnapshotBatchSize = 100;

    public AlpacaQuoteService(
        AlpacaCredentialProvider credentials,
        IHttpClientFactory httpClientFactory,
        ILogger<AlpacaQuoteService> logger)
    {
        this.credentials = credentials;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, AlpacaLatestQuote>> GetLatestQuotesAsync(
        IReadOnlyCollection<string> tickers,
        string feed,
        CancellationToken cancellationToken)
    {
        var symbols = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (symbols.Length == 0 || !credentials.IsConfigured)
        {
            return symbols.ToDictionary(ticker => ticker, ticker => Empty(ticker), StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            return await GetLatestQuotesBatchAsync(client, symbols, NormalizeFeed(feed), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Alpaca latest quotes request failed.");
            return symbols.ToDictionary(ticker => ticker, ticker => Empty(ticker), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Alpaca rejects a multi-symbol request when any symbol is invalid. Isolate the
    /// rejected symbol so one stale wishlist item cannot erase every healthy quote.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, AlpacaLatestQuote>> GetLatestQuotesBatchAsync(
        HttpClient client,
        IReadOnlyList<string> symbols,
        string feed,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/stocks/quotes/latest?symbols={String.Join(",", symbols.Select(Uri.EscapeDataString))}&feed={feed}");
        request.Headers.Add("APCA-API-KEY-ID", credentials.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", credentials.SecretKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ParseQuotes(symbols, body);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && symbols.Count > 1)
        {
            var invalidSymbol = TryReadInvalidSymbol(body, symbols);
            if (invalidSymbol is not null)
            {
                logger.LogWarning(
                    "Alpaca rejected invalid quote symbol {Ticker}; valid symbols will continue.",
                    invalidSymbol);
                var validSymbols = symbols
                    .Where(symbol => !symbol.Equals(invalidSymbol, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var validResults = await GetLatestQuotesBatchAsync(client, validSymbols, feed, cancellationToken);
                return MergeWithEmpty(validResults, invalidSymbol);
            }

            var midpoint = symbols.Count / 2;
            var left = await GetLatestQuotesBatchAsync(client, symbols.Take(midpoint).ToArray(), feed, cancellationToken);
            var right = await GetLatestQuotesBatchAsync(client, symbols.Skip(midpoint).ToArray(), feed, cancellationToken);
            return left.Concat(right).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        logger.LogWarning(
            "Alpaca latest quotes request failed for {TickerCount} symbol(s) with {StatusCode}: {Body}",
            symbols.Count,
            response.StatusCode,
            body);
        return symbols.ToDictionary(ticker => ticker, Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, AlpacaLatestQuote> ParseQuotes(
        IReadOnlyList<string> symbols,
        string body)
    {
        var results = symbols.ToDictionary(ticker => ticker, Empty, StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("quotes", out var quotes))
        {
            return results;
        }

        foreach (var quoteProperty in quotes.EnumerateObject())
        {
            var quote = quoteProperty.Value;
            results[quoteProperty.Name.ToUpperInvariant()] = new AlpacaLatestQuote(
                quoteProperty.Name.ToUpperInvariant(),
                ReadDecimal(quote, "bp"),
                ReadDecimal(quote, "ap"),
                ReadDecimal(quote, "bs"),
                ReadDecimal(quote, "as"),
                ReadTimestamp(quote, "t"));
        }

        return results;
    }

    private static string? TryReadInvalidSymbol(string body, IReadOnlyList<string> requestedSymbols)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("message", out var messageProperty))
            {
                return null;
            }

            const string marker = "invalid symbol:";
            var message = messageProperty.GetString() ?? String.Empty;
            var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var candidate = message[(markerIndex + marker.Length)..].Trim();
            return requestedSymbols.FirstOrDefault(symbol => symbol.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, AlpacaLatestQuote> MergeWithEmpty(
        IReadOnlyDictionary<string, AlpacaLatestQuote> results,
        string invalidSymbol)
    {
        var merged = results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        merged[invalidSymbol] = Empty(invalidSymbol);
        return merged;
    }

    private static AlpacaLatestQuote Empty(string ticker) => new(ticker, null, null, null, null, null);

    /// <summary>
    /// The previous close for each symbol, from Alpaca's snapshot endpoint
    /// (<c>/v2/stocks/snapshots</c>, its <c>dailyBar</c> and <c>prevDailyBar</c>).
    /// A symbol Alpaca cannot answer for is simply absent: the desk shows its
    /// change as unknown rather than inventing a reference price.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AlpacaPreviousClose>> GetPreviousClosesAsync(
        IReadOnlyCollection<string> tickers,
        string feed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var today = NewYorkDate(now);
        var symbols = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = new Dictionary<string, AlpacaPreviousClose>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            if (previousCloses.TryGetValue((symbol, today), out var cached))
            {
                results[symbol] = cached;
            }
        }

        var missing = symbols.Where(symbol => !results.ContainsKey(symbol)).ToArray();
        if (missing.Length == 0 || !credentials.IsConfigured)
        {
            return results;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            foreach (var batch in missing.Chunk(SnapshotBatchSize))
            {
                foreach (var (symbol, close) in await GetSnapshotBatchAsync(client, batch, NormalizeFeed(feed), today, cancellationToken))
                {
                    results[symbol] = close;
                    previousCloses[(symbol, today)] = close;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Alpaca snapshots request failed; previous closes stay unknown.");
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<string, AlpacaPreviousClose>> GetSnapshotBatchAsync(
        HttpClient client,
        IReadOnlyList<string> symbols,
        string feed,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/stocks/snapshots?symbols={String.Join(",", symbols.Select(Uri.EscapeDataString))}&feed={feed}");
        request.Headers.Add("APCA-API-KEY-ID", credentials.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", credentials.SecretKey);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ParsePreviousCloses(body, today);
        }

        // One invalid symbol rejects the whole request; drop it and read the rest.
        if (response.StatusCode == HttpStatusCode.BadRequest && symbols.Count > 1 &&
            TryReadInvalidSymbol(body, symbols) is { } invalidSymbol)
        {
            var valid = symbols.Where(symbol => !symbol.Equals(invalidSymbol, StringComparison.OrdinalIgnoreCase)).ToArray();
            return await GetSnapshotBatchAsync(client, valid, feed, today, cancellationToken);
        }

        logger.LogWarning(
            "Alpaca snapshots request failed for {TickerCount} symbol(s) with {StatusCode}: {Body}",
            symbols.Count,
            response.StatusCode,
            body);
        return new Dictionary<string, AlpacaPreviousClose>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads previous closes from a snapshots response. The multi-symbol response
    /// is keyed by symbol; a <c>snapshots</c> wrapper is accepted too. Either bar
    /// may be null. Pure, so the selection rule is testable against fixtures.
    /// </summary>
    internal static IReadOnlyDictionary<string, AlpacaPreviousClose> ParsePreviousCloses(string body, DateOnly today)
    {
        var results = new Dictionary<string, AlpacaPreviousClose>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.TryGetProperty("snapshots", out var wrapped) ? wrapped : document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var bars = new[] { "dailyBar", "prevDailyBar" }
                .Select(name => entry.Value.TryGetProperty(name, out var bar) && bar.ValueKind == JsonValueKind.Object
                    ? (Timestamp: ReadTimestamp(bar, "t"), Close: ReadDecimal(bar, "c"))
                    : (Timestamp: (DateTimeOffset?)null, Close: (decimal?)null))
                .Where(bar => bar.Timestamp is not null && bar.Close is > 0m)
                .Select(bar => (bar.Timestamp!.Value, bar.Close!.Value));
            if (SelectPreviousClose(bars, today) is { } selected)
            {
                var symbol = entry.Name.ToUpperInvariant();
                results[symbol] = new AlpacaPreviousClose(symbol, selected.Close, selected.Date);
            }
        }

        return results;
    }

    /// <summary>
    /// The most recent daily bar whose New York date is before today. In the
    /// regular session and after hours today's bar is skipped for yesterday's;
    /// before the open the latest bar already is the previous session.
    /// </summary>
    internal static (decimal Close, DateOnly Date)? SelectPreviousClose(
        IEnumerable<(DateTimeOffset Timestamp, decimal Close)> dailyBars,
        DateOnly today)
    {
        return dailyBars
            .Select(bar => (bar.Close, Date: NewYorkDate(bar.Timestamp)))
            .Where(bar => bar.Date < today)
            .OrderByDescending(bar => bar.Date)
            .Select(bar => ((decimal Close, DateOnly Date)?)bar)
            .FirstOrDefault();
    }

    internal static DateOnly NewYorkDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, NewYork).DateTime);

    private static readonly TimeZoneInfo NewYork = ResolveNewYork();

    // Same resolution as the operational strip: IANA id, else the Windows id.
    private static TimeZoneInfo ResolveNewYork()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private static string NormalizeFeed(string feed)
    {
        var normalized = String.IsNullOrWhiteSpace(feed) ? "sip" : feed.Trim().ToLowerInvariant();
        return normalized is "sip" or "iex" or "delayed_sip" or "overnight" or "otc" ? normalized : "sip";
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.String when Decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}
