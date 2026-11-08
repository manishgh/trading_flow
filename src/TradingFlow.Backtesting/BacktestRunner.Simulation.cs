using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

// Trade simulation and exit engine for BacktestRunner: entry candidate creation (long/short),
// bar-by-bar exit evaluation, risk/stop sizing, slippage application, and structural helpers.
// Split into a partial file for readability; behavior is identical to the inline version.
public sealed partial class BacktestRunner
{
    private static bool AllowsLong(StrategyDefinition strategy)
    {
        return strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsShort(StrategyDefinition strategy)
    {
        return strategy.EntryRules.EnableShort &&
            (strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoosePrimaryRejection(string longRejection, string shortRejection)
    {
        if (!longRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return longRejection;
        }

        if (!shortRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return shortRejection;
        }

        return longRejection;
    }

    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateCandidate(
        string candidateId,
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        StrategyOrderPlan canonicalOrderPlan,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor,
        CancellationToken cancellationToken,
        int? plannedEntryIndex = null)
    {
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var entryIndex = plannedEntryIndex ?? FindFirstBarIndexAtOrAfter(bars, signalCloseTimestamp);
        if (entryIndex >= bars.Count)
        {
            return null;
        }

        var stopContextIndex = canonicalOrderPlan.StopContextIndex;
        if (stopContextIndex < 0 ||
            stopContextIndex >= entryIndex ||
            stopContextIndex >= snapshots.Count)
        {
            return null;
        }

        var entryBar = bars[entryIndex];
        var stopContextSnapshot = snapshots[stopContextIndex];
        var relativeVolume = stopContextSnapshot.RelativeVolume ?? 1.0m;
        if (canonicalOrderPlan.Direction.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            return CreateShortCandidate(
                candidateId,
                run,
                strategy,
                signal,
                canonicalOrderPlan,
                entryIndex,
                stopContextIndex,
                bars,
                snapshots,
                auditor,
                relativeVolume,
                cancellationToken);
        }

        // Approximate trade amount based on portfolio config
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyLongSlippage(entryBar.Open, relativeVolume, strategy.Execution.SlippageBps, approximateTradeAmount);

        var initialStopLossPrice = canonicalOrderPlan.InitialStopPrice;
        // The open is unknown when the decision is made, so a kept-gap plan uses only the prior close.
        var planningPrice = strategy.Execution.KeepsOpeningGaps() ? bars[entryIndex - 1].Close : entryPrice;
        var stopDistance = planningPrice - initialStopLossPrice;
        if ((strategy.ExitRules.HasProtectiveStop && initialStopLossPrice <= 0m) || stopDistance <= 0m)
        {
            return null;
        }

        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = ResolveTakeProfitPrice(
            canonicalOrderPlan,
            planningPrice,
            stopDistance);
        if (strategy.ExitRules.HasProfitTarget && takeProfitPrice <= planningPrice)
        {
            return null;
        }
        var entryTimestamp = entryBar.Timestamp;
        var highestHighSinceEntry = Math.Max(entryPrice, entryBar.High);

        // The entry is filled from this completed bar. Protective orders become
        // known at its close and can first participate in the next eligible bar.
        var exitStartIndex = FindNextValidExecutionBarIndex(bars, entryIndex + 1, strategy);
        if (exitStartIndex >= bars.Count)
        {
            auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, ExecutionState.SignalGenerated,
                "Completed signal retained without an executable exit horizon", candidateId);
            return (BuildOpenCandidate(run, candidateId, strategy, signal, "long", entryTimestamp,
                entryPrice, initialStopLossPrice, takeProfitPrice, stopDistance, bars[entryIndex - 1].Volume), entryIndex);
        }

        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"LONG signal generated at {signal.CurrentPrice}", candidateId);
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})", candidateId);

