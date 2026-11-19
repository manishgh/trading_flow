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
        string candidateId, BacktestRunConfig run, AuthorizedRuntimeStrategy runtimeStrategy, StrategyDefinition strategy, TradeSignal signal,
        StrategyOrderPlan plan, IReadOnlyList<OhlcvBar> daily, IReadOnlyList<IndicatorSnapshot> snapshots,
        int entryIndex, ExecutionAuditor auditor)
    {
        ValidateFineExecutionPolicy(run, runtimeStrategy, strategy);
        if (entryIndex <= 0 || daily.Take(entryIndex).Any(x => x.AdjustmentPolicy != "raw"))
            throw new InvalidDataException("The SIP portfolio policy requires explicit raw completed daily inputs.");
        var schedules = candidateSessionSchedules ?? throw new InvalidOperationException("Event execution requires authoritative sessions.");
        var terminal = FineExecutionTiming.SessionOpen(run.Engine.PrecommittedLiquidationSourceUtc!.Value, strategy.Session.ExchangeTimezone, schedules);
        var entry = FineExecutionTiming.SessionOpen(daily[entryIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules);
        if (entry < plan.TriggeredAtUtc || daily[entryIndex - 1].KnownAtUtc > entry)
            throw new InvalidDataException("Daily entry evidence is not known before its route.");
        var reference = daily[entryIndex - 1].Close;
        var distance = reference - plan.InitialStopPrice;
        if (distance <= 0 || strategy.ExitRules.HasProtectiveStop && plan.InitialStopPrice <= 0) return null;
        var target = ResolveTakeProfitPrice(plan, reference, distance);
        if (strategy.ExitRules.HasProfitTarget && target <= reference) return null;
        var (exitIndex, reason, evidence) = FineExecutionTiming.DailyExit(strategy, daily, snapshots, entryIndex,
            run.Engine.PrecommittedLiquidationSourceUtc.Value,
            bar => _sessionClock.ValidateExecutionWindow(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session),
            source => FineExecutionTiming.SessionOpen(source, strategy.Session.ExchangeTimezone, schedules), schedules);
        var resolvedExitIndex = exitIndex ?? throw new InvalidOperationException("Daily technical/terminal exit horizon unavailable.");
        var exit = FineExecutionTiming.SessionOpen(daily[resolvedExitIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules);
        var candidate = BuildCandidate(candidateId, strategy, signal, "long", entry, reference,
            plan.InitialStopPrice, target, exit, 0m, reason, distance, daily[entryIndex - 1].Volume) with
        { ExitEvidence = evidence, ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, daily[entryIndex].Timestamp,
            daily[resolvedExitIndex].Timestamp, terminal, [])
            { EventExecutionEndUtc = FineExecutionTiming.SessionClose(daily[resolvedExitIndex].Timestamp, strategy.Session.ExchangeTimezone, schedules) } };
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, plan.TriggeredAtUtc, ExecutionState.SignalGenerated,
            "Completed raw daily intent; execution delegated to shared SIP event portfolio", candidateId);
        return (candidate, resolvedExitIndex);
    }
    // Daily candidate intent has no protective-price horizon under this policy.
    // The chronological book alone observes protective prices on execution slices.
    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateFineExecutionCandidate(
        string candidateId, BacktestRunConfig run, AuthorizedRuntimeStrategy runtimeStrategy, StrategyDefinition strategy, TradeSignal signal,
        StrategyOrderPlan plan, IReadOnlyList<OhlcvBar> daily,
        IReadOnlyList<IndicatorSnapshot> snapshots, IReadOnlyList<OhlcvBar> fine,
        int entryIndex, ExecutionAuditor auditor)
    {
        ValidateFineExecutionPolicy(run, runtimeStrategy, strategy);
        var schedules = candidateSessionSchedules
            ?? throw new InvalidOperationException("Fine execution requires authoritative sessions.");
        var precommittedTerminal = run.Engine.PrecommittedLiquidationSourceUtc;
        var entrySource = daily[entryIndex].Timestamp;
        var entryOpen = FineExecutionTiming.SessionOpen(entrySource, strategy.Session.ExchangeTimezone, schedules);
        if (entryOpen < plan.TriggeredAtUtc)
            throw new InvalidOperationException("Opening execution cannot precede actionable daily evidence.");
        var opening = FindUniqueSliceAt(fine, entryOpen)
            ?? throw new InvalidDataException($"Opening execution slice missing: {signal.Ticker} {entryOpen:O}.");
        var isShort = plan.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        var amount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var referenceVolume = snapshots[plan.StopContextIndex].RelativeVolume ?? 1m;
        var entryPrice = isShort
            ? DynamicSlippageModel.ApplyShortEntrySlippage(opening.Open, referenceVolume, strategy.Execution.SlippageBps, amount)
            : DynamicSlippageModel.ApplyLongSlippage(opening.Open, referenceVolume, strategy.Execution.SlippageBps, amount);
        // A kept gap needs no special exit here: the book's stop acts on the first slice after the fill.
        var planningPrice = strategy.Execution.KeepsOpeningGaps() ? daily[entryIndex - 1].Close : entryPrice;
        var distance = isShort ? plan.InitialStopPrice - planningPrice : planningPrice - plan.InitialStopPrice;
        if (distance <= 0 || strategy.ExitRules.HasProtectiveStop && plan.InitialStopPrice <= 0) return null;
        var target = ResolveTakeProfitPrice(plan, planningPrice, distance);
        if (strategy.ExitRules.HasProfitTarget && (isShort ? (target <= 0m || target >= planningPrice) : target <= planningPrice)) return null;

        var (exitIndex, reason, evidence) = FineExecutionTiming.DailyExit(strategy, daily, snapshots, entryIndex,
            precommittedTerminal, bar => _sessionClock.ValidateExecutionWindow(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session),
            source => FineExecutionTiming.SessionOpen(source, strategy.Session.ExchangeTimezone, schedules), schedules, isShort);
        var exitSource = exitIndex is { } resolvedExit ? daily[resolvedExit].Timestamp : daily[^1].Timestamp;
        DateTimeOffset? exitOpen = exitIndex is { } resolvedExitIdx
            ? FineExecutionTiming.SessionOpen(daily[resolvedExitIdx].Timestamp, strategy.Session.ExchangeTimezone, schedules)
            : null;
        DateTimeOffset terminalOpen;
        DateTimeOffset lastRequiredSlice;
        if (precommittedTerminal is { } requiredTerminal)
        {
            terminalOpen = FineExecutionTiming.SessionOpen(requiredTerminal, strategy.Session.ExchangeTimezone, schedules);
            lastRequiredSlice = terminalOpen;
        }
        else
        {
            // Kept-gap as-traded swing strategies run without a precommitted terminal open: cap required 5m RTH slices
            // at the resolved daily exit session's close when a technical/time exit is scheduled, or the final daily
            // session's close when the position remains open_at_study_end.
            terminalOpen = DateTimeOffset.MaxValue;
            lastRequiredSlice = FineExecutionTiming.SessionClose(exitSource, strategy.Session.ExchangeTimezone, schedules).AddMinutes(-5);
        }

        var required = FineExecutionTiming.RequiredSlices(entryOpen, lastRequiredSlice,
            strategy.Session.ExchangeTimezone, schedules);
        var candidate = BuildCandidate(candidateId, strategy, signal, isShort ? "short" : "long", entryOpen,
            entryPrice, plan.InitialStopPrice, target, exitOpen, exitOpen.HasValue ? 0m : null, reason, distance,
            daily[entryIndex - 1].Volume) with
        {
            ExitEvidence = evidence,
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1,
                entrySource, exitSource, terminalOpen, required)
        };
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, plan.TriggeredAtUtc,
            ExecutionState.SignalGenerated, "Daily intent; protective execution delegated to five-minute book", candidateId);
        return (candidate, exitIndex ?? daily.Count - 1);
    }

    // Refuse unevidenced entry, protection, or accounting combinations before execution starts.
    private static void ValidateFineExecutionPolicy(BacktestRunConfig run, AuthorizedRuntimeStrategy? runtimeStrategy, StrategyDefinition strategy)
    {
        if (run.Engine.AccountLossReplay is { Enabled: true } &&
            (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.DailyRthFiveMinuteV1 ||
             strategy.Session.ExchangeTimezone != "America/New_York" || run.Portfolio.StartingCapital <= 0m))
            throw new InvalidOperationException("Sampled account loss requires a New York regular-session five-minute IOC account with positive starting capital.");
        var isSingleDirectionLong = strategy.Direction == "long" && !strategy.EntryRules.EnableShort;
        var isSingleDirectionShort = strategy.Direction == "short" && strategy.EntryRules.EnableShort;
        var directionSupported = run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthFiveMinuteV1
            ? isSingleDirectionLong || isSingleDirectionShort
            : isSingleDirectionLong;
        var allowsWindowCutoffHorizon = run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthFiveMinuteV1 &&
            runtimeStrategy is not null &&
            ExecutionRules.IsAsTradedResearchTiming(runtimeStrategy.AdmissionProfile.ExecutionTimingPolicy);
        if (run.Engine.ExecutionReplayPolicyId is not (ExecutionReplayPolicy.DailyRthFiveMinuteV1 or ExecutionReplayPolicy.DailyRthSipPortfolioV1) ||
            run.Mode != "backtest" || strategy.Timeframe != "1d" || strategy.Execution.Timeframe != "1d" ||
            !directionSupported || strategy.ExitRules.EnableAtrTrailingStop ||
            strategy.ExitRules.EnableConfirmedVwapExit || strategy.Execution.Confirmation?.Enabled == true ||
            (run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthFiveMinuteV1 && !run.Intervals.Contains(ExecutionReplayPolicy.Timeframe)) ||
            strategy.Session.IsContinuousMarket || strategy.Session.UseExtendedHours || strategy.Session.QuietMinutesAfterOpen != 0 ||
            (strategy.Confluence.Enabled && strategy.Confluence.Timeframe != "1d") ||
            (run.Engine.PrecommittedLiquidationSourceUtc is null && !allowsWindowCutoffHorizon))
            throw new InvalidOperationException("Unsupported daily/five-minute execution-policy combination.");
    }

    // Evaluates SEC Rule 201 at session D+1 open: if the D+1 opening print (daily open or 09:30 NY 5m open when
    // DailyRthFiveMinuteV1 is active) drops >= 10% from session D's regular close (Open <= 0.90 * Close_D), Rule 201
    // is triggered immediately at the open and market short entry is refused.
    private bool IsSecRule201OpeningGapTriggered(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> executionBars,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe,
        int plannedEntryIndex)
    {
        var priorClose = executionBars[plannedEntryIndex - 1].Close;
        if (priorClose <= 0m) return false;
        var threshold = priorClose * 0.90m;
        if (executionBars[plannedEntryIndex].Open <= threshold) return true;
        if (run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthFiveMinuteV1 &&
            candidateSessionSchedules is { } schedules &&
            barsByTimeframe.TryGetValue(ExecutionReplayPolicy.Timeframe, out var fine))
        {
            var entryOpen = FineExecutionTiming.SessionOpen(
                executionBars[plannedEntryIndex].Timestamp,
                strategy.Session.ExchangeTimezone,
                schedules);
            var opening = FindUniqueSliceAt(fine, entryOpen);
            if (opening is not null && opening.Open <= threshold) return true;
        }
        return false;
    }

    /// <summary>
    /// Locates the unique bar in <paramref name="fine"/> whose timestamp equals <paramref name="timestamp"/>.
    /// Uses O(log N) binary search with adjacent duplicate verification when sorted ascending, and falls back to a
    /// linear scan if unsorted so duplicate timestamps still throw <see cref="InvalidOperationException"/>.
    /// </summary>
    private static OhlcvBar? FindUniqueSliceAt(IReadOnlyList<OhlcvBar> fine, DateTimeOffset timestamp)
    {
        if (fine.Count == 0) return null;
        if (fine.Count == 1) return fine[0].Timestamp == timestamp ? fine[0] : null;
        if (fine[0].Timestamp <= fine[^1].Timestamp)
        {
            var low = 0;
            var high = fine.Count - 1;
            while (low <= high)
            {
                var mid = low + ((high - low) >> 1);
                var midTimestamp = fine[mid].Timestamp;
                if (midTimestamp == timestamp)
                {
                    if ((mid > 0 && fine[mid - 1].Timestamp == timestamp) ||
                        (mid + 1 < fine.Count && fine[mid + 1].Timestamp == timestamp))
                    {
                        throw new InvalidOperationException($"Duplicate execution slice timestamp observed: {timestamp:O}.");
                    }
                    return fine[mid];
                }
                if (midTimestamp < timestamp) low = mid + 1;
                else high = mid - 1;
            }
            var unordered = (low > 0 && low < fine.Count && fine[low - 1].Timestamp > fine[low].Timestamp) ||
                            (low > 1 && fine[low - 2].Timestamp > fine[low - 1].Timestamp);
            if (!unordered) return null;
        }
        return fine.SingleOrDefault(bar => bar.Timestamp == timestamp);
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
    // Determine the existing route and report all matches at its source close, prior to replaying finer protection.
    // When terminalSource is null (kept-gap as-traded study without a precommitted terminal open), returns Index = null
    // and Reason = "open_at_study_end" if no technical/time exit triggers within the available daily horizon.
    public static (int? Index, string Reason, BacktestExitEvidence? Evidence) DailyExit(StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> daily, IReadOnlyList<IndicatorSnapshot> snapshots, int entryIndex,
        DateTimeOffset? terminalSource, Func<OhlcvBar, bool> eligible, Func<DateTimeOffset, DateTimeOffset> sessionOpen,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null, bool isShort = false)
    {
        var entrySource = daily[entryIndex].Timestamp;
        var engine = new TechnicalExecutionEngine();
        // Match the daily baseline: the first evaluated session has barsHeld=1. It is the session after the entry, or the
        // entry session itself with check_exits_from_fill_day. Five-minute bars never increment this count.
        var held = 1;
        var firstCheck = strategy.ExitRules.CheckExitsFromFillDay ? entryIndex : entryIndex + 1;
        for (var i = firstCheck; i < daily.Count; i++)
        {
            var bar = daily[i];
            var exitIndex = i;
            BacktestExitEvidence? evidence = null;
            string? reason;
            if (terminalSource is { } requiredTerminal && i > entryIndex && bar.Timestamp == requiredTerminal)
            {
                reason = "scheduled_study_exit";
            }
            else
            {
                if (!eligible(bar)) continue;
                var barsHeld = held++;
                var observations = new List<BacktestExitCondition>();
                if (isShort)
                {
                    reason = TechnicalExecutionEngine.GetShortTechnicalOrTimeExitReason(strategy, snapshots[i], barsHeld,
                        bar.Timestamp, entrySource, snapshots[i - 1], observations);
                }
                else
                {
                    var priceTrend = TechnicalExecutionEngine.ComputeLogTrend(daily, i,
                        strategy.ExitRules.ExitLogPriceLookbackBars, b => b.Close, false);
                    var volumeTrend = TechnicalExecutionEngine.ComputeLogTrend(daily, i,
                        strategy.ExitRules.ExitLogVolumeLookbackBars, b => b.Volume, true);
                    reason = engine.GetTechnicalOrTimeExitReason(strategy, snapshots[i], barsHeld,
                        bar.Timestamp, entrySource, priceTrend?.Slope, volumeTrend?.Slope, snapshots[i - 1], observations);
                }
                if (reason is null) continue;
                evidence = BacktestDecisionTiming.DescribeCompletedExit(bar, strategy, observations, schedules);
                var available = bar.Timestamp.AddDays(1);
                if (bar.KnownAtUtc > available) available = bar.KnownAtUtc.Value;
                exitIndex = Enumerable.Range(i + 1, daily.Count - i - 1)
                    .FirstOrDefault(j => eligible(daily[j]) && sessionOpen(daily[j].Timestamp) >= available, daily.Count);
                if (terminalSource is { } forcedTerminal)
                {
                    if (exitIndex >= daily.Count || daily[exitIndex].Timestamp >= forcedTerminal)
                    {
                        exitIndex = Enumerable.Range(0, daily.Count).First(j => daily[j].Timestamp >= forcedTerminal);
                        reason = "scheduled_study_exit";
                    }
                }
                else if (exitIndex >= daily.Count)
                {
                    return (null, "open_at_study_end", evidence);
                }
            }
            return (exitIndex, reason, evidence);
        }

        if (terminalSource is null)
        {
            return (null, "open_at_study_end", null);
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
