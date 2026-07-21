using System.Text.Json;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

// Guardian exit management for a live automation session: the polling monitor loop that decides
// when to close a position, plus safety-order placement, broker sell-order cancellation, and
// trailing-stop raises. Split into a partial file for readability; behavior is unchanged.
public sealed partial class MobileAutomationService
{
    private async Task MonitorExitAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        IMarketDataProvider provider,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var pollingInterval = TimeSpan.FromMinutes(1);
        session.Report("monitoring", $"Monitoring {session.Ticker} for strategy-managed exits.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var openPositions = await brokerClient.GetOpenPositionsAsync(cancellationToken);
            var position = openPositions.FirstOrDefault(x =>
                x.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                x.Qty > 0 &&
                !x.Side.Equals("short", StringComparison.OrdinalIgnoreCase));

            var openOrders = await brokerClient.GetOpenOrdersAsync(cancellationToken);
            if (orderLifecycleService is not null)
            {
                foreach (var order in openOrders.Where(order =>
                             order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
                             ClientOrderIdFactory.IsBindingFormat(order.ClientOrderId)))
                {
                    await orderLifecycleService.ApplyBrokerUpdateAsync(
                        BrokerOrderUpdateFactory.Create(order),
                        cancellationToken);
                }
            }

            if (position is null)
            {
                if (session.EntryOrderId is not null && openOrders.Any(x => x.OrderId == session.EntryOrderId))
                {
                    session.Report("waiting_fill", $"Waiting for {session.Ticker} entry order to fill.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                session.Status = "completed";
                session.FinishedAt = DateTimeOffset.UtcNow;
                session.Report("completed", $"No open position remains for {session.Ticker}. Session finished.");
                await PersistAsync(cancellationToken);
                return;
            }

            await EnsureSafetyExitOrdersAsync(session, runConfig, brokerClient, position, cancellationToken);

            var state = await LoadTickerStateAsync(runConfig, strategy, session.Ticker, provider, cancellationToken);
            if (!state.BarsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                !state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
                executionBars.Count == 0 ||
                executionSnapshots.Count == 0)
            {
                session.Report("monitoring", $"Execution timeframe state missing for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var entryTimestamp = session.EntrySubmittedAt ?? session.StartedAt ?? DateTimeOffset.UtcNow;
            var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
            if (entryIndex >= executionBars.Count)
            {
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var entryPrice = position.EntryPrice > 0m ? position.EntryPrice : session.EntryPrice ?? 0m;
            if (entryPrice <= 0m)
            {
                session.Report("monitoring", $"Entry price unavailable for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var stopLoss = session.StopLossPrice ?? (entryPrice - ((executionSnapshots[^1].Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple));
            var stopDistance = entryPrice - stopLoss;
            if (stopDistance <= 0m)
            {
                session.Report("monitoring", $"Stop distance invalid for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var takeProfit = session.TakeProfitPrice ?? (entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple));
            var guardianDecision = positionGuardianEngine.EvaluateLong(
                strategy,
                executionBars,
                executionSnapshots,
                entryTimestamp,
                entryPrice,
                stopLoss,
                takeProfit);

            if (guardianDecision.ShouldExit)
            {
                session.Report("submitting_exit", $"Exit triggered for {session.Ticker}: {guardianDecision.Reason}.");
                foreach (var order in openOrders.Where(order =>
                             order.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                             order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Failed to cancel sell order {OrderId} before closing {Ticker}.", order.OrderId, session.Ticker);
                    }
                }

                var closeQuantity = session.ShareQuantity ?? (int)Math.Floor(position.Qty);
                if (closeQuantity <= 0)
                {
                    session.Report("exit_rejected", $"Exit triggered for {session.Ticker}, but close quantity was unavailable.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                var closed = await brokerClient.ClosePositionAsync(session.Ticker, closeQuantity, cancellationToken);
                if (closed)
                {
                    session.Status = "completed";
                    session.FinishedAt = DateTimeOffset.UtcNow;
                    session.ExitReason = guardianDecision.Reason;
                    session.ExitSubmittedAt = DateTimeOffset.UtcNow;
                    session.Report("completed", $"Closed {session.Ticker} on {guardianDecision.Reason} at ~{guardianDecision.ExitPrice?.ToString("F2") ?? "market"}.");
                    await PersistAsync(cancellationToken);

                    if (auditRepo is not null)
                    {
                        await auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = session.RunName,
                            Ticker = session.Ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = DateTimeOffset.UtcNow,
                            Decision = "ExitSubmitted",
                            RejectionReason = guardianDecision.Reason,
                            SignalJson = JsonSerializer.Serialize(new
                            {
                                ticker = session.Ticker,
                                session.RunName,
                                exitReason = guardianDecision.Reason,
                                exitSignalTimestamp = guardianDecision.ExitSignalTimestamp,
                                estimatedExitPrice = guardianDecision.ExitPrice,
                                entryPrice,
                                currentPrice = position.CurrentPrice,
                                unrealizedPl = position.UnrealizedPl
                            })
                        }, cancellationToken);
                    }

                    return;
                }
            }
            else
            {
                await TryRaiseBrokerTrailingStopAsync(
                    session,
                    strategy,
                    brokerClient,
                    openOrders,
                    stopLoss,
                    guardianDecision.CurrentStopLossPrice,
                    cancellationToken);
            }

            session.LastObservedPrice = position.CurrentPrice;
            session.UnrealizedPl = position.UnrealizedPl;
            session.Report("monitoring", $"Holding {session.Ticker}. Last {position.CurrentPrice:F2}, unrealized {position.UnrealizedPl:F2}. Guardian={guardianDecision.Reason}.");
            await PersistAsync(cancellationToken);
            await Task.Delay(pollingInterval, cancellationToken);
        }
    }

    private async Task CancelOpenSellOrdersForTickerAsync(
        IBrokerClient brokerClient,
        string ticker,
        CancellationToken cancellationToken)
    {
        var openOrders = await brokerClient.GetOpenOrdersAsync(cancellationToken);
        foreach (var order in openOrders.Where(order =>
                     order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                     order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to cancel sell order {OrderId} before closing automation position for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
        }
    }

    private async Task EnsureSafetyExitOrdersAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        IBrokerClient brokerClient,
        BrokerPosition position,
        CancellationToken cancellationToken)
    {
        if (!runConfig.Execution.ExtendedHours || session.ExitSafetyOrdersSubmitted)
        {
            return;
        }

        var stopLoss = session.StopLossPrice;
        var takeProfit = session.TakeProfitPrice;
        if (stopLoss is null || takeProfit is null)
        {
            return;
        }

        await brokerClient.SubmitExitOrdersAsync(session.Ticker, (int)position.Qty, stopLoss.Value, takeProfit.Value, cancellationToken);
        session.ExitSafetyOrdersSubmitted = true;
        session.Report("monitoring", $"Submitted safety OCO exits for {session.Ticker}.");
        await PersistAsync(cancellationToken);
    }

    private async Task TryRaiseBrokerTrailingStopAsync(
        MutableAutomationSession session,
        StrategyDefinition strategy,
        IBrokerClient brokerClient,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        decimal initialStopLossPrice,
        decimal calculatedStopLossPrice,
        CancellationToken cancellationToken)
    {
        if (!strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            calculatedStopLossPrice <= (session.StopLossPrice ?? initialStopLossPrice) + 0.01m)
        {
            return;
        }

        var stopOrder = openOrders
            .Where(order =>
                order.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.StopPrice is not null)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();
        if (stopOrder is null)
        {
            session.Report("monitoring", $"Guardian calculated a raised stop for {session.Ticker} at {calculatedStopLossPrice:F2}, but no broker stop leg is open.");
            return;
        }

        if (stopOrder.StopPrice is { } brokerStopPrice &&
            calculatedStopLossPrice <= brokerStopPrice + 0.01m)
        {
            return;
        }

        var modified = await brokerClient.ModifyOrderAsync(stopOrder.OrderId, calculatedStopLossPrice, 0m, cancellationToken);
        if (!modified)
        {
            session.Report("monitoring", $"Broker rejected trailing stop raise for {session.Ticker}.");
            return;
        }

        session.StopLossPrice = calculatedStopLossPrice;
        session.Report("monitoring", $"Raised broker trailing stop for {session.Ticker} to {calculatedStopLossPrice:F2}.");
        await PersistAsync(cancellationToken);
    }

}
