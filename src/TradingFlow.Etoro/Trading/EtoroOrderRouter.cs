using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Etoro.Configuration;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.MarketData;
using TradingFlow.Etoro.Models;

namespace TradingFlow.Etoro.Trading;

public sealed class EtoroOrderRouter : IOrderRouter
{
    private readonly EtoroApiClient apiClient;
    private readonly EtoroOptions options;
    private readonly EtoroInstrumentResolver instrumentResolver;
    private readonly EtoroWriteSafetyGuard writeSafetyGuard;

    public EtoroOrderRouter(
        EtoroApiClient apiClient,
        EtoroOptions options,
        EtoroInstrumentResolver instrumentResolver,
        EtoroWriteSafetyGuard writeSafetyGuard)
    {
        this.apiClient = apiClient;
        this.options = options;
        this.instrumentResolver = instrumentResolver;
        this.writeSafetyGuard = writeSafetyGuard;
    }

    public async Task ExecuteBracketOrderAsync(FinalizedOrder order, CancellationToken cancellationToken)
    {
        if (!options.ActiveProfile.AllowTrading)
        {
            throw new InvalidOperationException($"eToro trading is disabled for {options.Environment} profile.");
        }

        var instrumentId = await instrumentResolver.ResolveInstrumentIdAsync(order.Ticker, cancellationToken);
        var requestPath = BuildOpenPath();

        if (options.OrderSizing == EtoroOrderSizingMode.Amount)
        {
            var amount = Decimal.Round(order.ShareQuantity * order.LimitPrice, 2);
            var request = new EtoroOpenOrderByAmountRequest(
                "open",
                "buy",
                order.Ticker.ToUpperInvariant(),
                instrumentId,
                options.DefaultSettlementType,
                "mkt",
                null,
                options.DefaultLeverage,
                amount,
                options.DefaultOrderCurrency,
                null,
                null,
                order.StopLossPrice,
                order.TakeProfitPrice,
                options.DefaultStopLossType,
                null,
                null);
            await writeSafetyGuard.RunOnceAsync(BuildOpenOperationKey(order, instrumentId), async () =>
            {
                _ = await apiClient.PostAsync<EtoroOrderResponse>(requestPath, request, allowUnsafeWriteRetry: false, cancellationToken);
            });
            return;
        }

        var unitsRequest = new EtoroOpenOrderByUnitsRequest(
            "open",
            "buy",
            order.Ticker.ToUpperInvariant(),
            instrumentId,
            options.DefaultSettlementType,
            "mkt",
            null,
            options.DefaultLeverage,
            null,
            options.DefaultOrderCurrency,
            order.ShareQuantity,
            null,
            order.StopLossPrice,
            order.TakeProfitPrice,
            options.DefaultStopLossType,
            null,
            null);
        await writeSafetyGuard.RunOnceAsync(BuildOpenOperationKey(order, instrumentId), async () =>
        {
            _ = await apiClient.PostAsync<EtoroOrderResponse>(requestPath, unitsRequest, allowUnsafeWriteRetry: false, cancellationToken);
        });
    }

    public Task<EtoroOrderResponse> ClosePositionAsync(string positionId, long instrumentId, decimal? units, CancellationToken cancellationToken)
    {
        if (!options.ActiveProfile.AllowTrading)
        {
            throw new InvalidOperationException($"eToro trading is disabled for {options.Environment} profile.");
        }

        var escapedPositionId = Uri.EscapeDataString(positionId);
        var path = options.Environment == EtoroEnvironment.Demo
            ? $"/trading/execution/demo/market-close-orders/positions/{escapedPositionId}"
            : $"/trading/execution/market-close-orders/positions/{escapedPositionId}";
        return writeSafetyGuard.RunOnceAsync(
            $"close:{options.Environment}:{positionId}:{instrumentId}:{units?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}",
            () => apiClient.PostAsync<EtoroOrderResponse>(
                path,
                new EtoroClosePositionRequest(instrumentId, units),
                allowUnsafeWriteRetry: false,
                cancellationToken));
    }

    private string BuildOpenPath()
    {
        return options.Environment == EtoroEnvironment.Demo
            ? "/api/v2/trading/execution/demo/orders"
            : "/api/v2/trading/execution/orders";
    }

    private string BuildOpenOperationKey(FinalizedOrder order, long instrumentId)
    {
        return String.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"open:{options.Environment}:{order.Ticker.ToUpperInvariant()}:{order.StrategyName}:{instrumentId}:{order.ShareQuantity}:{order.LimitPrice}:{order.StopLossPrice}:{order.TakeProfitPrice}:{order.ExecutionTimestamp:O}");
    }
}