        if (strategy.Execution.KeepsOpeningGaps() && entryBar.Open <= initialStopLossPrice)
        {
            var gapExitBar = bars[exitStartIndex];
            var gapExitPrice = ApplyLongExitSlippage(gapExitBar.Open, snapshots[exitStartIndex].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
            auditor.LogEvent(signal.Ticker, strategy.StrategyName, gapExitBar.Timestamp, ExecutionState.PositionClosed, $"Exit via {GapThroughStopExitReason} at {gapExitPrice}", candidateId);
            return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice,
                gapExitBar.Timestamp, gapExitPrice, GapThroughStopExitReason, stopDistance, bars[entryIndex - 1].Volume), exitStartIndex);
        }

        var technicalEngine = new TradingFlow.Engine.Execution.TechnicalExecutionEngine();

        BacktestPendingExitDecision? pendingExit = null;
        var eligibleBarsHeld = 1;
        for (var i = exitStartIndex; i < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
            // This boundary is frozen before prices are read; never choose it from the final observed stock bar.
            if (run.Engine.PrecommittedLiquidationSourceUtc == bar.Timestamp)
            {
                auditor.LogEvent(signal.Ticker,strategy.StrategyName,bar.Timestamp,ExecutionState.PositionClosed,
                    "Exit via scheduled_study_exit at precommitted session open",candidateId);
                return (BuildCandidate(candidateId,strategy,signal,"long",entryTimestamp,entryPrice,
                    initialStopLossPrice,takeProfitPrice,bar.Timestamp,bar.Open,"scheduled_study_exit",
                    stopDistance,bars[entryIndex-1].Volume),i);
            }
            if (!_sessionClock.ValidateExecutionWindow(
                    bar.Timestamp,
                    strategy.Execution.Timeframe,
                    strategy.Session))
            {
                continue;
            }

            var barsHeld = eligibleBarsHeld;
            eligibleBarsHeld++;
            var exitPriceLogTrend = TradingFlow.Engine.Execution.TechnicalExecutionEngine.ComputeLogTrend(
                bars,
                i,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var exitVolumeLogTrend = TradingFlow.Engine.Execution.TechnicalExecutionEngine.ComputeLogTrend(
                bars,
                i,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);
            var previousEligibleIndex = FindPreviousValidExecutionBarIndex(bars, i - 1, strategy);
            if (previousEligibleIndex >= entryIndex)
            {
                currentStopLossPrice = technicalEngine.CalculateEffectiveStopLoss(
                    strategy,
                    snapshots[previousEligibleIndex],
                    entryPrice,
                    stopDistance,
                    initialStopLossPrice,
                    currentStopLossPrice,
                    highestHighSinceEntry);
            }
            if (strategy.ExitRules.HasProtectiveStop && bar.Low <= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice > initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyLongExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {stopExitReason} at {slippedStopPrice}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, currentStopLossPrice, stopExitReason, stopDistance, bars[entryIndex - 1].Volume), i);
            }

            if (strategy.ExitRules.HasProfitTarget && bar.High >= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyLongExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via take_profit at {slippedTakeProfit}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, takeProfitPrice, "take_profit", stopDistance, bars[entryIndex - 1].Volume), i);
            }

            if (technicalEngine.ShouldExitLongOnConfirmedVwapFailure(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Max(highestHighSinceEntry, bar.High), barsHeld))
            {
                var nextExitIndex = FindNextValidExecutionBarIndex(
                    bars,
                    i + 1,
                    strategy);
                var hasNextExitBar = nextExitIndex < bars.Count;
                if (!hasNextExitBar)
                {
                    pendingExit ??= PendingExitDecision(run, strategy, bar, "confirmed_vwap_failure");
                    continue;
                }

                var exitBar = hasNextExitBar ? bars[nextExitIndex] : bar;
                var exitSnapshot = hasNextExitBar ? snapshots[nextExitIndex] : snapshots[i];
                var exitBasis = hasNextExitBar ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyLongExitSlippage(exitBasis, (run.Engine.PrecommittedLiquidationSourceUtc == exitBar.Timestamp ? snapshots[i].RelativeVolume : exitSnapshot.RelativeVolume) ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via confirmed_vwap_failure at {confirmedExitPrice}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_failure", stopDistance, bars[entryIndex - 1].Volume), hasNextExitBar ? nextExitIndex : i);
            }

            var strategyWithoutTrailing = strategy with
            {
                ExitRules = strategy.ExitRules with { EnableAtrTrailingStop = false }
            };
            var (exitPrice, exitReason) = technicalEngine.EvaluateBarForExit(
                strategyWithoutTrailing,
                bar,
                snapshots[i],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                barsHeld,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                exitPriceLogTrend?.Slope,
                exitVolumeLogTrend?.Slope,
                previousEligibleIndex >= 0 ? snapshots[previousEligibleIndex] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                // In backtest, for technical exit, we exit on next bar open
                if (!IsPriceTriggeredExitReason(exitReason))
                {
                    var nextExitIndex = FindNextValidExecutionBarIndex(
                        bars,
                        i + 1,
                        strategy);
                    if (nextExitIndex >= bars.Count)
                    {
                        pendingExit ??= PendingExitDecision(run, strategy, bar, exitReason);
                        continue;
                    }

                    var nextBar = bars[nextExitIndex];
                    var nextRelativeVolume = run.Engine.PrecommittedLiquidationSourceUtc == nextBar.Timestamp
                        ? snapshots[i].RelativeVolume ?? relativeVolume
                        : snapshots[nextExitIndex].RelativeVolume ?? relativeVolume;
                    var nextExitPrice = ApplyLongExitSlippage(nextBar.Open, nextRelativeVolume, strategy, approximateTradeAmount);
                    auditor.LogEvent(signal.Ticker, strategy.StrategyName, nextBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {nextExitPrice}", candidateId);
                    return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, nextBar.Timestamp, nextExitPrice, exitReason, stopDistance, bars[entryIndex - 1].Volume), nextExitIndex);
                }

                var isProtectiveExit = IsPriceTriggeredExitReason(exitReason);
                var slippedExitPrice = ApplyLongExitSlippage(
                    exitPrice.Value,
                    snapshots[i].RelativeVolume ?? relativeVolume,
                    strategy,
                    approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {slippedExitPrice}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, isProtectiveExit ? exitPrice.Value : slippedExitPrice, exitReason, stopDistance, bars[entryIndex - 1].Volume), i);
            }
        }

        return (BuildOpenCandidate(run, candidateId, strategy, signal, "long", entryTimestamp,
            entryPrice, initialStopLossPrice, takeProfitPrice, stopDistance, bars[entryIndex - 1].Volume)
            with { PendingExitDecision = pendingExit }, bars.Count - 1);
    }

    // Replay one short from its next-open fill to its first stop, target, technical or time exit; mirror of the long builder.
    // Without a protective stop the risk unit is the planning price and only the strategy's own exits close the position.
    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateShortCandidate(
        string candidateId,
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        StrategyOrderPlan canonicalOrderPlan,
        int entryIndex,
        int stopContextIndex,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor,
        decimal relativeVolume,
        CancellationToken cancellationToken)
    {
        var entryBar = bars[entryIndex];
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = ApplyShortEntrySlippage(entryBar.Open, relativeVolume, strategy, approximateTradeAmount);
        var initialStopLossPrice = canonicalOrderPlan.InitialStopPrice;
        var planningPrice = strategy.Execution.KeepsOpeningGaps() ? bars[entryIndex - 1].Close : entryPrice;
        // Without a protective stop the risk unit is the planning price, mirroring the long side.
        var stopDistance = strategy.ExitRules.HasProtectiveStop ? initialStopLossPrice - planningPrice : planningPrice;
        if ((strategy.ExitRules.HasProtectiveStop && initialStopLossPrice <= 0m) || stopDistance <= 0m)
        {
            return null;
        }

        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = ResolveTakeProfitPrice(
            canonicalOrderPlan,
            planningPrice,
            stopDistance);
        if (strategy.ExitRules.HasProfitTarget && (takeProfitPrice <= 0 || takeProfitPrice >= planningPrice))
        {
            return null;
        }

        var entryTimestamp = entryBar.Timestamp;
        var lowestLowSinceEntry = Math.Min(entryPrice, entryBar.Low);
        var exitStartIndex = FindNextValidExecutionBarIndex(bars, entryIndex + 1, strategy);
        if (exitStartIndex >= bars.Count)
        {
            auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, ExecutionState.SignalGenerated,
                "Completed short signal retained without an executable exit horizon", candidateId);
            return (BuildOpenCandidate(run, candidateId, strategy, signal, "short", entryTimestamp,
                entryPrice, initialStopLossPrice, takeProfitPrice, stopDistance, bars[entryIndex - 1].Volume), entryIndex);
        }

        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"SHORT signal generated at {signal.CurrentPrice}", candidateId);
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated short fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})", candidateId);

        if (strategy.Execution.KeepsOpeningGaps() && strategy.ExitRules.HasProtectiveStop && entryBar.Open >= initialStopLossPrice)
        {
            var gapExitBar = bars[exitStartIndex];
            var gapExitPrice = ApplyShortExitSlippage(gapExitBar.Open, snapshots[exitStartIndex].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
            auditor.LogEvent(signal.Ticker, strategy.StrategyName, gapExitBar.Timestamp, ExecutionState.PositionClosed, $"Exit via {GapThroughStopExitReason} at {gapExitPrice}", candidateId);
            return (BuildCandidate(candidateId, strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice,
                gapExitBar.Timestamp, gapExitPrice, GapThroughStopExitReason, stopDistance, bars[entryIndex - 1].Volume), exitStartIndex);
        }

        BacktestPendingExitDecision? pendingExit = null;
        var eligibleBarsHeld = 1;
        for (var i = exitStartIndex; i < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
            // This boundary is frozen before prices are read; never choose it from the final observed stock bar.
            if (run.Engine.PrecommittedLiquidationSourceUtc == bar.Timestamp)
            {
                auditor.LogEvent(signal.Ticker,strategy.StrategyName,bar.Timestamp,ExecutionState.PositionClosed,
                    "Exit via scheduled_study_exit at precommitted session open",candidateId);
                return (BuildCandidate(candidateId,strategy,signal,"short",entryTimestamp,entryPrice,
                    initialStopLossPrice,takeProfitPrice,bar.Timestamp,bar.Open,"scheduled_study_exit",
                    stopDistance,bars[entryIndex-1].Volume),i);
            }
            if (!_sessionClock.ValidateExecutionWindow(
                    bar.Timestamp,
                    strategy.Execution.Timeframe,
                    strategy.Session))
            {
                continue;
            }

            var barsHeld = eligibleBarsHeld;
            eligibleBarsHeld++;
            var previousEligibleIndex = FindPreviousValidExecutionBarIndex(bars, i - 1, strategy);
            if (strategy.ExitRules.EnableAtrTrailingStop &&
                previousEligibleIndex >= entryIndex &&
                snapshots[previousEligibleIndex].Atr is { } priorAtr &&
                priorAtr > 0m &&
                (entryPrice - lowestLowSinceEntry) / stopDistance >= strategy.ExitRules.TrailingActivationR)
            {
                var trailingStop = lowestLowSinceEntry + (strategy.ExitRules.TrailingStopAtrMultiple * priorAtr);
                currentStopLossPrice = Math.Min(initialStopLossPrice, Math.Min(currentStopLossPrice, trailingStop));
            }

            if (strategy.ExitRules.HasProtectiveStop && bar.High >= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyShortExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via {stopExitReason} at {slippedStopPrice}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, currentStopLossPrice, stopExitReason, stopDistance, bars[entryIndex - 1].Volume), i);
            }

            if (strategy.ExitRules.HasProfitTarget && bar.Low <= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyShortExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via take_profit at {slippedTakeProfit}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, takeProfitPrice, "take_profit", stopDistance, bars[entryIndex - 1].Volume), i);
            }

            if (ShouldExitShortOnConfirmedVwapReclaim(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Min(lowestLowSinceEntry, bar.Low), barsHeld))
            {
                var nextExitIndex = FindNextValidExecutionBarIndex(
                    bars,
                    i + 1,
                    strategy);
                var hasNextExitBar = nextExitIndex < bars.Count;
                if (!hasNextExitBar)
                {
                    pendingExit ??= PendingExitDecision(run, strategy, bar, "confirmed_vwap_reclaim");
                    continue;
                }

                var exitBar = hasNextExitBar ? bars[nextExitIndex] : bar;
                var exitSnapshot = hasNextExitBar ? snapshots[nextExitIndex] : snapshots[i];
                var exitBasis = hasNextExitBar ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyShortExitSlippage(exitBasis, (run.Engine.PrecommittedLiquidationSourceUtc == exitBar.Timestamp ? snapshots[i].RelativeVolume : exitSnapshot.RelativeVolume) ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via confirmed_vwap_reclaim at {confirmedExitPrice}", candidateId);
                return (BuildCandidate(candidateId, strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_reclaim", stopDistance, bars[entryIndex - 1].Volume), hasNextExitBar ? nextExitIndex : i);
            }

            var strategyWithoutTrailing = strategy with
            {
                ExitRules = strategy.ExitRules with { EnableAtrTrailingStop = false }
            };
            var (exitPrice, exitReason) = EvaluateShortBarForExit(
                strategyWithoutTrailing,
                bar,
                snapshots[i],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                barsHeld,
                ref currentStopLossPrice,
                ref lowestLowSinceEntry,
                previousEligibleIndex >= 0 ? snapshots[previousEligibleIndex] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                if (!IsPriceTriggeredExitReason(exitReason))
                {
                    var nextExitIndex = FindNextValidExecutionBarIndex(
                        bars,
                        i + 1,
                        strategy);
                    if (nextExitIndex >= bars.Count)
                    {
                        pendingExit ??= PendingExitDecision(run, strategy, bar, exitReason);
                        continue;
                    }

                    var nextBar = bars[nextExitIndex];
                    var nextRelativeVolume = run.Engine.PrecommittedLiquidationSourceUtc == nextBar.Timestamp
                        ? snapshots[i].RelativeVolume ?? relativeVolume
                        : snapshots[nextExitIndex].RelativeVolume ?? relativeVolume;
                    var nextExitPrice = ApplyShortExitSlippage(
                        nextBar.Open,
                        nextRelativeVolume,
                        strategy,
                        approximateTradeAmount);
                    auditor.LogEvent(
                        signal.Ticker,
                        strategy.StrategyName,
                        nextBar.Timestamp,
                        TradingFlow.Engine.Execution.ExecutionState.PositionClosed,
                        $"Short exit via {exitReason} at {nextExitPrice}",
                        candidateId);
                    return (BuildCandidate(
                        candidateId,
                        strategy,
                        signal,
                        "short",
                        entryTimestamp,
                        entryPrice,
                        initialStopLossPrice,
                        takeProfitPrice,
                        nextBar.Timestamp,
                        nextExitPrice,
                        exitReason,
                        stopDistance,
                        bars[entryIndex - 1].Volume), nextExitIndex);
                }

                var isProtectiveExit = IsPriceTriggeredExitReason(exitReason);
                var slippedExitPrice = ApplyShortExitSlippage(
                    exitPrice.Value,
                    snapshots[i].RelativeVolume ?? relativeVolume,
                    strategy,
                    approximateTradeAmount);
                auditor.LogEvent(
                    signal.Ticker,
                    strategy.StrategyName,
                    bar.Timestamp,
                    TradingFlow.Engine.Execution.ExecutionState.PositionClosed,
                    $"Short exit via {exitReason} at {slippedExitPrice}",
                    candidateId);
                return (BuildCandidate(
                    candidateId,
                    strategy,
                    signal,
                    "short",
                    entryTimestamp,
                    entryPrice,
                    initialStopLossPrice,
                    takeProfitPrice,
                    bar.Timestamp,
                    isProtectiveExit ? exitPrice.Value : slippedExitPrice,
                    exitReason,
                    stopDistance,
                    bars[entryIndex - 1].Volume), i);
            }
        }

        return (BuildOpenCandidate(run, candidateId, strategy, signal, "short", entryTimestamp,
            entryPrice, initialStopLossPrice, takeProfitPrice, stopDistance, bars[entryIndex - 1].Volume)
            with { PendingExitDecision = pendingExit }, bars.Count - 1);
    }

    // Decide whether a completed bar closes the short: stop and target only when those orders exist, then the short technical
    // exits (returned at the bar close and filled by the caller at the next open), then the bar-count limit.
    private static (decimal? ExitPrice, string? ExitReason) EvaluateShortBarForExit(
        StrategyDefinition strategy,
        OhlcvBar bar,
        IndicatorSnapshot snapshot,
        decimal entryPrice,
        decimal initialStopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset entryTimestamp,
        decimal stopDistance,
        int barsHeld,
        ref decimal currentStopLossPrice,
        ref decimal lowestLowSinceEntry,
        IndicatorSnapshot? previousSnapshot = null)
    {
        if (strategy.ExitRules.EnableAtrTrailingStop &&
            snapshot.Atr is { } atr &&
            atr > 0)
        {
            var profitR = (entryPrice - lowestLowSinceEntry) / stopDistance;
            if (profitR >= strategy.ExitRules.TrailingActivationR)
            {
                var trailingStop = lowestLowSinceEntry + (strategy.ExitRules.TrailingStopAtrMultiple * atr);
                currentStopLossPrice = Math.Min(initialStopLossPrice, Math.Min(currentStopLossPrice, trailingStop));
            }
        }

        if (strategy.ExitRules.HasProtectiveStop && bar.High >= currentStopLossPrice)
        {
            var exitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
            return (currentStopLossPrice, exitReason);
        }

        if (strategy.ExitRules.HasProfitTarget && bar.Low <= takeProfitPrice)
        {
            return (takeProfitPrice, "take_profit");
        }

        if (barsHeld >= strategy.ExitRules.MinHoldBarsBeforeTechnicalExit)
        {
            if (strategy.ExitRules.ExitOnCloseBelowVwap &&
                snapshot.Vwap is { } vwap &&
                snapshot.CurrentPrice > vwap)
            {
                return (bar.Close, "technical_exit_above_vwap");
            }

            if (strategy.ExitRules.ExitOnMacdHistogramNegative &&
                snapshot.MacdHistogram is { } histogram &&
                histogram > 0)
            {
                return (bar.Close, "technical_exit_macd_histogram_positive");
            }

            // Connors short mirror: cover when the completed close falls below its 5-day average.
            if (strategy.ExitRules.ExitOnCloseBelowSma5 && snapshot.Sma5 is { } sma5 && snapshot.CurrentPrice < sma5)
            {
                return (bar.Close, "technical_exit_below_sma5");
            }

            if (strategy.ExitRules.ExitShortOnSma10CrossAboveSma20 &&
                TechnicalExecutionEngine.IsSma10CrossedAboveSma20(snapshot, previousSnapshot))
            {
                return (bar.Close, "technical_exit_sma10_cross_above_sma20");
            }
        }

        if (strategy.ExitRules.MaxHoldBars is { } maxHoldBars &&
            maxHoldBars > 0 &&
            barsHeld >= maxHoldBars)
        {
            return (bar.Close, "max_hold_bars");
        }

        var maxExitTimestamp = entryTimestamp.AddHours((double)strategy.ExitRules.MaxHoldHours);
        if (bar.Timestamp >= maxExitTimestamp)
        {
            return (bar.Close, "max_hold");
        }

        lowestLowSinceEntry = Math.Min(lowestLowSinceEntry, bar.Low);
        return (null, null);
    }

    private static bool IsPriceTriggeredExitReason(string exitReason) =>
        exitReason.Equals("take_profit", StringComparison.OrdinalIgnoreCase) ||
        exitReason.Equals("trailing_stop", StringComparison.OrdinalIgnoreCase) ||
        exitReason.StartsWith("stop_loss", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldExitLongOnConfirmedVwapFailure(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int entryIndex,
        decimal entryPrice,
        decimal stopDistance,
        decimal highestHighSinceEntry,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableConfirmedVwapExit ||
            barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit ||
            stopDistance <= 0)
        {
            return false;
        }

        if (strategy.ExitRules.DisableConfirmedVwapExitAfterR is { } disableAfterR &&
            ((highestHighSinceEntry - entryPrice) / stopDistance) >= disableAfterR)
        {
            return false;
        }

        var confirmationBars = Math.Max(strategy.ExitRules.ConfirmedVwapExitBars, 1);
        if (index - confirmationBars + 1 < entryIndex)
        {
            return false;
        }

        for (var i = index - confirmationBars + 1; i <= index; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Vwap is null || snapshot.Atr is null)
            {
                return false;
            }

            var failureLevel = snapshot.Vwap.Value - (snapshot.Atr.Value * strategy.ExitRules.ConfirmedVwapExitAtrBuffer);
            if (snapshot.CurrentPrice >= failureLevel)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldExitShortOnConfirmedVwapReclaim(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int entryIndex,
        decimal entryPrice,
        decimal stopDistance,
        decimal lowestLowSinceEntry,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableConfirmedVwapExit ||
            barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit ||
            stopDistance <= 0)
        {
            return false;
        }

        if (strategy.ExitRules.DisableConfirmedVwapExitAfterR is { } disableAfterR &&
            ((entryPrice - lowestLowSinceEntry) / stopDistance) >= disableAfterR)
        {
            return false;
        }

        var confirmationBars = Math.Max(strategy.ExitRules.ConfirmedVwapExitBars, 1);
        if (index - confirmationBars + 1 < entryIndex)
        {
            return false;
        }

        for (var i = index - confirmationBars + 1; i <= index; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Vwap is null || snapshot.Atr is null)
            {
                return false;
            }

            var reclaimLevel = snapshot.Vwap.Value + (snapshot.Atr.Value * strategy.ExitRules.ConfirmedVwapExitAtrBuffer);
            if (snapshot.CurrentPrice <= reclaimLevel)
            {
                return false;
            }
        }

        return true;
    }

    private static decimal ApplyLongExitSlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyLongExitSlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static decimal ApplyShortEntrySlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyShortEntrySlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static decimal ApplyShortExitSlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyShortExitSlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static BacktestCandidateTrade BuildCandidate(
        string candidateId,
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
        DateTimeOffset entryTimestamp,
        decimal entryPrice,
        decimal stopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset? exitTimestamp,
        decimal? exitPrice,
        string exitReason,
        decimal stopDistance,
        decimal entryBarVolume)
    {
        return new BacktestCandidateTrade(
            signal.Ticker,
            strategy.StrategyName,
            direction,
            entryTimestamp,
            Decimal.Round(entryPrice, 4),
            Decimal.Round(stopLossPrice, 4),
            Decimal.Round(takeProfitPrice, 4),
            exitTimestamp,
            exitPrice is { } price ? Decimal.Round(price, 4) : null,
            exitReason,
            Decimal.Round(stopDistance, 4),
            entryBarVolume,
            ResolvePortfolioSelectionScore(strategy, signal),
            ResolveInitialStopKind(strategy),
            strategy.StrategyId,
            candidateId)
        {
            PlanningReferencePrice = ResolvePlanningReferencePrice(strategy, direction, stopLossPrice, stopDistance)
        };
    }

    // Recover the prior-close planning price that kept-gap strategies size from. With a stop it is the stop shifted back by the
    // stop distance; without one the candidate builders set the risk unit to the planning price itself (stop price 0), so
    // shifting from 0 would give a negative price for a short. Strategies that do not keep gaps plan from the fill.
    private static decimal? ResolvePlanningReferencePrice(StrategyDefinition strategy, string direction, decimal stopLossPrice, decimal stopDistance)
    {
        if (!strategy.Execution.KeepsOpeningGaps()) return null;
        if (!strategy.ExitRules.HasProtectiveStop) return Decimal.Round(stopDistance, 4);
        return Decimal.Round(direction == "long" ? stopLossPrice + stopDistance : stopLossPrice - stopDistance, 4);
    }

    private const string GapThroughStopExitReason = "gap_fill_through_structural_stop";

    // Preserve an unfinished hypothesis without manufacturing an executable exit or weakening a required liquidation.
    private static BacktestCandidateTrade BuildOpenCandidate(BacktestRunConfig run, string candidateId,
        StrategyDefinition strategy, TradeSignal signal, string direction, DateTimeOffset entryTimestamp,
        decimal entryPrice, decimal stopPrice, decimal targetPrice, decimal distance, decimal volume)
    {
        if (run.Engine.PrecommittedLiquidationSourceUtc is not null)
            throw new InvalidOperationException("Required precommitted liquidation has no executable source slice.");
        return BuildCandidate(candidateId, strategy, signal, direction, entryTimestamp, entryPrice,
            stopPrice, targetPrice, null, null, "open_at_study_end", distance, volume);
    }

    // A pending intention is known only when its completed source and any delayed receipt are both available.
    private static BacktestPendingExitDecision? PendingExitDecision(BacktestRunConfig run, StrategyDefinition strategy,
        OhlcvBar source, string reason)
    {
        var completed = source.Timestamp.Add(ParseTimeframe(strategy.Execution.Timeframe));
        var known = source.KnownAtUtc is { } receipt && receipt > completed ? receipt : completed;
        return run.TimeWindow.End is { } cutoff && known > cutoff ? null : new(known.ToUniversalTime(), reason);
    }

    private static string ResolveInitialStopKind(StrategyDefinition strategy) =>
        NormalizeRuleName(strategy.ExitRules.InitialStopMode) switch
        {
            "atr" => "atr",
            ExitRules.NoStop => ExitRules.NoStop,
            _ => "structural"
        };

    // Score same-day candidates for capital: rsi2_ascending ranks the most oversold long first, rsi2_descending the most overbought short.
    private static decimal ResolvePortfolioSelectionScore(
        StrategyDefinition strategy,
        TradeSignal signal)
    {
        return strategy.EntryRules.PortfolioRankMode.Trim().ToLowerInvariant() switch
        {
            "none" => 0m,
            "rsi2_ascending" => signal.CurrentRsi2 is { } rsi2 ? 100m - rsi2 : 0m,
            "rsi2_descending" => signal.CurrentRsi2 ?? 0m,
            _ => throw new NotSupportedException(
                $"Unsupported portfolio_rank_mode: {strategy.EntryRules.PortfolioRankMode}.")
        };
    }

    private static decimal ResolveTakeProfitPrice(
        StrategyOrderPlan orderPlan,
        decimal entryPrice,
        decimal stopDistance)
    {
        var mode = NormalizeRuleName(orderPlan.ProfitTargetMode);
        // No target order: 0 is never a valid long target and every target check is skipped for this mode.
        if (mode == ExitRules.NoProfitTarget) return 0m;
        if (mode == "vwap" && orderPlan.ProfitTargetReferencePrice is { } vwap)
        {
            return vwap;
        }

        return orderPlan.Direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? entryPrice - (stopDistance * orderPlan.TargetRMultiple)
            : entryPrice + (stopDistance * orderPlan.TargetRMultiple);
    }

    private static string NormalizeRuleName(string? value)
    {
        return String.IsNullOrWhiteSpace(value)
            ? String.Empty
            : value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
    }

    private int FindPreviousValidExecutionBarIndex(
        IReadOnlyList<OhlcvBar> bars,
        int startIndex,
        StrategyDefinition strategy)
    {
        for (var index = Math.Min(startIndex, bars.Count - 1);
             index >= 0;
             index--)
        {
            if (_sessionClock.ValidateExecutionWindow(
                    bars[index].Timestamp,
                    strategy.Execution.Timeframe,
                    strategy.Session))
            {
                return index;
            }
        }

        return -1;
    }
}
