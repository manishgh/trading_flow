using System;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Execution;

public sealed record BrokerOrderReceipt(
    string BrokerOrderId,
    DateTimeOffset BrokerAcceptedAtUtc);

public sealed class BrokerOrderRejectedException(
    string operation,
    int statusCode,
    string message) : InvalidOperationException(message)
{
    public string Operation { get; } = operation;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Carries the exact entry contract approved by the execution layer to the broker adapter.
/// The adapter must submit these values as-is and must never silently change market orders
/// into limit orders or alter their time in force.
/// </summary>
public sealed record BrokerEntryOrder(
    FinalizedOrder Order,
    string Side,
    string OrderType,
    string TimeInForce,
    bool SubmitOutsideRegularHours);

public sealed record ProtectiveStopOrder(
    string Ticker,
    string Side,
    decimal Quantity,
    decimal StopPrice,
    string TimeInForce,
    string ClientOrderId);

/// <summary>
/// Reprices an existing ordinary protective stop. Alpaca creates a successor
/// order for a replacement, so the deterministic client ID belongs to that
/// successor and is the recovery key when the PATCH response is lost.
/// </summary>
public sealed record BrokerProtectiveOrderReplacement(
    string BrokerOrderId,
    string ReplacementClientOrderId,
    string OrderType,
    decimal StopPrice,
    decimal? LimitPrice);

public sealed record BrokerExitOrder(
    string Ticker,
    string Side,
    decimal Quantity,
    string OrderType,
    string TimeInForce,
    decimal? LimitPrice,
    decimal? StopPrice,
    bool SubmitOutsideRegularHours,
    string ClientOrderId);

public interface IBrokerOrderReader
{
    Task<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(
        CancellationToken cancellationToken);

    Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken);
}

public interface IAccountScopedBrokerOrderReader : IBrokerOrderReader, IBrokerAccountProvider
{
}

public interface IBrokerClient :
    IAccountScopedBrokerOrderReader,
    ITradingSessionProvider,
    IAssetTradingEligibilityProvider
{
    Task<BrokerOrderReceipt> SubmitOrderAsync(BrokerEntryOrder order, CancellationToken cancellationToken);
    Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(
        ProtectiveStopOrder order,
        CancellationToken cancellationToken);
    Task<BrokerOrderReceipt> SubmitExitOrderAsync(
        BrokerExitOrder order,
        CancellationToken cancellationToken);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken);
    Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken);
    Task<ActiveBrokerOrder> ReplaceProtectiveOrderAsync(
        BrokerProtectiveOrderReplacement replacement,
        CancellationToken cancellationToken);
    Task<System.Collections.Generic.IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken);
}
