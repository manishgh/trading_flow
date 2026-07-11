using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Catalysts;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

// Technical-exit + broker trailing-stop handling (LiveRunner partial split).
public sealed partial class LiveRunner
{
    private async Task<bool> TrySubmitTechnicalExitAsync(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        string ticker,
        IReadOnlyDictionary<string, List<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderRepo is null ||
            run.Execution.DryRun ||
            !run.Execution.AllowLiveOrders)
        {
            return false;
        }

        var position = openPositions.FirstOrDefault(x =>
            x.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            x.Qty > 0 &&
            !x.Side.Equals("short", StringComparison.OrdinalIgnoreCase));
        if (position is null)
        {
            return false;
        }

        var activeOrders = await _orderRepo.GetActiveOrdersByTickerAsync(ticker, cancellationToken);
        var strategyOrder = activeOrders
            .Where(x => x.StrategyName.Equals(strategy.StrategyName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();
        if (strategyOrder is null)
        {
            logger.LogDebug(
                "Open position for {Ticker} has no active persisted order for strategy {StrategyName}; technical exit evaluation skipped.",
                ticker,
                strategy.StrategyName);
            return false;
        }

        if (strategyOrder.Status.Equals("technical_exit_submitted", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!barsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
            !snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionBars.Count == 0 ||
            executionSnapshots.Count == 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; execution timeframe {Timeframe} is missing.",
                ticker,
                strategy.StrategyName,
                strategy.Execution.Timeframe);
            return false;
        }

        var entryTimestamp = strategyOrder.CreatedAt;
        var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
        if (entryIndex >= executionBars.Count)
        {
            return true;
        }

        var entryPrice = position.EntryPrice > 0 ? position.EntryPrice : strategyOrder.EntryPrice;
        if (entryPrice <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; entry price is unavailable.",
                ticker,
                strategy.StrategyName);
            return false;
        }

        var latestSnapshot = executionSnapshots[^1];
        var initialStopLossPrice = strategyOrder.StopLossPrice > 0
            ? strategyOrder.StopLossPrice
            : entryPrice - ((latestSnapshot.Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple);
        var stopDistance = entryPrice - initialStopLossPrice;
        if (stopDistance <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; stop distance is invalid. Entry={EntryPrice} Stop={StopLossPrice}",
                ticker,
                strategy.StrategyName,
                entryPrice,
                initialStopLossPrice);
            return false;
        }

        var takeProfitPrice = strategyOrder.TakeProfitPrice > 0
            ? strategyOrder.TakeProfitPrice
            : entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);
        var currentStopLossPrice = initialStopLossPrice;
        var highestHighSinceEntry = entryPrice;

        string? exitReason = null;
        decimal? exitPrice = null;
        DateTimeOffset exitSignalTimestamp = DateTimeOffset.UtcNow;
        for (var index = entryIndex; index < executionBars.Count; index++)
        {
            var exitPriceLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var exitVolumeLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);
            if (_technicalExecutionEngine.ShouldExitLongOnConfirmedVwapFailure(
                strategy,
                executionSnapshots,
                index,
                entryIndex,
                entryPrice,
                stopDistance,
                Math.Max(highestHighSinceEntry, executionBars[index].High),
                index - entryIndex))
            {
                exitReason = "confirmed_vwap_failure";
                exitPrice = executionBars[index].Close;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }

            var (candidateExitPrice, candidateExitReason) = _technicalExecutionEngine.EvaluateBarForExit(
                strategy,
                executionBars[index],
                executionSnapshots[index],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                index - entryIndex,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                exitPriceLogTrend?.Slope,
                exitVolumeLogTrend?.Slope,
                index > 0 ? executionSnapshots[index - 1] : null);

