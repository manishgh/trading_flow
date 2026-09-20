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

        return await SubmitDurableExitAsync(
            normalizedTicker,
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
        var order = (await provider.GetOpenOrdersAsync(cancellationToken))
            .SingleOrDefault(candidate =>
                candidate.OrderId.Equals(brokerOrderId.Trim(), StringComparison.Ordinal));
        if (order is null)
        {
            throw new InvalidOperationException(
                $"Open broker order '{brokerOrderId}' was not found for cancellation.");
        }

        var state = await orderSubmissions.RequestCancelAsync(
            new OrderCancellationSubmission(
                order.ParentClientOrderId ?? order.ClientOrderId,
                order.OrderId,
                "operator_cancel_order",
                DateTimeOffset.UtcNow),
            provider,
            cancellationToken);
        return OrderStateMachine.IsTerminal(state.State);
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
        if (normalizedHorizon != "swing")
        {
            throw new InvalidOperationException("Operator-direct buy horizon must be swing.");
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
        var result = await orderSubmissions.SubmitEntryOrderAsync(
            new EntryOrderSubmission(
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

    private async Task<ManualOrderResult> SubmitDurableExitAsync(
        string normalizedTicker,
        decimal quantity,
        decimal limitPrice,
        bool allowExtendedHoursTrading,
        CancellationToken cancellationToken,
        Guid? ticketId,
        string orderType = "limit",
        decimal? triggerPrice = null,
        string timeInForce = "day")
    {
        var (_, provider) = GetClients();
        var now = DateTimeOffset.UtcNow;
        var brokerPosition = (await provider.GetOpenPositionsAsync(cancellationToken))
            .SingleOrDefault(position =>
                position.Ticker.Equals(normalizedTicker, StringComparison.OrdinalIgnoreCase));
        if (brokerPosition is null || brokerPosition.Qty <= 0m)
        {
            throw new InvalidOperationException(
                $"No open long paper position exists for {normalizedTicker}; sell is disabled to avoid accidental shorting.");
        }

        if (quantity > brokerPosition.Qty)
        {
            throw new InvalidOperationException(
                $"Sell quantity {quantity} exceeds open paper position {brokerPosition.Qty} for {normalizedTicker}.");
        }

        var runId = ticketId ?? Guid.NewGuid();
        var runContext = ExecutionRunContextFactory.Create(
            runId,
            "paper",
            new
            {
                policy = "operator_exit",
                symbol = normalizedTicker,
                quantity,
                orderType,
                timeInForce,
                limitPrice,
                triggerPrice,
                allowExtendedHoursTrading
            },
            now);
        var result = await orderSubmissions.SubmitPositionExitAsync(
            new PositionExitSubmission(
                runContext,
                normalizedTicker,
                quantity,
                "operator_exit",
                now,
                allowExtendedHoursTrading,
                orderType,
                timeInForce,
                orderType is "limit" or "stop_limit" ? limitPrice : null,
                orderType is "stop" or "stop_limit" ? triggerPrice : null),
            provider,
            cancellationToken);
        return new ManualOrderResult(
            result.BrokerOrderId,
            normalizedTicker,
            "sell",
            quantity,
            limitPrice);
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
