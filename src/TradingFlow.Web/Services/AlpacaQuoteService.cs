using System.Globalization;
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
/// Reads Alpaca's latest stock quotes for the selected wishlist. This is kept
/// separate from the paper runner because watchlist display needs bid/ask now,
/// while strategy execution still owns its own warmed candle pipeline.
/// </summary>
public sealed class AlpacaQuoteService
{
    private readonly AlpacaCredentialProvider credentials;
    private readonly ILogger<AlpacaQuoteService> logger;

    public AlpacaQuoteService(AlpacaCredentialProvider credentials, ILogger<AlpacaQuoteService> logger)
    {
        this.credentials = credentials;
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
            using var client = new HttpClient
            {
                BaseAddress = TradingFlow.Alpaca.AlpacaEndpointResolver
                    .Resolve(TradingFlow.Engine.Configuration.ProductionProfile.Paper)
                    .MarketDataRest
            };
            client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", credentials.KeyId);
            client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", credentials.SecretKey);

            var normalizedFeed = NormalizeFeed(feed);
            var url = $"/v2/stocks/quotes/latest?symbols={String.Join(",", symbols.Select(Uri.EscapeDataString))}&feed={normalizedFeed}";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Alpaca latest quotes request failed with {StatusCode}: {Body}", response.StatusCode, body);
                return symbols.ToDictionary(ticker => ticker, ticker => Empty(ticker), StringComparer.OrdinalIgnoreCase);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var results = symbols.ToDictionary(ticker => ticker, ticker => Empty(ticker), StringComparer.OrdinalIgnoreCase);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Alpaca latest quotes request failed.");
            return symbols.ToDictionary(ticker => ticker, ticker => Empty(ticker), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static AlpacaLatestQuote Empty(string ticker) => new(ticker, null, null, null, null, null);

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
