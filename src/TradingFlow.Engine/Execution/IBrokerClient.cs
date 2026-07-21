using System;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Execution;

public sealed record BrokerOrderReceipt(
    string BrokerOrderId,
    DateTimeOffset BrokerAcceptedAtUtc);

public sealed record ProtectiveStopOrder(
    string Ticker,
    string Side,
    decimal Quantity,
    decimal StopPrice,
    string TimeInForce,
    string ClientOrderId);

public interface IBrokerOrderReader
{
    Task<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(
        CancellationToken cancellationToken);

    Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken);
}

public interface IBrokerClient : IBrokerOrderReader
{
    Task<BrokerOrderReceipt> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken);
    Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(
        ProtectiveStopOrder order,
        CancellationToken cancellationToken);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken);
    Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken);
    Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken);
    Task<System.Collections.Generic.IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken);
    Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken);
    Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken);
    Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken);
}
