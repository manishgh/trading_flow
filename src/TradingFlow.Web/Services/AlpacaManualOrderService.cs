using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

public sealed record ManualOrderResult(string OrderId, string Ticker, string Side, decimal Quantity, decimal LimitPrice);

/// <summary>
/// Manual paper-trading order helper for the wishlist UI. It intentionally keeps
/// sell conservative: sell is treated as closing an existing paper position, not
/// opening a short position from the watchlist.
/// </summary>
public sealed class AlpacaManualOrderService : IDisposable
{
    private readonly AlpacaCredentialProvider credentials;
    private readonly IRawArchiveWriter rawArchiveWriter;
    private readonly IEntryAdmissionControl entryAdmission;
    private readonly IPositionConflictGuard positionConflict;
    private readonly object clientLock = new();
    private AlpacaTradingRestClient? tradingClient;
    private AlpacaBrokerClient? providerClient;

    public AlpacaManualOrderService(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        IEntryAdmissionControl entryAdmission,
        IPositionConflictGuard positionConflict)
    {
        this.credentials = credentials;
        this.rawArchiveWriter = rawArchiveWriter;
        this.entryAdmission = entryAdmission;
        this.positionConflict = positionConflict;
    }

    public async Task<ManualOrderResult> SubmitLimitOrderAsync(
        string ticker,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken cancellationToken,
        bool allowExtendedHoursTrading = false)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        var normalizedSide = NormalizeSide(side);
        if (normalizedSide == "buy")
        {
            entryAdmission.EnsureEntriesAllowed();
        }
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

        return normalizedSide == "buy"
            ? await positionConflict.ExecuteEntryAsync(
                normalizedTicker,
                "MANUAL",
                token => SubmitLimitOrderCoreAsync(
                    normalizedTicker,
                    normalizedSide,
                    quantity,
                    limitPrice,
                    allowExtendedHoursTrading,
                    token),
                cancellationToken)
            : await SubmitLimitOrderCoreAsync(
                normalizedTicker,
                normalizedSide,
                quantity,
                limitPrice,
                allowExtendedHoursTrading,
                cancellationToken);
    }

    private async Task<ManualOrderResult> SubmitLimitOrderCoreAsync(
        string normalizedTicker,
        string normalizedSide,
        decimal quantity,
        decimal limitPrice,
        bool allowExtendedHoursTrading,
        CancellationToken cancellationToken)
    {
        var (client, provider) = GetClients();
        var now = DateTimeOffset.UtcNow;
        var session = await provider.GetSessionAsync(now, cancellationToken);
        ExtendedHoursOrderPolicy.Validate(
            session,
            orderType: "limit",
            timeInForce: "day",
            allowExtendedHoursTrading);
        if (session.Session == EquityTradingSession.Overnight)
        {
            var eligibility = await provider.GetEligibilityAsync(normalizedTicker, cancellationToken);
            ExtendedHoursOrderPolicy.ValidateOvernightAsset(normalizedTicker, eligibility);
        }

        var positionQuantity = await GetOpenPositionQuantityAsync(
            client,
            normalizedTicker,
            cancellationToken);
        if (normalizedSide == "sell")
        {
            if (positionQuantity <= 0m)
            {
                throw new InvalidOperationException($"No open paper position exists for {normalizedTicker}; sell is disabled to avoid accidental shorting.");
            }

            if (quantity > positionQuantity)
            {
                throw new InvalidOperationException($"Sell quantity {quantity} exceeds open paper position {positionQuantity} for {normalizedTicker}.");
            }
        }
        else
        {
            if (positionQuantity != 0m)
            {
                throw new PositionConflictException(
                    RejectCode.REJECT_SETUP_INVALID,
                    $"Position conflict for {normalizedTicker}: the broker already reports quantity {positionQuantity}.");
            }

            if (await HasOpenBrokerOrderAsync(client, normalizedTicker, cancellationToken))
            {
                throw new PositionConflictException(
                    RejectCode.REJECT_SETUP_INVALID,
                    $"Position conflict for {normalizedTicker}: the broker already has an open order for this symbol.");
            }
        }

        var clientOrderId = $"tf-manual-{normalizedSide}-{normalizedTicker}-{now:yyyyMMddHHmmssfff}";
        var submitOutsideRegularHours = session.Session != EquityTradingSession.Regular;
        object requestBody = submitOutsideRegularHours
            ? new
            {
                symbol = normalizedTicker,
                qty = quantity.ToString("0.########", CultureInfo.InvariantCulture),
                side = normalizedSide,
                type = "limit",
                time_in_force = "day",
                limit_price = FormatPrice(limitPrice),
                extended_hours = true,
                client_order_id = clientOrderId
            }
            : new
            {
                symbol = normalizedTicker,
                qty = quantity.ToString("0.########", CultureInfo.InvariantCulture),
                side = normalizedSide,
                type = "limit",
                time_in_force = "day",
                limit_price = FormatPrice(limitPrice),
                client_order_id = clientOrderId
            };

        using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/orders") { Content = content };
        var response = await client.SendAsync(
            request,
            "broker-manual-order",
            correlationId: clientOrderId,
            attributes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["side"] = normalizedSide,
                ["ticker"] = normalizedTicker
            },
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("manual paper order", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        var orderId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? String.Empty : String.Empty;
        if (String.IsNullOrWhiteSpace(orderId))
        {
            throw new InvalidOperationException(
                $"Alpaca manual paper order returned no order id. RawArchiveId={response.Archive.Manifest.ArchiveId}.");
        }

        return new ManualOrderResult(orderId, normalizedTicker, normalizedSide, quantity, limitPrice);
    }

    private static async Task<bool> HasOpenBrokerOrderAsync(
        AlpacaTradingRestClient client,
        string ticker,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/orders?status=open&symbols={Uri.EscapeDataString(ticker)}&limit=500&nested=true");
        var response = await client.SendAsync(
            request,
            "broker-manual-open-order-check",
            correlationId: ticker,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                AlpacaTradingRestClient.DescribeFailure("manual open-order query", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        return document.RootElement.ValueKind == JsonValueKind.Array &&
            document.RootElement.GetArrayLength() > 0;
    }

    private (AlpacaTradingRestClient Trading, AlpacaBrokerClient Provider) GetClients()
    {
        lock (clientLock)
        {
            if (tradingClient is not null && providerClient is not null)
            {
                return (tradingClient, providerClient);
            }

            var options = AlpacaOptions.Create(ProductionProfile.Paper) with
            {
                KeyId = credentials.KeyId,
                SecretKey = credentials.SecretKey
            };
            tradingClient = new AlpacaTradingRestClient(new HttpClient(), options, rawArchiveWriter);
            providerClient = new AlpacaBrokerClient(new HttpClient(), options, rawArchiveWriter);
            return (tradingClient, providerClient);
        }
    }

    public void Dispose()
    {
        lock (clientLock)
        {
            tradingClient?.Dispose();
            providerClient?.Dispose();
            tradingClient = null;
            providerClient = null;
        }
    }

    private static async Task<decimal> GetOpenPositionQuantityAsync(
        AlpacaTradingRestClient client,
        string ticker,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/positions/{Uri.EscapeDataString(ticker)}");
        var response = await client.SendAsync(
            request,
            "broker-manual-position-check",
            correlationId: ticker,
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0m;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("manual position query", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
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
