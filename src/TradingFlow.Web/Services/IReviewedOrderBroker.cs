using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Web.Services;

public interface IReviewedOrderBroker
{
    Task<ManualBrokerContext> GetBrokerContextAsync(string ticker, CancellationToken cancellationToken);
    Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken);
    Task<ManualOrderResult> SubmitStrategyEntryAsync(PreviewOrderRequest request, Guid intentId, CandidateRecord candidate, ProductionRun run, CancellationToken cancellationToken);
    Task<ManualOrderResult> SubmitLimitOrderAsync(string ticker, string side, decimal quantity, decimal limitPrice,
        decimal? stopLossPrice, decimal? takeProfitPrice, string? horizon, CancellationToken cancellationToken,
        bool allowExtendedHoursTrading = false, Guid? ticketId = null, string orderType = "limit", decimal? triggerPrice = null, string timeInForce = "day");
}
