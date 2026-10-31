using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    // The completed prior daily close is a planning reference, never tomorrow's opening print.
    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateEventExecutionCandidate(
        string candidateId, BacktestRunConfig run, StrategyDefinition strategy, TradeSignal signal,
        StrategyOrderPlan plan, IReadOnlyList<OhlcvBar> daily, IReadOnlyList<IndicatorSnapshot> snapshots,
        int entryIndex, ExecutionAuditor auditor)
    {
        ValidateFineExecutionPolicy(run, strategy);
        if (entryIndex <= 0 || daily.Take(entryIndex).Any(x => x.AdjustmentPolicy != "raw"))
            throw new InvalidDataException("The SIP portfolio policy requires explicit raw completed daily inputs.");
        var schedules = candidateSessionSchedules ?? throw new InvalidOperationException("Event execution requires authoritative sessions.");
        var terminal = FineExecutionTiming.SessionOpen(run.Engine.PrecommittedLiquidationSourceUtc!.Value, strategy.Session.ExchangeTimezone, schedules);
        var entry = FineExecutionTiming.SessionOpen(daily[entryIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules);
        if (entry < plan.TriggeredAtUtc || daily[entryIndex - 1].KnownAtUtc > entry)
            throw new InvalidDataException("Daily entry evidence is not known before its route.");
        var reference = daily[entryIndex - 1].Close;
        var distance = reference - plan.InitialStopPrice;
        if (distance <= 0 || plan.InitialStopPrice <= 0) return null;
        var target = ResolveTakeProfitPrice(plan, reference, distance);
        if (target <= reference) return null;
        var (exitIndex, reason) = FineExecutionTiming.DailyExit(strategy, daily, snapshots, entryIndex,
            run.Engine.PrecommittedLiquidationSourceUtc.Value,
            bar => _sessionClock.ValidateExecutionWindow(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session),
            source => FineExecutionTiming.SessionOpen(source, strategy.Session.ExchangeTimezone, schedules));
        var exit = FineExecutionTiming.SessionOpen(daily[exitIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules);
        var candidate = BuildCandidate(candidateId, strategy, signal, "long", entry, reference,
            plan.InitialStopPrice, target, exit, 0m, reason, distance, daily[entryIndex - 1].Volume) with
        { ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, daily[entryIndex].Timestamp,
            daily[exitIndex].Timestamp, terminal, [])
            { EventExecutionEndUtc = FineExecutionTiming.SessionClose(daily[exitIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules) } };
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, plan.TriggeredAtUtc, ExecutionState.SignalGenerated,
            "Completed raw daily intent; execution delegated to shared SIP event portfolio", candidateId);
        return (candidate, exitIndex);
    }
    // Daily candidate intent has no protective-price horizon under this policy.
    // The chronological book alone observes protective prices on execution slices.
    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateFineExecutionCandidate(
        string candidateId, BacktestRunConfig run, StrategyDefinition strategy, TradeSignal signal,
        StrategyOrderPlan plan, IReadOnlyList<OhlcvBar> daily,
        IReadOnlyList<IndicatorSnapshot> snapshots, IReadOnlyList<OhlcvBar> fine,
        int entryIndex, ExecutionAuditor auditor)
    {
        ValidateFineExecutionPolicy(run, strategy);
        var schedules = candidateSessionSchedules
            ?? throw new InvalidOperationException("Fine execution requires authoritative sessions.");
        var terminalSource = run.Engine.PrecommittedLiquidationSourceUtc!.Value;
        var terminalOpen = FineExecutionTiming.SessionOpen(terminalSource, strategy.Session.ExchangeTimezone, schedules);
        var entrySource = daily[entryIndex].Timestamp;
        var entryOpen = FineExecutionTiming.SessionOpen(entrySource, strategy.Session.ExchangeTimezone, schedules);
        if (entryOpen < plan.TriggeredAtUtc)
            throw new InvalidOperationException("Opening execution cannot precede actionable daily evidence.");
        var opening = fine.SingleOrDefault(bar => bar.Timestamp == entryOpen)
            ?? throw new InvalidDataException($"Opening execution slice missing: {signal.Ticker} {entryOpen:O}.");
        var amount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var referenceVolume = snapshots[plan.StopContextIndex].RelativeVolume ?? 1m;
        var entryPrice = DynamicSlippageModel.ApplyLongSlippage(opening.Open, referenceVolume,
            strategy.Execution.SlippageBps, amount);
        // A kept gap needs no special exit here: the book's stop acts on the first slice after the fill.
        var planningPrice = strategy.Execution.KeepsOpeningGaps() ? daily[entryIndex - 1].Close : entryPrice;
        var distance = planningPrice - plan.InitialStopPrice;
        if (distance <= 0 || plan.InitialStopPrice <= 0) return null;
        var target = ResolveTakeProfitPrice(plan, planningPrice, distance);
        if (target <= planningPrice) return null;

        var (exitIndex, reason) = FineExecutionTiming.DailyExit(strategy, daily, snapshots, entryIndex,
            terminalSource, bar => _sessionClock.ValidateExecutionWindow(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session),
            source => FineExecutionTiming.SessionOpen(source, strategy.Session.ExchangeTimezone, schedules));
        var exitSource = daily[exitIndex].Timestamp;
        var exitOpen = FineExecutionTiming.SessionOpen(exitSource, strategy.Session.ExchangeTimezone, schedules);
        var required = FineExecutionTiming.RequiredSlices(entryOpen, terminalOpen,
            strategy.Session.ExchangeTimezone, schedules);
        var candidate = BuildCandidate(candidateId, strategy, signal, "long", entryOpen,
            entryPrice, plan.InitialStopPrice, target, exitOpen, 0m, reason, distance,
            daily[entryIndex - 1].Volume) with
        {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1,
                entrySource, exitSource, terminalOpen, required)
        };
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, plan.TriggeredAtUtc,
            ExecutionState.SignalGenerated, "Daily intent; protective execution delegated to five-minute book", candidateId);
        return (candidate, exitIndex);
    }

    // Refuse strategy/session combinations whose entry, protection or exit semantics this policy cannot replay.
    private static void ValidateFineExecutionPolicy(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (run.Engine.ExecutionReplayPolicyId is not (ExecutionReplayPolicy.DailyRthFiveMinuteV1 or ExecutionReplayPolicy.DailyRthSipPortfolioV1) ||
            run.Mode != "backtest" || strategy.Timeframe != "1d" || strategy.Execution.Timeframe != "1d" ||
            strategy.Direction != "long" || strategy.EntryRules.EnableShort || strategy.ExitRules.EnableAtrTrailingStop ||
            strategy.ExitRules.EnableConfirmedVwapExit || strategy.Execution.Confirmation?.Enabled == true ||
            (run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthFiveMinuteV1 && !run.Intervals.Contains(ExecutionReplayPolicy.Timeframe)) ||
            strategy.Session.IsContinuousMarket || strategy.Session.UseExtendedHours || strategy.Session.QuietMinutesAfterOpen != 0 ||
            (strategy.Confluence.Enabled && strategy.Confluence.Timeframe != "1d") ||
            run.Engine.PrecommittedLiquidationSourceUtc is null)
            throw new InvalidOperationException("Unsupported daily/five-minute execution-policy combination.");
    }
}

