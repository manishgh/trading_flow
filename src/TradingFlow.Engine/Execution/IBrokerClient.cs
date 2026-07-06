using System;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Execution;

public interface IBrokerClient
{
    Task<string> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken);
    Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken);
    Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken);
    Task<System.Collections.Generic.IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken);
    Task<System.Collections.Generic.IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken);
    Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken);
    Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken);
    Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken);
}
