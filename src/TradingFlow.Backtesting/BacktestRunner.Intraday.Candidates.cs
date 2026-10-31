using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    // Build ordinary persisted candidates with bounded previous/current session slices; execution alone observes future prices.
    private async Task<IReadOnlyList<StrategyTickerBacktestBatch>> BuildIntradayCandidatesAsync(
        BacktestRunConfig run, StrategyDefinition strategy, PreparedBacktestMarket preparedMarket,
        CancellationToken cancellationToken)
    {
        var schedules = candidateSessionSchedules ?? throw new InvalidDataException("Intraday candidates require authoritative exchange sessions.");
        var runtime = ResolveRuntimeStrategy(strategy);
        var evaluator = new PreviousSessionBreakoutEvaluator();
        var openingEvaluator = new OpeningRangeBreakoutEvaluator();
        var openingRange = strategy.EntryRules.OpeningRangeBreakout is not null;
        var budget = new RetainedCandidateBudget(run.Engine.MaxRetainedCandidates);
        var batches = new List<StrategyTickerBacktestBatch>();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var priorDates = IntradayPreviousSessionDates(schedules);
        foreach (var ticker in run.Tickers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preparedMarket.MarketState.TickerStates.TryGetValue(ticker, out var state) ||
                !state.BarsByTimeframe.TryGetValue("5m", out var bars) ||
                !state.SnapshotsByTimeframe.TryGetValue("5m", out var snapshots))
            {
                batches.Add(StrategyTickerBacktestBatch.Failed(ticker, strategy, "five_minute_market_evidence_unavailable"));
                batches[^1] = batches[^1] with { IntradayUnavailableFromUtc = preparedMarket.WindowStart };
                continue;
            }
            var sessionBars = bars.Where(bar => IsIntradayRegularBar(bar, schedules, zone))
                .GroupBy(bar => IntradaySessionDate(bar.Timestamp, zone))
                .ToDictionary(group => group.Key, group => group.OrderBy(bar => bar.Timestamp).ToArray());
            var snapshotMap = snapshots.ToDictionary(snapshot => snapshot.Timestamp);
            var candidates = new List<BacktestCandidateTrade>();
            var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
            DateTimeOffset? unavailableFrom = FindIntradaySourceGap(sessionBars, schedules, priorDates, zone,
                preparedMarket.WindowStart, preparedMarket.WindowEnd);
            if (!sessionBars.Values.SelectMany(value => value).Any(bar => bar.Timestamp >= preparedMarket.WindowStart &&
                bar.Timestamp.AddMinutes(5) <= preparedMarket.WindowEnd)) unavailableFrom = preparedMarket.WindowStart;
            if (unavailableFrom is not null) diagnostics.IncrementRejection("required_five_minute_setup_source_incomplete");
            using var journal = new BacktestCandidateJournal(CreateCandidateAuditJournalPath(strategy, ticker));
            var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
            var simulationRun = BacktestCandidateJournal.CreateRun(
                new { Run = CandidateIdentityConfiguration(run), Strategy = runtime.Identity, Ticker = ticker },
                $"{run.RunName}:{runtime.Identity.StrategyId}:{ticker}", preparedMarket.EvaluationStart);
            foreach (var day in sessionBars.OrderBy(pair => pair.Key))
            {
                if (!priorDates.TryGetValue(day.Key, out var priorDate) ||
                    !sessionBars.TryGetValue(priorDate, out var priorBars))
                {
                    diagnostics.IncrementRejection("previous_exchange_session_prices_unavailable");
                    continue;
                }
                var baselineSessions = openingRange ? schedules.Values.Where(session => session.IsTradingDay && session.TradeDate < day.Key)
                    .OrderBy(session => session.TradeDate).TakeLast(20).ToArray() : [];
                var baselineBars = openingRange ? OpeningRangeBaselineBars(sessionBars, baselineSessions, zone) : null;
                var calendarStart = baselineSessions.Length > 0 ? baselineSessions[0].TradeDate : priorDate;
                var boundedCalendar = schedules.Where(pair => pair.Key >= calendarStart && pair.Key <= day.Key)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                for (var index = 1; index < day.Value.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var confirmation = day.Value[index];
                    var asOf = confirmation.Timestamp.AddMinutes(5);
                    if (asOf < preparedMarket.EvaluationStart || asOf > preparedMarket.WindowEnd) continue;
                    diagnostics.EvaluatedBarCount++;
                    var completed = priorBars.Concat(day.Value.Take(index + 1)).ToArray();
                    var increment = UsEquityOrderIncrementResolver.Resolve(day.Key,
                        completed.Min(bar => bar.Low), completed.Max(bar => bar.High));
                    if (increment is null)
                    {
                        unavailableFrom = EarlierIntradayBoundary(unavailableFrom, asOf);
                        diagnostics.IncrementRejection("documented_order_increment_unavailable_for_price_or_date");
                        continue;
                    }
                    var context = new PreviousSessionBreakoutContext(boundedCalendar, increment.TickSize,
                        asOf, increment.PolicyId + ":" + increment.SourceUrl + ";" +
                        UsEquityOrderIncrementResolver.EffectiveBoundarySourceUrl + ";" + UsEquityOrderIncrementResolver.EndBoundarySourceUrl,
                        "retrospectively_downloaded_exchange_calendar;scheduled_session_bounds_assumed_available_at_decision;original_receipt_time_not_observed")
                    { OpeningRangeBaselineBars = baselineBars };
                    var completedSnapshots = completed.Where(bar => snapshotMap.ContainsKey(bar.Timestamp))
                        .Select(bar => snapshotMap[bar.Timestamp]).ToArray();
                    var preview = openingRange
                        ? openingEvaluator.Evaluate(strategy, completed, completedSnapshots, context, asOf)
                        : evaluator.Evaluate(strategy, completed, context, asOf);
                    // Persist every observed crossing, including failed confirmation/pattern evidence; ordinary non-setups stay aggregated.
                    if (!preview.HasBreakout)
                    {
                        if (IntradaySetupEvidenceUnavailable(preview.RejectionReason))
                            unavailableFrom = EarlierIntradayBoundary(unavailableFrom, asOf);
                        diagnostics.IncrementRejection(preview.RejectionReason ?? "previous_session_breakout_unavailable");
                        continue;
                    }
                    if (completed.Any(bar => !snapshotMap.ContainsKey(bar.Timestamp)))
                    {
                        var missing = completed.First(bar => !snapshotMap.ContainsKey(bar.Timestamp));
                        var boundary = missing.Timestamp < preparedMarket.WindowStart ? asOf : missing.Timestamp;
                        unavailableFrom = unavailableFrom is { } existing && existing < boundary ? existing : boundary;
                        diagnostics.IncrementRejection("required_completed_snapshot_missing");
                        continue;
                    }
                    var key = StrategyDecisionRequestAssembler.CreateSetupKey(strategy.StrategyId, "5m", completed[^2].Timestamp);
                    var expiry = preview.Signal?.ExpiresAtUtc ?? asOf.AddMinutes(5);
                    var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(ticker, key, asOf, expiry);
                    var eligibility = new StrategyEligibilityEvidence(true, "static_run_universe",
                        $"{ticker}:{day.Key:yyyy-MM-dd}", asOf, JsonSerializer.Serialize(new { ticker, sessionDate = day.Key }));
                    var request = StrategyDecisionRequestAssembler.Create(runtime, ticker,
                        new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = completed },
                        new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = completedSnapshots },
                        Array.Empty<CatalystEvent>(), asOf, discovery, eligibility,
                        eligibility with { Source = "strategy_has_no_regime_gate" }, StrategyCandidateState.DataWarming,
                        0, key, asOf, expiry, 2, simulationRun.RunId, previousSessionContext: context);
                    var persisted = await orchestrator.EvaluateAsync(simulationRun, request, cancellationToken);
                    if (persisted.Decision?.OrderPlan?.PendingEntry is not { } pending)
                    {
                        diagnostics.IncrementRejection(persisted.Decision?.NoEntryReason ?? "previous_session_order_not_triggered");
                        continue;
                    }
                    budget.Reserve();
                    candidates.Add(CreateIntradayPendingCandidate(strategy, ticker,
                        persisted.Candidate.CandidateId.ToString("D"), pending, confirmation.Volume));
                    diagnostics.CandidateTradeCount++;
                }
            }
            batches.Add(new StrategyTickerBacktestBatch(ticker, strategy.StrategyId, strategy.StrategyName,
                unavailableFrom is null, unavailableFrom is null ? null :
                    $"Required previous/current regular-session five-minute setup evidence is incomplete; first affected time {unavailableFrom:O}.",
                candidates, [], journal.DurableJournalPath, diagnostics) { IntradayUnavailableFromUtc = unavailableFrom });
        }
        return batches;
    }

    // Capture only the actual first-three intervals from each required prior session once; absent records stay absent for the shared evaluator.
    private static IReadOnlyList<OhlcvBar> OpeningRangeBaselineBars(IReadOnlyDictionary<DateOnly, OhlcvBar[]> sessionBars,
        IReadOnlyList<MarketSessionSchedule> sessions, TimeZoneInfo zone)
    {
        var result = new List<OhlcvBar>(sessions.Count * 3);
        foreach (var session in sessions)
        {
            if (!sessionBars.TryGetValue(session.TradeDate, out var source)) continue;
            var open = IntradayCalendarAtUtc(session.TradeDate, session.RegularOpen, zone);
            result.AddRange(source.Where(bar => bar.Timestamp >= open && bar.Timestamp < open.AddMinutes(15)));
        }
        return result;
    }

    // Distinguish unavailable mandatory evidence from an observed candle that simply fails the approved pattern.
    private static bool IntradaySetupEvidenceUnavailable(string? reason) => reason is
        "previous_session_tick_or_calendar_unavailable" or "previous_session_completed_bars_unavailable" or
        "previous_session_bar_evidence_invalid" or "current_exchange_session_unavailable" or
        "previous_exchange_session_unavailable" or "previous_session_five_minute_coverage_incomplete" or
        "breakout_previous_close_missing" or "opening_range_tick_or_calendar_unavailable" or
        "opening_range_completed_bar_evidence_invalid" or "opening_range_current_session_coverage_incomplete" or
        "opening_range_volume_baseline_unavailable";

    // Preserve the earliest unsupported source boundary while retaining later diagnostic candidates and audit evidence.
    private static DateTimeOffset EarlierIntradayBoundary(DateTimeOffset? existing, DateTimeOffset proposed) =>
        existing is { } earlier && earlier < proposed ? earlier : proposed;

    // Mark the first missing mandatory source interval only when it can affect this study; warm-up-only dates do not fail a work item.
    internal static DateTimeOffset? FindIntradaySourceGap(IReadOnlyDictionary<DateOnly, OhlcvBar[]> sessionBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, IReadOnlyDictionary<DateOnly, DateOnly> priorDates,
        TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        foreach (var session in schedules.Values.Where(value => value.IsTradingDay).OrderBy(value => value.TradeDate))
        {
            var open = IntradayCalendarAtUtc(session.TradeDate, session.RegularOpen, zone);
            var close = IntradayCalendarAtUtc(session.TradeDate, session.RegularClose, zone);
            if (close <= start || open >= end) continue;
            var boundary = open < start ? start : open;
            if (!priorDates.TryGetValue(session.TradeDate, out var previous) ||
                !schedules.TryGetValue(previous, out var priorSession) ||
                !sessionBars.TryGetValue(previous, out var priorBars)) return boundary;
            var priorOpen = IntradayCalendarAtUtc(previous, priorSession.RegularOpen, zone);
            var priorClose = IntradayCalendarAtUtc(previous, priorSession.RegularClose, zone);
            var priorStarts = priorBars.Select(bar => bar.Timestamp).ToHashSet();
            for (var time = priorOpen; time < priorClose; time = time.AddMinutes(5))
                if (!priorStarts.Contains(time)) return boundary;
            if (!sessionBars.TryGetValue(session.TradeDate, out var currentBars)) return boundary;
            var currentStarts = currentBars.Select(bar => bar.Timestamp).ToHashSet();
            for (var time = open; time.AddMinutes(5) <= close && time.AddMinutes(5) <= end; time = time.AddMinutes(5))
                if (time >= start && !currentStarts.Contains(time)) return time;
        }
        return null;
    }

    // Convert calendar wall-clock bounds for explicit interval coverage checks.
    private static DateTimeOffset IntradayCalendarAtUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    // Preserve completed intent separately from actual fills; target and risk are recomputed from each actual modeled fill.
    private static BacktestCandidateTrade CreateIntradayPendingCandidate(StrategyDefinition strategy, string ticker,
        string candidateId, PreviousSessionBreakoutSignal pending, decimal completedVolume)
    {
        var risk = Math.Abs(pending.LimitPrice - pending.InitialStopPrice);
        var target = pending.LimitPrice + (pending.Direction == "long" ? 1m : -1m) * strategy.ExitRules.TargetRMultiple * risk;
        return new BacktestCandidateTrade(ticker, strategy.StrategyName, pending.Direction, pending.ActiveAtUtc,
            pending.LimitPrice, pending.InitialStopPrice, target, pending.SessionDeadlineUtc, null,
            "session_deadline_pending", risk, completedVolume, StrategyId: strategy.StrategyId, CandidateId: candidateId)
        {
            DecisionStrategyId = strategy.StrategyId,
            IntradayExecution = new(pending.LimitPrice, pending.TickSize, pending.ActiveAtUtc, pending.ExpiresAtUtc,
                pending.SessionDeadlineUtc, pending.PreviousSessionHigh, pending.PreviousSessionLow,
                pending.BreakoutTimestampUtc, pending.ConfirmationTimestampUtc, pending.EntryPattern)
            {
                EntryOrderMode = pending.EntryOrderMode,
                BreakoutTriggerPrice = pending.BreakoutTriggerPrice,
                OpeningRangeHigh = pending.OpeningRangeHigh, OpeningRangeLow = pending.OpeningRangeLow,
                OpeningRangeOpen = pending.OpeningRangeOpen, OpeningRangeClose = pending.OpeningRangeClose,
                OpeningRangeCompletedAtUtc = pending.OpeningRangeCompletedAtUtc,
                OpeningRelativeVolume = pending.OpeningRelativeVolume,
                OpeningVolumeBaselineSampleCount = pending.OpeningVolumeBaselineSampleCount,
                MaxFilledParentTradesPerSession = pending.MaxFilledParentTradesPerSession,
                RequireRangeReturnBeforeReentry = pending.RequireRangeReturnBeforeReentry
            },
            ExecutionReplay = new(ExecutionReplayPolicy.ForEntryMode(pending.EntryOrderMode), pending.BreakoutTimestampUtc,
                pending.ConfirmationTimestampUtc, pending.SessionDeadlineUtc, [])
        };
    }

    // Index preceding sessions once, requiring explicit closed dates between consecutive trading sessions.
    private static IReadOnlyDictionary<DateOnly, DateOnly> IntradayPreviousSessionDates(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        var result = new Dictionary<DateOnly, DateOnly>();
        DateOnly? previousTradingDay = null;
        DateOnly? previousCalendarDay = null;
        foreach (var pair in schedules.OrderBy(pair => pair.Key))
        {
            if (previousCalendarDay is { } prior && pair.Key != prior.AddDays(1)) previousTradingDay = null;
            if (pair.Value.IsTradingDay)
            {
                if (previousTradingDay is { } tradingDay) result[pair.Key] = tradingDay;
                previousTradingDay = pair.Key;
            }
            previousCalendarDay = pair.Key;
        }
        return result;
    }

    // Filter regular bars using provider session bounds, including holidays, early closes and historical daylight saving time.
    private static bool IsIntradayRegularBar(OhlcvBar bar,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(bar.Timestamp, zone);
        return schedules.TryGetValue(DateOnly.FromDateTime(local.DateTime), out var session) && session.IsTradingDay &&
            TimeOnly.FromDateTime(local.DateTime) >= session.RegularOpen &&
            TimeOnly.FromDateTime(local.AddMinutes(5).DateTime) <= session.RegularClose;
    }

    // Keep source-session grouping explicit in New York even when UTC and local dates differ.
    private static DateOnly IntradaySessionDate(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
}