internal static class FineExecutionTiming
{
    /// <summary>Bind the exclusive execution deadline to the authoritative exit session, including early closes.</summary>
    public static DateTimeOffset SessionClose(DateTimeOffset dailySource, string timezone,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(dailySource, zone).DateTime);
        if (!schedules.TryGetValue(date, out var session) || !session.IsTradingDay)
            throw new InvalidDataException($"No authoritative closing session for {dailySource:O}.");
        return At(date, session.RegularClose, zone);
    }
    public static (int Index, string Reason) DailyExit(StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> daily, IReadOnlyList<IndicatorSnapshot> snapshots, int entryIndex,
        DateTimeOffset terminalSource, Func<OhlcvBar, bool> eligible, Func<DateTimeOffset, DateTimeOffset> sessionOpen)
    {
        var entrySource = daily[entryIndex].Timestamp;
        var engine = new TechnicalExecutionEngine();
        // Match the daily baseline: entry session skipped, first subsequent daily
        // evaluation has barsHeld=1. Five-minute bars never increment this count.
        var held = 1;
        for (var i = entryIndex + 1; i < daily.Count; i++)
        {
            var bar = daily[i];
            var exitIndex = i;
            string? reason;
            if (bar.Timestamp == terminalSource) reason = "scheduled_study_exit";
            else
            {
                if (!eligible(bar)) continue;
                var priceTrend = TechnicalExecutionEngine.ComputeLogTrend(daily, i,
                    strategy.ExitRules.ExitLogPriceLookbackBars, b => b.Close, false);
                var volumeTrend = TechnicalExecutionEngine.ComputeLogTrend(daily, i,
                    strategy.ExitRules.ExitLogVolumeLookbackBars, b => b.Volume, true);
                reason = engine.GetTechnicalOrTimeExitReason(strategy, snapshots[i], held++,
                    bar.Timestamp, entrySource, priceTrend?.Slope, volumeTrend?.Slope, snapshots[i - 1]);
                if (reason is null) continue;
                var available = bar.Timestamp.AddDays(1);
                if (bar.KnownAtUtc > available) available = bar.KnownAtUtc.Value;
                exitIndex = Enumerable.Range(i + 1, daily.Count - i - 1)
                    .FirstOrDefault(j => eligible(daily[j]) && sessionOpen(daily[j].Timestamp) >= available, daily.Count);
                if (exitIndex >= daily.Count || daily[exitIndex].Timestamp >= terminalSource)
                {
                    exitIndex = Enumerable.Range(0, daily.Count).First(j => daily[j].Timestamp >= terminalSource);
                    reason = "scheduled_study_exit";
                }
            }
            return (exitIndex, reason);
        }
        throw new InvalidOperationException("Daily technical/terminal exit horizon unavailable.");
    }

    public static DateTimeOffset SessionOpen(DateTimeOffset dailySource, string timezone,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var local = TimeZoneInfo.ConvertTime(dailySource, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (local.TimeOfDay != TimeSpan.Zero || !schedules.TryGetValue(date, out var session) || !session.IsTradingDay)
            throw new InvalidDataException($"No authoritative daily source session at {dailySource:O}.");
        return At(date, session.RegularOpen, zone);
    }

    public static IReadOnlyList<DateTimeOffset> RequiredSlices(DateTimeOffset start, DateTimeOffset terminalOpen,
        string timezone, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(terminalOpen, zone).DateTime);
        var result = new List<DateTimeOffset>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!schedules.TryGetValue(date, out var session) || session.TradeDate != date)
                throw new InvalidDataException($"Execution calendar missing {date}.");
            if (!session.IsTradingDay) continue;
            var close = At(date, session.RegularClose, zone);
            for (var t = At(date, session.RegularOpen, zone); t.AddMinutes(5) <= close && t <= terminalOpen; t = t.AddMinutes(5))
                if (t >= start) result.Add(t);
        }
        return result;
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
