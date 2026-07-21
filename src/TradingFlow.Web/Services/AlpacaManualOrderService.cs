using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services;

public sealed record ManualOrderResult(string OrderId, string Ticker, string Side, decimal Quantity, decimal LimitPrice);

/// <summary>
/// Manual paper-trading order helper for the wishlist UI. It intentionally keeps
/// sell conservative: sell is treated as closing an existing paper position, not
/// opening a short position from the watchlist.
/// </summary>
public sealed class AlpacaManualOrderService
{
    private readonly AlpacaCredentialProvider credentials;

    public AlpacaManualOrderService(AlpacaCredentialProvider credentials)
    {
        this.credentials = credentials;
    }

    public async Task<ManualOrderResult> SubmitLimitOrderAsync(
        string ticker,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        var normalizedSide = NormalizeSide(side);
        if (quantity <= 0m)
        {
            throw new InvalidOperationException("Quantity must be greater than zero.");
        }

        if (limitPrice <= 0m)
        {
            throw new InvalidOperationException("A valid bid/ask price is required before placing an order.");
        }

        if (!credentials.IsConfigured)
        {
            throw new InvalidOperationException("Alpaca paper credentials are not configured.");
        }

        using var client = CreateClient();
        if (normalizedSide == "sell")
        {
            var positionQuantity = await GetOpenPositionQuantityAsync(client, normalizedTicker, cancellationToken);
            if (positionQuantity <= 0m)
            {
                throw new InvalidOperationException($"No open paper position exists for {normalizedTicker}; sell is disabled to avoid accidental shorting.");
            }

            if (quantity > positionQuantity)
            {
                throw new InvalidOperationException($"Sell quantity {quantity} exceeds open paper position {positionQuantity} for {normalizedTicker}.");
            }
        }

        var requestBody = new
        {
            symbol = normalizedTicker,
            qty = quantity.ToString("0.########", CultureInfo.InvariantCulture),
            side = normalizedSide,
            type = "limit",
            time_in_force = "day",
            limit_price = FormatPrice(limitPrice),
            extended_hours = true,
            client_order_id = $"tf-manual-{normalizedSide}-{normalizedTicker}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}"
        };

        using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v2/orders", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Alpaca paper order rejected: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        var orderId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? String.Empty : String.Empty;
        return new ManualOrderResult(orderId, normalizedTicker, normalizedSide, quantity, limitPrice);
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = AlpacaEndpointResolver.Resolve(ProductionProfile.Paper).TradingRest
        };
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", credentials.KeyId);
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", credentials.SecretKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<decimal> GetOpenPositionQuantityAsync(HttpClient client, string ticker, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/v2/positions/{Uri.EscapeDataString(ticker)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0m;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not read paper position for {ticker}: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("qty", out var qty) &&
            Decimal.TryParse(qty.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static string NormalizeTicker(string ticker)
    {
        if (String.IsNullOrWhiteSpace(ticker))
        {
            throw new InvalidOperationException("Ticker is required.");
        }

        return ticker.Trim().ToUpperInvariant();
    }

    private static string NormalizeSide(string side)
    {
        var normalized = side.Trim().ToLowerInvariant();
        return normalized is "buy" or "sell"
            ? normalized
            : throw new InvalidOperationException("Order side must be buy or sell.");
    }

    private static string FormatPrice(decimal price)
    {
        return price >= 1m
            ? price.ToString("0.00", CultureInfo.InvariantCulture)
            : price.ToString("0.0000", CultureInfo.InvariantCulture);
    }
}
