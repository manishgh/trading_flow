using System.Text.Json;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

// Guardian exit management for a swing automation session: the polling monitor loop that decides
// when to close a position, plus safety-order placement, broker sell-order cancellation, and
// trailing-stop raises. Split into a partial file for readability; behavior is unchanged.
public sealed partial class MobileAutomationService
{
    private async Task MonitorExitAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        ExecutionRunContext executionRunContext,
        IMarketDataProvider provider,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var pollingInterval = TimeSpan.FromMinutes(1);
        session.Report("monitoring", $"Monitoring {session.Ticker} for strategy-managed exits.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var sessionSnapshot = session.ToSnapshot();
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
                if (sessionSnapshot.EntryOrderId is not null &&
                    openOrders.Any(x => x.OrderId == sessionSnapshot.EntryOrderId))
                {
                    session.Report("waiting_fill", $"Waiting for {session.Ticker} entry order to fill.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                session.Update(current =>
                {
                    current.Status = "completed";
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    current.Report("completed", $"No open position remains for {current.Ticker}. Session finished.");
                });
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

            sessionSnapshot = session.ToSnapshot();
            var entryTimestamp = sessionSnapshot.EntrySubmittedAt ?? sessionSnapshot.StartedAt ?? DateTimeOffset.UtcNow;
            var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
            if (entryIndex >= executionBars.Count)
            {
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var entryPrice = position.EntryPrice > 0m ? position.EntryPrice : sessionSnapshot.EntryPrice ?? 0m;
            if (entryPrice <= 0m)
            {
                session.Report("monitoring", $"Entry price unavailable for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var stopLoss = sessionSnapshot.StopLossPrice ?? (entryPrice - ((executionSnapshots[^1].Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple));
            var stopDistance = entryPrice - stopLoss;
            if (stopDistance <= 0m)
            {
                session.Report("monitoring", $"Stop distance invalid for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var takeProfit = sessionSnapshot.TakeProfitPrice ?? (entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple));
            var guardianDecision = positionGuardianEngine.EvaluateLong(
                strategy,
                executionBars,
                executionSnapshots,
                entryTimestamp,
                entryPrice,
                stopLoss,
                takeProfit);

            if (guardianDecision.ShouldExit || sessionSnapshot.ExitSubmittedAt is not null)
            {
                var durableExitReason = sessionSnapshot.ExitReason ?? guardianDecision.Reason;
                session.Report("submitting_exit", $"Exit triggered for {session.Ticker}: {durableExitReason}.");
                if (orderSubmissionService is null)
                {
                    throw new InvalidOperationException(
                        "Strategy exits require the durable order command service.");
                }

                var refreshedPosition = (await brokerClient.GetOpenPositionsAsync(cancellationToken))
                    .FirstOrDefault(candidate =>
                        candidate.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                        candidate.Qty > 0m &&
                        !candidate.Side.Equals("short", StringComparison.OrdinalIgnoreCase));
                if (refreshedPosition is null)
                {
                    session.Report("completed", $"{session.Ticker} is flat; no additional exit was submitted.");
                    await PersistAsync(cancellationToken);
                    continue;
                }

                var closeQuantity = Math.Min(
                    sessionSnapshot.ShareQuantity ?? (int)Math.Floor(refreshedPosition.Qty),
                    (int)Math.Floor(refreshedPosition.Qty));
                if (closeQuantity <= 0)
                {
                    session.Report("exit_rejected", $"Exit triggered for {session.Ticker}, but close quantity was unavailable.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                var submittedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
                OrderSubmissionResult exit;
                try
                {
                    exit = await orderSubmissionService.SubmitPositionExitAsync(
                        new PositionExitSubmission(
                            executionRunContext,
                            session.Ticker,
                            closeQuantity,
                            durableExitReason,
                            submittedAtUtc,
                            runConfig.Execution.AllowExtendedHoursTrading),
                        brokerClient,
                        cancellationToken);
                }
                catch (PositionExitNoLongerRequiredException)
                {
                    session.Report("completed", $"{session.Ticker} became flat while its owned protection was being resolved.");
                    await PersistAsync(cancellationToken);
                    continue;
                }
                session.Update(current =>
                {
                    current.Status = "running";
                    current.ExitReason = durableExitReason;
                    current.ExitSubmittedAt = submittedAtUtc;
                    current.Report(
                        "exit_submitted",
                        $"Exit submitted for {current.Ticker} on {durableExitReason}. ClientOrderId={exit.ClientOrderId}.");
                });
                await PersistAsync(cancellationToken);

                if (auditRepo is not null)
                {
                    await auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = session.RunName,
                        Ticker = session.Ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = submittedAtUtc,
                        Decision = "ExitSubmitted",
                        RejectionReason = guardianDecision.Reason,
                        SignalJson = JsonSerializer.Serialize(new
                        {
                            ticker = session.Ticker,
                            session.RunName,
                            exitReason = durableExitReason,
                            exit.IntentId,
                            exit.ClientOrderId,
                            exit.BrokerOrderId,
                            exitSignalTimestamp = guardianDecision.ExitSignalTimestamp,
                            estimatedExitPrice = guardianDecision.ExitPrice,
                            entryPrice,
                            currentPrice = position.CurrentPrice,
                            unrealizedPl = position.UnrealizedPl
                        })
                    }, cancellationToken);
                }

                await Task.Delay(pollingInterval, cancellationToken);
                continue;
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

            session.Update(current =>
            {
                current.LastObservedPrice = position.CurrentPrice;
                current.UnrealizedPl = position.UnrealizedPl;
                current.Report("monitoring", $"Holding {current.Ticker}. Last {position.CurrentPrice:F2}, unrealized {position.UnrealizedPl:F2}. Guardian={guardianDecision.Reason}.");
            });
            await PersistAsync(cancellationToken);
            await Task.Delay(pollingInterval, cancellationToken);
        }
    }

    private async Task EnsureSafetyExitOrdersAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        IBrokerClient brokerClient,
        BrokerPosition position,
        CancellationToken cancellationToken)
    {
        var sessionSnapshot = session.ToSnapshot();
        if (!runConfig.Execution.AllowExtendedHoursTrading || sessionSnapshot.ExitSafetyOrdersSubmitted)
        {
            return;
        }

        if (protectiveOrders is null)
        {
            throw new InvalidOperationException(
                "Extended-hours fill protection requires the durable protective-order service.");
        }

        var account = await brokerClient.GetAccountSnapshotAsync(cancellationToken);
        var openOrders = await brokerClient.GetOpenOrdersAsync(cancellationToken);
        var repairs = await protectiveOrders.EnsureAsync(
            brokerClient,
            account.AccountId,
            [position],
            openOrders,
            cancellationToken);
        if (repairs.Any(repair => !repair.Succeeded))
        {
            throw new InvalidOperationException(
                $"Protective-order repair failed for {session.Ticker}: " +
                String.Join("; ", repairs.Where(repair => !repair.Succeeded).Select(repair => repair.Detail)));
        }

        session.Update(current =>
        {
            current.ExitSafetyOrdersSubmitted = true;
            current.Report("monitoring", $"Durable broker protection verified for {current.Ticker}.");
        });
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
        var sessionSnapshot = session.ToSnapshot();
        if (!strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            calculatedStopLossPrice <= (sessionSnapshot.StopLossPrice ?? initialStopLossPrice) + 0.01m)
        {
            return;
        }

        var stopOrders = openOrders
            .Where(order =>
                order.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.OrderType is "stop" or "stop_limit" &&
                order.StopPrice is not null &&
                calculatedStopLossPrice > order.StopPrice.Value + 0.01m)
            .OrderByDescending(order => order.CreatedAt)
            .ToArray();
        if (stopOrders.Length == 0)
        {
            return;
        }

        if (orderSubmissionService is null)
        {
            throw new InvalidOperationException(
                "Trailing-stop replacement requires the common order command service.");
        }

        foreach (var stopOrder in stopOrders)
        {
            await orderSubmissionService.ReplaceProtectiveStopAsync(
                new ProtectiveStopReplacementSubmission(
                    stopOrder.ParentClientOrderId ?? stopOrder.ClientOrderId,
                    stopOrder.OrderId,
                    session.Ticker,
                    calculatedStopLossPrice,
                    "atr_trailing_stop_raise",
                    timeProvider.GetUtcNow().ToUniversalTime()),
                brokerClient,
                cancellationToken);
        }

        session.Update(current =>
        {
            current.StopLossPrice = calculatedStopLossPrice;
            current.Report("monitoring", $"Raised {stopOrders.Length} broker trailing stop tranche(s) for {current.Ticker} to {calculatedStopLossPrice:F2}.");
        });
        await PersistAsync(cancellationToken);
    }

}