            if (candidateExitReason is not null)
            {
                exitReason = candidateExitReason;
                exitPrice = candidateExitPrice;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }
        }

        if (exitReason is null)
        {
            await TryRaiseBrokerTrailingStopAsync(
                strategy,
                ticker,
                strategyOrder,
                openOrders,
                initialStopLossPrice,
                currentStopLossPrice,
                cancellationToken,
                progress);

            return false;
        }

        logger.LogInformation(
            "Technical exit triggered for {Ticker} {StrategyName}. Reason={ExitReason} SignalTime={SignalTime} EstimatedExitPrice={ExitPrice}",
            ticker,
            strategy.StrategyName,
            exitReason,
            exitSignalTimestamp,
            exitPrice);
        progress?.Report($"Technical exit triggered for {ticker}: {exitReason}. Closing broker position...");

        foreach (var order in openOrders.Where(order =>
            order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await _brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
                logger.LogInformation(
                    "Cancelled open sell order {OrderId} before technical exit close for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to cancel sell order {OrderId} before technical exit close for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
        }

        var closeQuantity = Math.Max(1, strategyOrder.ShareQuantity);
        var closed = await _brokerClient.ClosePositionAsync(ticker, closeQuantity, cancellationToken);
        if (closed)
        {
            await _orderRepo.UpdateOrderStatusAsync(strategyOrder.OrderId, "technical_exit_submitted", cancellationToken);
            _auditor.LogEvent(ticker, strategy.StrategyName, DateTimeOffset.UtcNow, ExecutionState.PositionClosed, $"Technical exit submitted: {exitReason}");
            if (_auditRepo is not null)
            {
                await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                {
                    RunName = run.RunName,
                    Ticker = ticker,
                    StrategyName = strategy.StrategyName,
                    Timestamp = DateTimeOffset.UtcNow,
                    Decision = "ExitSubmitted",
                    RejectionReason = exitReason,
                    SignalJson = JsonSerializer.Serialize(new
                    {
                        ticker,
                        strategy = strategy.StrategyName,
                        exitReason,
                        exitSignalTimestamp,
                        estimatedExitPrice = exitPrice,
                        entryPrice,
                        currentPrice = position.CurrentPrice,
                        unrealizedPl = position.UnrealizedPl
                    })
                }, cancellationToken);
            }

            progress?.Report($"Submitted technical exit close for {ticker}: {exitReason}.");
            return true;
        }

        logger.LogWarning(
            "Broker rejected technical exit close for {Ticker} {StrategyName}. Reason={ExitReason}",
            ticker,
            strategy.StrategyName,
            exitReason);
        if (_auditRepo is not null)
        {
            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
            {
                RunName = run.RunName,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "ExitRejected",
                RejectionReason = exitReason,
                SignalJson = JsonSerializer.Serialize(new
                {
                    ticker,
                    strategy = strategy.StrategyName,
                    exitReason,
                    entryPrice,
                    currentPrice = position.CurrentPrice,
                    unrealizedPl = position.UnrealizedPl
                })
            }, cancellationToken);
        }

        return true;
    }

    private async Task TryRaiseBrokerTrailingStopAsync(
        StrategyDefinition strategy,
        string ticker,
        PersistedOrder strategyOrder,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        decimal initialStopLossPrice,
        decimal calculatedStopLossPrice,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderRepo is null ||
            !strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            calculatedStopLossPrice <= strategyOrder.StopLossPrice + 0.01m)
        {
            return;
        }

        var stopOrder = openOrders
            .Where(order =>
                order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.StopPrice is not null)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();
        if (stopOrder is null)
        {
            logger.LogWarning(
                "Trailing stop for {Ticker} {StrategyName} calculated at {StopLossPrice}, but no open broker stop leg was found.",
                ticker,
                strategy.StrategyName,
                calculatedStopLossPrice);
            progress?.Report($"Trailing stop ready for {ticker} at {calculatedStopLossPrice:0.00}, but no broker stop leg is open.");
            return;
        }

        if (stopOrder.StopPrice is { } brokerStopPrice &&
            calculatedStopLossPrice <= brokerStopPrice + 0.01m)
        {
            return;
        }

        var modified = await _brokerClient.ModifyOrderAsync(stopOrder.OrderId, calculatedStopLossPrice, 0m, cancellationToken);
        if (!modified)
        {
            logger.LogWarning(
                "Broker did not accept trailing stop update for {Ticker} {StrategyName}. OrderId={OrderId} NewStop={StopLossPrice}",
                ticker,
                strategy.StrategyName,
                stopOrder.OrderId,
                calculatedStopLossPrice);
            return;
        }

        strategyOrder.StopLossPrice = calculatedStopLossPrice;
        strategyOrder.UpdatedAt = DateTimeOffset.UtcNow;
        await _orderRepo.SaveOrderAsync(strategyOrder, cancellationToken);

        logger.LogInformation(
            "Raised trailing stop for {Ticker} {StrategyName}. OrderId={OrderId} NewStop={StopLossPrice}",
            ticker,
            strategy.StrategyName,
            stopOrder.OrderId,
            calculatedStopLossPrice);
        progress?.Report($"Raised trailing stop for {ticker} to {calculatedStopLossPrice:0.00}.");
    }
}
