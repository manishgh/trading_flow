using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
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
    private readonly IOrderSubmissionService orderSubmissions;
    private readonly ManualEntryOptions manualEntryOptions;
    private readonly object clientLock = new();
    private AlpacaTradingRestClient? tradingClient;
    private AlpacaBrokerClient? providerClient;

    public AlpacaManualOrderService(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        IOrderSubmissionService orderSubmissions,
        ManualEntryOptions manualEntryOptions)
    {
        this.credentials = credentials;
        this.rawArchiveWriter = rawArchiveWriter;
        this.orderSubmissions = orderSubmissions;
        this.manualEntryOptions = manualEntryOptions;
    }

    public async Task<ManualOrderResult> SubmitLimitOrderAsync(
        string ticker,
        string side,
        decimal quantity,
        decimal limitPrice,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        string? horizon,
        CancellationToken cancellationToken,
        bool allowExtendedHoursTrading = false)
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

        if (normalizedSide == "buy")
        {
            if (manualEntryOptions.Policy != ManualEntryPolicy.OperatorDirect)
            {
                throw new InvalidOperationException(
                    "Manual buy is strategy-gated. Start the selected strategy so it can produce a validated entry candidate.");
            }

            return await SubmitOperatorDirectBuyAsync(
                normalizedTicker,
                quantity,
                limitPrice,
                stopLossPrice,
                takeProfitPrice,
                horizon,
                allowExtendedHoursTrading,
                cancellationToken);
        }

        return await SubmitLimitOrderCoreAsync(
                normalizedTicker,
                normalizedSide,
                quantity,
                limitPrice,
                allowExtendedHoursTrading,
                cancellationToken);
    }

    private async Task<ManualOrderResult> SubmitOperatorDirectBuyAsync(
        string ticker,
        decimal quantity,
        decimal limitPrice,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        string? horizon,
        bool allowExtendedHoursTrading,
        CancellationToken cancellationToken)
    {
        if (quantity != Decimal.Truncate(quantity) || quantity > Int32.MaxValue)
        {
            throw new InvalidOperationException("Operator-direct buy quantity must be a positive whole-share value.");
        }

        var normalizedHorizon = horizon?.Trim().ToLowerInvariant();
        if (normalizedHorizon is not ("intraday" or "swing"))
        {
            throw new InvalidOperationException("Operator-direct buy horizon must be intraday or swing.");
        }

        if (stopLossPrice is not > 0m || stopLossPrice >= limitPrice)
        {
            throw new InvalidOperationException("Operator-direct buy requires a stop price below the entry limit.");
        }

        if (takeProfitPrice is not > 0m || takeProfitPrice <= limitPrice)
        {
            throw new InvalidOperationException("Operator-direct buy requires a take-profit price above the entry limit.");
        }

        if (allowExtendedHoursTrading)
        {
            throw new InvalidOperationException(
                "Operator-direct extended-hours entry is blocked because Alpaca does not support protected bracket entries outside regular hours.");
        }

        var (_, broker) = GetClients();
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        const string strategyId = "MANUAL-OPERATOR-DIRECT";
        var runContext = ExecutionRunContextFactory.Create(
            runId,
            "paper",
            new
            {
                policy = "operator_direct",
                symbol = ticker,
                quantity,
                limitPrice,
                stopLossPrice,
                takeProfitPrice,
                horizon = normalizedHorizon,
                allowExtendedHoursTrading
            },
            now);
        var intentId = OrderIntentIdFactory.Create(runId, strategyId, "buy", ticker, now);
        var candidateId = Guid.NewGuid();
        var result = await orderSubmissions.SubmitBracketOrderAsync(
            new BracketOrderSubmission(
                intentId,
                new ValidatedEntryCandidate(
                    candidateId,
                    "operator_direct",
                    normalizedHorizon,
                    now,
                    now,
                    JsonSerializer.Serialize(new
                    {
                        operatorOverride = true,
                        quantity,
                        limitPrice,
                        stopLossPrice,
                        takeProfitPrice
                    })),
                runContext,
                strategyId,
                "buy",
                "limit",
                "day",
                ExecutionRunContextFactory.ResolveSessionDate(now, "America/New_York"),
                now,
                new FinalizedOrder(
                    ticker,
                    "Manual Operator Direct",
                    Decimal.ToInt32(quantity),
                    limitPrice,
                    stopLossPrice.Value,
                    takeProfitPrice.Value,
                    now),
                AllowExtendedHoursTrading: false),
            broker,
            cancellationToken);

        return new ManualOrderResult(
            result.BrokerOrderId,
            ticker,
            "buy",
            quantity,
            limitPrice);
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
            providerClient = new AlpacaBrokerClient(
                new HttpClient(),
                new HttpClient(),
                options,
                rawArchiveWriter);
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
