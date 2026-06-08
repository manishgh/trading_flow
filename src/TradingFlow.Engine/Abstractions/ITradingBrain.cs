using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Engine.Abstractions;

public interface ITradingBrain
{
    Task<IReadOnlyCollection<FinalizedOrder>> EvaluateAsync(
        IReadOnlyCollection<IndicatorSnapshot> marketState,
        CancellationToken cancellationToken);
}

