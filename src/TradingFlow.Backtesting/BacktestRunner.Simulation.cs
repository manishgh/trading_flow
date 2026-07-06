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
    private static decimal? ComputeCloseLocationValue(OhlcvBar bar)
    {
        var range = bar.High - bar.Low;
        return range <= 0m
            ? null
            : (bar.Close - bar.Low) / range;
    }

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
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
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

        var entryBar = bars[entryIndex];
        var entrySnapshot = snapshots[entryIndex];
        var relativeVolume = entrySnapshot.RelativeVolume ?? 1.0m;
        if (direction.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            return CreateShortCandidate(run, strategy, signal, entryIndex, bars, snapshots, auditor, relativeVolume, cancellationToken);
        }

        // Approximate trade amount based on portfolio config
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyLongSlippage(entryBar.Open, relativeVolume, strategy.Execution.SlippageBps, approximateTradeAmount);

        var risk = ResolveInitialRisk(
            strategy,
            signal,
            "long",
            entryPrice,
            run.Portfolio.RiskPerTradePct,
            entryIndex,
            bars,
            snapshots);
        if (risk is null)
        {
            return null;
        }

        var (initialStopLossPrice, stopDistance) = risk.Value;
        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = ResolveTakeProfitPrice(strategy, "long", entryPrice, stopDistance, snapshots[entryIndex]);
        if (takeProfitPrice <= entryPrice)
        {
            return null;
        }
        var entryTimestamp = entryBar.Timestamp;
        var highestHighSinceEntry = entryPrice;

        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"LONG signal generated at {signal.CurrentPrice}");
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})");

        var technicalEngine = new TradingFlow.Engine.Execution.TechnicalExecutionEngine();

        // Daily bars and research swing bars do not preserve stop/target sequence inside
        // the entry bar. Strategies can disable same-bar stop/target handling to
        // avoid manufacturing fills from unknown intrabar order.
        var exitStartIndex = IsDailyOrHigher(strategy.Execution.Timeframe) || !strategy.ExitRules.AllowSameBarStopTarget
            ? entryIndex + 1
            : entryIndex;
        if (exitStartIndex >= bars.Count)
        {
            return null;
        }

        for (var i = exitStartIndex; i < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
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
            if (IsIntradayFlatStrategy(strategy) &&
                _sessionClock.ShouldFlattenBeforeSessionClose(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
            {
                var eodRelativeVolume = snapshots[i].RelativeVolume ?? relativeVolume;
                var eodExitPrice = ApplyLongExitSlippage(bar.Open, eodRelativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via end_of_day_exit at {eodExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, eodExitPrice, "end_of_day_exit", stopDistance, entryBar.Volume), i);
            }

            var barsHeld = i - entryIndex;
            currentStopLossPrice = technicalEngine.CalculateEffectiveStopLoss(
                strategy,
                snapshots[i],
                entryPrice,
                stopDistance,
                initialStopLossPrice,
                currentStopLossPrice,
                highestHighSinceEntry);
            if (bar.Low <= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice > initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyLongExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {stopExitReason} at {slippedStopPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedStopPrice, stopExitReason, stopDistance, entryBar.Volume), i);
            }

            if (ShouldExitFailedBreakout(strategy, "long", entryPrice, stopDistance, bar.Close, barsHeld))
            {
                var failedExitPrice = ApplyLongExitSlippage(bar.Close, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via failed_breakout_circuit_breaker at {failedExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, failedExitPrice, "failed_breakout_circuit_breaker", stopDistance, entryBar.Volume), i);
            }

            if (bar.High >= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyLongExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via take_profit at {slippedTakeProfit}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedTakeProfit, "take_profit", stopDistance, entryBar.Volume), i);
            }

            if (technicalEngine.ShouldExitLongOnConfirmedVwapFailure(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Max(highestHighSinceEntry, bar.High), barsHeld))
            {
                var exitBar = i + 1 < bars.Count ? bars[i + 1] : bar;
                var exitSnapshot = i + 1 < snapshots.Count ? snapshots[i + 1] : snapshots[i];
                var exitBasis = i + 1 < bars.Count ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyLongExitSlippage(exitBasis, exitSnapshot.RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via confirmed_vwap_failure at {confirmedExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_failure", stopDistance, entryBar.Volume), Math.Min(i + 1, bars.Count - 1));
            }

            var (exitPrice, exitReason) = technicalEngine.EvaluateBarForExit(
                strategy,
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
                i > 0 ? snapshots[i - 1] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                // In backtest, for technical exit, we exit on next bar open
                if (exitReason.StartsWith("technical_exit") && i + 1 < bars.Count)
                {
                    var nextBar = bars[i + 1];
                    var nextRelativeVolume = snapshots[i + 1].RelativeVolume ?? relativeVolume;
                    var nextExitPrice = ApplyLongExitSlippage(nextBar.Open, nextRelativeVolume, strategy, approximateTradeAmount);
                    auditor.LogEvent(signal.Ticker, strategy.StrategyName, nextBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {nextExitPrice}");
                    return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, nextBar.Timestamp, nextExitPrice, exitReason, stopDistance, entryBar.Volume), i + 1);
                }

                var slippedExitPrice = ApplyLongExitSlippage(exitPrice.Value, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {slippedExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedExitPrice, exitReason, stopDistance, entryBar.Volume), i);
            }
        }

        var finalBar = bars[^1];
        var finalRelativeVolume = snapshots[^1].RelativeVolume ?? relativeVolume;
        var finalExitPrice = ApplyLongExitSlippage(finalBar.Close, finalRelativeVolume, strategy, approximateTradeAmount);
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, finalBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via end_of_data at {finalExitPrice}");
        return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, finalBar.Timestamp, finalExitPrice, "end_of_data", stopDistance, entryBar.Volume), bars.Count - 1);
    }

    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateShortCandidate(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        int entryIndex,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor,
        decimal relativeVolume,
        CancellationToken cancellationToken)
    {
        var entryBar = bars[entryIndex];
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = ApplyShortEntrySlippage(entryBar.Open, relativeVolume, strategy, approximateTradeAmount);
        var risk = ResolveInitialRisk(
            strategy,
            signal,
            "short",
            entryPrice,
            run.Portfolio.RiskPerTradePct,
            entryIndex,
            bars,
            snapshots);
        if (risk is null)
        {
            return null;
        }

        var (initialStopLossPrice, stopDistance) = risk.Value;
        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = ResolveTakeProfitPrice(strategy, "short", entryPrice, stopDistance, snapshots[entryIndex]);
        if (takeProfitPrice <= 0 || takeProfitPrice >= entryPrice)
        {
            return null;
        }

        var entryTimestamp = entryBar.Timestamp;
        var lowestLowSinceEntry = entryPrice;
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"SHORT signal generated at {signal.CurrentPrice}");
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated short fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})");

        // Use the same sequence-safety rule as long-side candidate creation.
        var exitStartIndex = IsDailyOrHigher(strategy.Execution.Timeframe) || !strategy.ExitRules.AllowSameBarStopTarget
            ? entryIndex + 1
            : entryIndex;
        if (exitStartIndex >= bars.Count)
        {
            return null;
        }

        for (var i = exitStartIndex; i < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
            if (IsIntradayFlatStrategy(strategy) &&
                _sessionClock.ShouldFlattenBeforeSessionClose(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
            {
                var eodRelativeVolume = snapshots[i].RelativeVolume ?? relativeVolume;
                var eodExitPrice = ApplyShortExitSlippage(bar.Open, eodRelativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via end_of_day_exit at {eodExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, eodExitPrice, "end_of_day_exit", stopDistance, entryBar.Volume), i);
            }

            var barsHeld = i - entryIndex;
            if (bar.High >= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyShortExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via {stopExitReason} at {slippedStopPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedStopPrice, stopExitReason, stopDistance, entryBar.Volume), i);
            }

            if (ShouldExitFailedBreakout(strategy, "short", entryPrice, stopDistance, bar.Close, barsHeld))
            {
                var failedExitPrice = ApplyShortExitSlippage(bar.Close, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via failed_breakout_circuit_breaker at {failedExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, failedExitPrice, "failed_breakout_circuit_breaker", stopDistance, entryBar.Volume), i);
            }

            if (bar.Low <= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyShortExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via take_profit at {slippedTakeProfit}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedTakeProfit, "take_profit", stopDistance, entryBar.Volume), i);
            }

            if (ShouldExitShortOnConfirmedVwapReclaim(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Min(lowestLowSinceEntry, bar.Low), barsHeld))
            {
                var exitBar = i + 1 < bars.Count ? bars[i + 1] : bar;
                var exitSnapshot = i + 1 < snapshots.Count ? snapshots[i + 1] : snapshots[i];
                var exitBasis = i + 1 < bars.Count ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyShortExitSlippage(exitBasis, exitSnapshot.RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via confirmed_vwap_reclaim at {confirmedExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_reclaim", stopDistance, entryBar.Volume), Math.Min(i + 1, bars.Count - 1));
            }

            var (exitPrice, exitReason) = EvaluateShortBarForExit(
                strategy,
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
                i > 0 ? snapshots[i - 1] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                var slippedExitPrice = ApplyShortExitSlippage(exitPrice.Value, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via {exitReason} at {slippedExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedExitPrice, exitReason, stopDistance, entryBar.Volume), i);
            }
        }

        var finalBar = bars[^1];
        var finalRelativeVolume = snapshots[^1].RelativeVolume ?? relativeVolume;
        var finalExitPrice = ApplyShortExitSlippage(finalBar.Close, finalRelativeVolume, strategy, approximateTradeAmount);
        return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, finalBar.Timestamp, finalExitPrice, "end_of_data", stopDistance, entryBar.Volume), bars.Count - 1);
    }

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

        if (bar.High >= currentStopLossPrice)
        {
            var exitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
            return (currentStopLossPrice, exitReason);
        }

        if (bar.Low <= takeProfitPrice)
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

            if (strategy.ExitRules.ExitShortOnSma10CrossAboveSma20 &&
                TechnicalExecutionEngine.IsSma10CrossedAboveSma20(snapshot, previousSnapshot))
            {
                return (bar.Close, "technical_exit_sma10_cross_above_sma20");
            }
        }

        var maxExitTimestamp = entryTimestamp.AddHours((double)strategy.ExitRules.MaxHoldHours);
        if (bar.Timestamp >= maxExitTimestamp)
        {
            return (bar.Close, "max_hold");
        }

        lowestLowSinceEntry = Math.Min(lowestLowSinceEntry, bar.Low);
        return (null, null);
    }

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

    private static bool IsIntradayFlatStrategy(StrategyDefinition strategy)
    {
        return !IsDailyOrHigher(strategy.Timeframe) &&
            !IsDailyOrHigher(strategy.Execution.Timeframe) &&
            strategy.ExitRules.MaxHoldHours <= 8m;
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
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
        DateTimeOffset entryTimestamp,
        decimal entryPrice,
        decimal stopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset exitTimestamp,
        decimal exitPrice,
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
            Decimal.Round(exitPrice, 4),
            exitReason,
            Decimal.Round(stopDistance, 4),
            entryBarVolume);
    }

    private static decimal ResolvePositionRiskStopDistance(
        StrategyDefinition strategy,
        decimal atr,
        decimal entryPrice,
        decimal maxLossPctOfPosition)
    {
        var atrStopDistance = strategy.ExitRules.StopAtrMultiple * atr;
        if (entryPrice <= 0m || maxLossPctOfPosition <= 0m)
        {
            return atrStopDistance;
        }

        var positionRiskStopDistance = entryPrice * (maxLossPctOfPosition / 100m);
        return Math.Min(atrStopDistance, positionRiskStopDistance);
    }

    private static (decimal StopLossPrice, decimal StopDistance)? ResolveInitialRisk(
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
        decimal entryPrice,
        decimal maxLossPctOfPosition,
        int entryIndex,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots)
    {
        var mode = NormalizeRuleName(strategy.ExitRules.InitialStopMode);
        var isShort = direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        decimal stopLossPrice;

        switch (mode)
        {
            case "vwap_minus_atr" when !isShort:
                if (snapshots[entryIndex].Vwap is not { } longVwap || snapshots[entryIndex].Atr is not { } longAtr || longAtr <= 0m)
                {
                    return null;
                }

                stopLossPrice = longVwap - (strategy.ExitRules.StopAtrMultiple * longAtr);
                break;

            case "vwap_plus_atr" when isShort:
                if (snapshots[entryIndex].Vwap is not { } shortVwap || snapshots[entryIndex].Atr is not { } shortAtr || shortAtr <= 0m)
                {
                    return null;
                }

                stopLossPrice = shortVwap + (strategy.ExitRules.StopAtrMultiple * shortAtr);
                break;

            case "opening_range_opposite":
                var openingRange = GetOpeningRange(strategy, bars, snapshots[entryIndex].Timestamp);
                if (openingRange is null)
                {
                    return null;
                }

                stopLossPrice = isShort ? openingRange.Value.High : openingRange.Value.Low;
                break;

            case "extreme_shadow":
                var sessionExtreme = GetSessionExtremeThroughIndex(strategy, bars, snapshots[entryIndex].Timestamp, entryIndex);
                if (sessionExtreme is null)
                {
                    return null;
                }

                stopLossPrice = isShort
                    ? sessionExtreme.Value.High + strategy.ExitRules.StopTickBuffer
                    : sessionExtreme.Value.Low - strategy.ExitRules.StopTickBuffer;
                break;

            case "atr":
            default:
                var distance = ResolvePositionRiskStopDistance(strategy, signal.CurrentAtr, entryPrice, maxLossPctOfPosition);
                if (distance <= 0m)
                {
                    return null;
                }

                stopLossPrice = isShort ? entryPrice + distance : entryPrice - distance;
                break;
        }

        var stopDistance = isShort ? stopLossPrice - entryPrice : entryPrice - stopLossPrice;
        return stopDistance <= 0m ? null : (Decimal.Round(stopLossPrice, 4), stopDistance);
    }

    private static decimal ResolveTakeProfitPrice(
        StrategyDefinition strategy,
        string direction,
        decimal entryPrice,
        decimal stopDistance,
        IndicatorSnapshot entrySnapshot)
    {
        var mode = NormalizeRuleName(strategy.ExitRules.ProfitTargetMode);
        if (mode == "vwap" && entrySnapshot.Vwap is { } vwap)
        {
            return vwap;
        }

        return direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? entryPrice - (stopDistance * strategy.ExitRules.TargetRMultiple)
            : entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);
    }

    private static bool ShouldExitFailedBreakout(
        StrategyDefinition strategy,
        string direction,
        decimal entryPrice,
        decimal stopDistance,
        decimal currentPrice,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableFailedBreakoutCircuitBreaker ||
            stopDistance <= 0m ||
            barsHeld <= 0 ||
            barsHeld > Math.Max(1, strategy.ExitRules.FailedBreakoutBars))
        {
            return false;
        }

        var unrealizedR = direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? (entryPrice - currentPrice) / stopDistance
            : (currentPrice - entryPrice) / stopDistance;
        return unrealizedR < strategy.ExitRules.FailedBreakoutMinR;
    }

    private static (decimal High, decimal Low)? GetOpeningRange(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        DateTimeOffset timestamp)
    {
        if (strategy.EntryRules.OpeningRangeMinutes <= 0)
        {
            return null;
        }

        var exchangeTime = ToExchangeTime(timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = exchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        var rangeBars = bars
            .Where(bar =>
            {
                var barExchangeTime = ToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return barExchangeTime.Date == exchangeTime.Date &&
                    barExchangeTime >= sessionOpen &&
                    barExchangeTime < openingRangeEnd;
            })
            .ToArray();

        return rangeBars.Length == 0
            ? null
            : (rangeBars.Max(bar => bar.High), rangeBars.Min(bar => bar.Low));
    }

    private static (decimal High, decimal Low)? GetSessionExtremeThroughIndex(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        DateTimeOffset timestamp,
        int entryIndex)
    {
        var exchangeTime = ToExchangeTime(timestamp, strategy.Session.ExchangeTimezone);
        var sessionBars = bars
            .Take(entryIndex + 1)
            .Where(bar => ToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone).Date == exchangeTime.Date)
            .ToArray();

        return sessionBars.Length == 0
            ? null
            : (sessionBars.Max(bar => bar.High), sessionBars.Min(bar => bar.Low));
    }

    private static string NormalizeRuleName(string? value)
    {
        return String.IsNullOrWhiteSpace(value)
            ? String.Empty
            : value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
    }
}
