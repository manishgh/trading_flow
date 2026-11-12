using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

// Decision-hour candidates (long and short): the entry fills on the decision session's last-hour bar; afterwards the hourly
// bars are walked in time order, checking the stop and target on each (stop first when both fall in one bar), and each later
// session's decision runs at its decision time, so a technical or time exit fills on that session's last-hour bar.
public sealed partial class BacktestRunner
{
    /// <summary>The per-stock evidence an exit walk reads: the decided sessions, the hourly bars and the completed daily snapshots.</summary>
    private sealed record DecisionHourExitContext(StrategyDefinition Strategy, IReadOnlyList<DecisionHourSession> Sessions,
        IReadOnlyList<OhlcvBar> HourlyBars, IReadOnlyList<IndicatorSnapshot> DailySnapshots);

    /// <summary>
    /// Builds the candidate of a decision triggered on session <paramref name="entrySession"/>: fill on its last-hour bar at
    /// the open plus slippage, stop distance and target planned from the decision price, then the first exit of the walk, or an
    /// open candidate when none occurs before the data ends. Returns null when the stop or target is invalid.
    /// </summary>
    private BacktestCandidateTrade? CreateDecisionHourCandidate(string candidateId, BacktestRunConfig run, DecisionHourExitContext context,
        int entrySession, TradeSignal signal, StrategyOrderPlan orderPlan, ExecutionAuditor auditor)
    {
        var strategy = context.Strategy;
        var session = context.Sessions[entrySession];
        var fillBar = session.Decision!.LastHourBar;
        var isShort = orderPlan.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        var direction = isShort ? "short" : "long";
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var relativeVolume = session.Snapshot!.RelativeVolume ?? 1.0m;
        var entryPrice = isShort
            ? ApplyShortEntrySlippage(fillBar.Open, relativeVolume, strategy, approximateTradeAmount)
            : DynamicSlippageModel.ApplyLongSlippage(fillBar.Open, relativeVolume, strategy.Execution.SlippageBps, approximateTradeAmount);
        var (stop, stopDistance, target) = DecisionHourProtection(strategy, orderPlan, isShort);
        if (stopDistance is null) return null;

        auditor.LogEvent(signal.Ticker, strategy.StrategyName, session.DecisionTimeUtc, ExecutionState.SignalGenerated,
            $"{direction.ToUpperInvariant()} decision-hour signal at {signal.CurrentPrice}", candidateId);
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, fillBar.Timestamp, ExecutionState.OrderFilled,
            $"Simulated last-hour fill at {entryPrice} (Stop: {stop}, TP: {target})", candidateId);
        var exit = FindDecisionHourExit(context, entrySession, isShort, stop, target, relativeVolume, approximateTradeAmount);
        var volume = session.Decision.ProvisionalDailyBar.Volume;
        if (exit is null)
            return BuildOpenCandidate(run, candidateId, strategy, signal, direction, fillBar.Timestamp, entryPrice, stop, target,
                stopDistance.Value, volume) with { FillTimeframe = ExecutionRules.DecisionHourFillTimeframe };
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, exit.Bar.Timestamp, ExecutionState.PositionClosed,
            $"Exit via {exit.Reason} at {exit.Price}", candidateId);
        return BuildCandidate(candidateId, strategy, signal, direction, fillBar.Timestamp, entryPrice, stop, target, exit.Bar.Timestamp,
            exit.Price, exit.Reason, stopDistance.Value, volume) with { FillTimeframe = ExecutionRules.DecisionHourFillTimeframe };
    }

    /// <summary>
    /// The protective prices planned from the decision price (the plan's trigger reference): the stop, the risk unit (the
    /// decision price itself without a stop, as in the daily builders) and the target; a null distance marks an invalid plan.
    /// </summary>
    private static (decimal Stop, decimal? Distance, decimal Target) DecisionHourProtection(StrategyDefinition strategy,
        StrategyOrderPlan orderPlan, bool isShort)
    {
        var planningPrice = orderPlan.TriggerReferencePrice;
        var stop = orderPlan.InitialStopPrice;
        var distance = !strategy.ExitRules.HasProtectiveStop ? planningPrice : isShort ? stop - planningPrice : planningPrice - stop;
        if ((strategy.ExitRules.HasProtectiveStop && stop <= 0m) || distance <= 0m) return (stop, null, 0m);
        var target = ResolveTakeProfitPrice(orderPlan, planningPrice, distance);
        var invalidTarget = isShort ? target <= 0m || target >= planningPrice : target <= planningPrice;
        return strategy.ExitRules.HasProfitTarget && invalidTarget ? (stop, null, target) : (stop, distance, target);
    }

    /// <summary>
    /// Walks the hourly bars after the entry fill in time order. Before each bar, every later session whose decision time has
    /// come is decided (a technical or time exit returns that session's last-hour bar, filled at its open less slippage); then
    /// the bar is checked against the stop and target. Returns null when nothing exits before the data ends.
    /// </summary>
    private DecisionHourExit? FindDecisionHourExit(DecisionHourExitContext context, int entrySession, bool isShort, decimal stop,
        decimal target, decimal relativeVolume, decimal approximateTradeAmount)
    {
        var entry = context.Sessions[entrySession];
        var fillTime = entry.Decision!.LastHourBar.Timestamp;
        var next = entrySession + 1;
        var barsHeld = 0;
        foreach (var bar in context.HourlyBars.Where(bar => bar.Timestamp > fillTime))
        {
            for (; next < context.Sessions.Count && context.Sessions[next].DecisionTimeUtc <= bar.Timestamp; next++)
            {
                if (context.Sessions[next] is not { Decision: { } decision, Snapshot: { } snapshot } session) continue;
                barsHeld++;
                var reason = DecisionHourExitReason(context, session, snapshot, isShort, barsHeld, entry.DecisionTimeUtc);
                if (reason is null) continue;
                var lastHour = decision.LastHourBar;
                var price = isShort
                    ? ApplyShortExitSlippage(lastHour.Open, snapshot.RelativeVolume ?? relativeVolume, context.Strategy, approximateTradeAmount)
                    : ApplyLongExitSlippage(lastHour.Open, snapshot.RelativeVolume ?? relativeVolume, context.Strategy, approximateTradeAmount);
                return new(lastHour, price, reason);
            }
            if (ProtectiveExitOn(context.Strategy, bar, isShort, stop, target) is { } protective) return protective;
        }
        return null;
    }

    /// <summary>
    /// The technical or time exit a later session's decision reads from its provisional snapshot (shared exit rules: the long
    /// rules of TechnicalExecutionEngine, the short mirror of the daily builder), with the prior completed daily snapshot as the
    /// previous observation; null keeps the position.
    /// </summary>
    private static string? DecisionHourExitReason(DecisionHourExitContext context, DecisionHourSession session, IndicatorSnapshot snapshot,
        bool isShort, int barsHeld, DateTimeOffset entryDecisionTime)
    {
        var previous = session.DailyIndex > 0 ? context.DailySnapshots[session.DailyIndex - 1] : null;
        return isShort
            ? ShortTechnicalOrTimeExitReason(context.Strategy, snapshot, barsHeld, session.DecisionTimeUtc, entryDecisionTime, previous)
            : new TechnicalExecutionEngine().GetTechnicalOrTimeExitReason(context.Strategy, snapshot, barsHeld, session.DecisionTimeUtc,
                entryDecisionTime, previousSnapshot: previous);
    }

    /// <summary>
    /// The stop or target a completed hourly bar touches, stop first when both are touched (the worst case, as the shared
    /// simulator fills them). Prices are the order prices; the execution coordinator decides the actual fills.
    /// </summary>
    private static DecisionHourExit? ProtectiveExitOn(StrategyDefinition strategy, OhlcvBar bar, bool isShort, decimal stop, decimal target)
    {
        if (strategy.ExitRules.HasProtectiveStop && (isShort ? bar.High >= stop : bar.Low <= stop)) return new(bar, stop, "stop_loss");
        if (strategy.ExitRules.HasProfitTarget && (isShort ? bar.Low <= target : bar.High >= target)) return new(bar, target, "take_profit");
        return null;
    }
}
