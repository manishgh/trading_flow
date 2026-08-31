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

public sealed record ManualBrokerContext(
    EquityTradingSession Session,
    DateOnly TradeDate,
    decimal BuyingPower,
    decimal Equity,
    decimal OpenPositionQuantity,
    bool AssetActive,
    bool AssetTradable,
    bool OvernightTradable);

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
        bool allowExtendedHoursTrading = false,
        Guid? ticketId = null,
        string orderType = "limit",
        decimal? triggerPrice = null,
        string timeInForce = "day")
    {
        var normalizedTicker = NormalizeTicker(ticker);
        var normalizedSide = NormalizeSide(side);
        if (quantity <= 0m)
        {
            throw new InvalidOperationException("Quantity must be greater than zero.");
        }

        var normalizedType = (orderType ?? "limit").Trim().ToLowerInvariant();
        var normalizedTif = (timeInForce ?? "day").Trim().ToLowerInvariant();

        // A market order carries no price, so the bid/ask requirement applies only to
        // the priced types.
        if (normalizedType is "limit" or "stop_limit" && limitPrice <= 0m)
        {
            throw new InvalidOperationException("A valid bid/ask price is required before placing an order.");
        }
        if (normalizedType is "stop" or "stop_limit" && triggerPrice is not > 0m)
        {
            throw new InvalidOperationException("A stop trigger price is required for a stop or stop-limit order.");
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
                cancellationToken,
                ticketId);
        }

        return await SubmitLimitOrderCoreAsync(
                normalizedTicker,
                normalizedSide,
                quantity,
                limitPrice,
                allowExtendedHoursTrading,
                cancellationToken,
                ticketId,
                normalizedType,
                triggerPrice,
                normalizedTif);
    }

    /// <summary>
    /// Cancels a working broker order.
    /// </summary>
    /// <remarks>
    /// Cancel is the only in-place order mutation offered. Changing price or quantity
    /// goes through cancel followed by a fresh reviewed ticket rather than a PATCH, so
    /// an amended order is re-checked against quote age, spread, session, and admission
    /// exactly like a new one. A silent amend would bypass that boundary.
    /// </remarks>
    public async Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(brokerOrderId))
        {
            throw new InvalidOperationException("A broker order id is required to cancel.");
        }

        if (!credentials.IsConfigured)
        {
            throw new InvalidOperationException("Alpaca paper credentials are not configured.");
        }

        var (_, provider) = GetClients();
        return await provider.CancelOrderAsync(brokerOrderId.Trim(), cancellationToken);
    }

    public async Task<ManualBrokerContext> GetBrokerContextAsync(
        string ticker,
        CancellationToken cancellationToken)
    {
        var normalizedTicker = NormalizeTicker(ticker);
        if (!credentials.IsConfigured)
        {
            throw new InvalidOperationException("Alpaca paper credentials are not configured.");
        }

        var (trading, provider) = GetClients();
        var session = await provider.GetSessionAsync(DateTimeOffset.UtcNow, cancellationToken);
        var eligibility = await provider.GetEligibilityAsync(normalizedTicker, cancellationToken)
            ?? throw new InvalidOperationException($"Alpaca returned no asset eligibility for {normalizedTicker}.");
        var account = await provider.GetAccountSnapshotAsync(cancellationToken);
        var positionQuantity = await GetOpenPositionQuantityAsync(trading, normalizedTicker, cancellationToken);
        return new ManualBrokerContext(
            session.Session,
            session.TradeDate,
            account.BuyingPower,
            account.Equity,
            positionQuantity,
            eligibility.Active,
            eligibility.Tradable,
            eligibility.OvernightTradable);
    }

    private async Task<ManualOrderResult> SubmitOperatorDirectBuyAsync(
        string ticker,
        decimal quantity,
        decimal limitPrice,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        string? horizon,
        bool allowExtendedHoursTrading,
        CancellationToken cancellationToken,
        Guid? ticketId)
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

        var (_, broker) = GetClients();
        var now = DateTimeOffset.UtcNow;
        var runId = ticketId ?? Guid.NewGuid();
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
        var intentId = ticketId ?? OrderIntentIdFactory.Create(runId, strategyId, "buy", ticker, now);
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
                AllowExtendedHoursTrading: allowExtendedHoursTrading,
                OperatorOverride: OperatorOverrideAuthorization.Issue(
                    manualEntryOptions,
                    "wishlist_operator",
                    "Explicit operator-direct wishlist buy",
                    now)),
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
        CancellationToken cancellationToken,
        Guid? ticketId,
        string orderType = "limit",
        decimal? triggerPrice = null,
        string timeInForce = "day")
    {
        var (client, provider) = GetClients();
        var now = DateTimeOffset.UtcNow;
        var session = await provider.GetSessionAsync(now, cancellationToken);
        // Passing the real settings means the policy refuses an extended-hours market
        // or stop order rather than silently accepting one described as limit/day.
        ExtendedHoursOrderPolicy.Validate(
            session,
            orderType: orderType == "stop_limit" ? "limit" : orderType,
            timeInForce: timeInForce,
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

        var clientOrderId = ticketId is { } stableTicketId
            ? $"tf-manual-{normalizedSide}-{stableTicketId:N}"
            : $"tf-manual-{normalizedSide}-{normalizedTicker}-{now:yyyyMMddHHmmssfff}";
        var submitOutsideRegularHours = session.Session != EquityTradingSession.Regular;

        // Built as a dictionary rather than two anonymous shapes so price fields are
        // present only for the types that carry them. Alpaca rejects a market order
        // that arrives with a limit_price.
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = normalizedTicker,
            ["qty"] = quantity.ToString("0.########", CultureInfo.InvariantCulture),
            ["side"] = normalizedSide,
            ["type"] = orderType,
            ["time_in_force"] = timeInForce,
            ["client_order_id"] = clientOrderId
        };
        if (orderType is "limit" or "stop_limit")
        {
            payload["limit_price"] = FormatPrice(limitPrice);
        }
        if (orderType is "stop" or "stop_limit" && triggerPrice is { } trigger)
        {
            payload["stop_price"] = FormatPrice(trigger);
        }
        if (submitOutsideRegularHours)
        {
            payload["extended_hours"] = true;
        }

        object requestBody = payload;

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
        var orderId = document.RootElement.TryGetProperty("id", out var orderIdElement)
            ? orderIdElement.GetString() ?? String.Empty
            : String.Empty;
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
