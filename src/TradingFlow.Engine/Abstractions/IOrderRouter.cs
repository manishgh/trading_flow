using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Abstractions;

public interface IOrderRouter
{
    Task ExecuteBracketOrderAsync(FinalizedOrder order, CancellationToken cancellationToken);
}

