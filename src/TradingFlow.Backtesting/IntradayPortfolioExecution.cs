using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting;

public sealed record IntradayPortfolioReplayRequest(PortfolioConfig Portfolio,
    IReadOnlyList<StrategyDefinition> Strategies, IReadOnlyList<BacktestCandidateTrade> Candidates,
    IReadOnlyCollection<OhlcvBar> MarketBars, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Sessions,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    // Per stock (upper case): interval starts with no candle that trade records prove had no price-setting trade.
    IReadOnlyDictionary<string, IReadOnlySet<DateTimeOffset>>? ConfirmedQuietIntervals = null);
public sealed record IntradayReplayEvent(string CandidateId, string Ticker, DateTimeOffset TimestampUtc,
    string State, string Reason, int Quantity = 0, decimal? Price = null,
    decimal? StopPrice = null, decimal? TargetPrice = null, DateTimeOffset? EffectiveAfterUtc = null)
{
    public DateTimeOffset? SourceIntervalStartUtc { get; init; }
    public string? ExecutionPhase { get; init; }
    public long? ExecutionSequence { get; init; }
}
public sealed record IntradayPortfolioReplayOutcome(IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<BacktestExecutionFailure> Failures, IReadOnlyList<EventPortfolioDisposition> Dispositions,
    IReadOnlyList<IntradayReplayEvent> Events, BacktestPortfolioState PortfolioState)
{
    public IReadOnlyList<IntradayExecutionFill> FillEvents { get; init; } = [];
}
public sealed record IntradayExecutionFill(string CandidateId, SimulatedExecutionEvent Event)
{
    public DateTimeOffset SourceIntervalStartUtc { get; init; }
    public string ExecutionPhase { get; init; } = "";
    public long ExecutionSequence { get; init; }
}

/// <summary>Bounded OHLC adapter: shared risk sizing, shared fills/costs and completed-candle protection in one chronological book.</summary>
public static partial class IntradayPortfolioExecution
{
    public const string ValuationBasis = "intraday_ohlc_diagnostic_marked_to_market";
    /// <summary>Replay a frozen ordinary saved plan; no provider calls or strategy-specific research driver is involved.</summary>
    public static IntradayPortfolioReplayOutcome Replay(IntradayPortfolioReplayRequest request,
        CancellationToken cancellationToken = default) => new Book(request).Run(cancellationToken);

    private sealed class Intent(BacktestCandidateTrade candidate, StrategyDefinition strategy, int requested, decimal maximumNotional)
    {
        public BacktestCandidateTrade Candidate { get; } = candidate;
        public StrategyDefinition Strategy { get; } = strategy;
        public IntradayExecutionEvidence Evidence => Candidate.IntradayExecution!;
        public int Requested { get; } = requested;
        public decimal MaximumNotional { get; } = maximumNotional;
        public int Entered { get; set; }
        public int Exited { get; set; }
        public int Open => Entered - Exited;
        public decimal EntryNotional { get; set; }
        public decimal LastEntryFillPrice { get; set; }
        public decimal ExitNotional { get; set; }
        public decimal Fees { get; set; }
        public bool Pending { get; set; } = true;
        public bool StopTriggered { get; set; }
        public DateTimeOffset? StopTriggeredAt { get; set; }
        public bool EntryTriggered { get; set; }
        public DateTimeOffset? EntryBarStart { get; set; }
        public bool StopMarket => Evidence.EntryOrderMode == IntradayEntryOrderMode.StopMarket;
        public bool NextBarMarket => Evidence.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket;
        public bool MarketEntry => StopMarket || NextBarMarket;
        public string? ExitReason { get; set; }
        public DateTimeOffset? EntryAt { get; set; }
        public DateTimeOffset? ExitAt { get; set; }
        public DateTimeOffset? LastFillAt { get; set; }
        public decimal? Mark { get; set; }
        public DateTimeOffset? MarkAt { get; set; }
        public ContinuationProtectionState? Protection { get; set; }
        public bool Long => Candidate.Direction == "long";
        public decimal Entry => Entered == 0 ? Candidate.EntryPrice : EntryNotional / Entered;
        public decimal Stop => Protection?.StopPrice ?? Candidate.StopLossPrice;
        public decimal Target => Protection?.TargetPrice ?? (Entered > 0 ? InitialTarget : Candidate.TakeProfitPrice);
        public bool Failed { get; set; }
        public decimal EntryTaf { get; set; }
        public decimal ExitTaf { get; set; }
        public decimal InitialTarget { get; set; }
        public decimal StopBeforeUpdate { get; set; }
        public decimal TargetBeforeUpdate { get; set; }
        public DateTimeOffset? ProtectionEffectiveAfterUtc { get; set; }
        public Dictionary<string, int> OrderFilledQuantities { get; } = new(StringComparer.Ordinal);
    }

    private sealed partial class Book
    {
        private readonly IntradayPortfolioReplayRequest request;
        private readonly Dictionary<(string Symbol, DateTimeOffset Time), OhlcvBar> bars;
        private readonly Dictionary<string, StrategyDefinition> strategies;
        private readonly BacktestCandidateTrade[] candidates;
        private readonly List<Intent> intents = [];
        private readonly List<Intent> live = [];
        private readonly List<BacktestTrade> trades = [];
        private readonly List<BacktestExecutionFailure> failures = [];
        private readonly List<EventPortfolioDisposition> dispositions = [];
        private readonly List<IntradayReplayEvent> events = [];
        private readonly List<BacktestPortfolioFill> fills = [];
        private readonly List<IntradayExecutionFill> fillEvents = [];
        private readonly List<BacktestSessionObservation> observations = [];
        private readonly List<BacktestSessionPositionMark> marks = [];
        private readonly Dictionary<string, DateTimeOffset> resolved = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Symbol, DateOnly Session), int> filledParents = [];
        private readonly Dictionary<string, DateTimeOffset> rangeReturns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IntradayExecutionEvidence> rearmEvidence = new(StringComparer.OrdinalIgnoreCase);
        private readonly DeterministicExecutionSimulator simulator = new();
        private decimal cash;
        private decimal realized;
        private decimal totalFees;
        private long executionSequence;
        private int cursor;
        private DateOnly? unavailableDate;
        private readonly HashSet<string> unavailableReasons = new(StringComparer.Ordinal);
        private readonly TimeZoneInfo timezone;

        /// <summary>Freeze deterministic candidate priority and reject duplicate source bars before any cash changes.</summary>
        public Book(IntradayPortfolioReplayRequest request)
        {
            this.request = request;
            ValidateRequest(request);
            timezone = TimeZoneInfo.FindSystemTimeZoneById(request.Strategies[0].Session.ExchangeTimezone);
            strategies = request.Strategies.ToDictionary(x => x.StrategyId, StringComparer.Ordinal);
            bars = request.MarketBars.Where(x => x.Timeframe == "5m")
                .ToDictionary(x => (x.Ticker.ToUpperInvariant(), x.Timestamp));
            candidates = request.Candidates.OrderBy(x => x.EntryTimestamp).ThenByDescending(x => x.SelectionScore)
                .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.CandidateId, StringComparer.Ordinal).ToArray();
            cash = request.Portfolio.StartingCapital;
            for (var date = Date(request.StartUtc); date <= Date(request.EndUtc.AddTicks(-1)); date = date.AddDays(1))
                if (!request.Sessions.ContainsKey(date))
                    throw new InvalidDataException($"Intraday replay calendar missing {date:yyyy-MM-dd}.");
        }

        /// <summary>Complete the preceding interval before current admissions; today's unknown future cannot finance earlier entries.</summary>
        public IntradayPortfolioReplayOutcome Run(CancellationToken cancellationToken)
        {
            foreach (var session in request.Sessions.Values.Where(x => x.IsTradingDay).OrderBy(x => x.TradeDate))
            {
                var open = At(session.TradeDate, session.RegularOpen);
                var close = At(session.TradeDate, session.RegularClose);
                if (close <= request.StartUtc || open >= request.EndUtc) continue;
                for (var clock = open; clock <= close && clock <= request.EndUtc; clock = clock.AddMinutes(5))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock > open) CompleteInterval(clock.AddMinutes(-5), clock);
                    if (clock < close && clock >= request.StartUtc)
                    {
                        AdmitDue(clock, close.AddMinutes(-5));
                        if (clock == close.AddMinutes(-5)) LiquidateDeadline(clock);
                    }
                }
                ObserveSession(session.TradeDate);
            }
            while (cursor < candidates.Length) Reject(candidates[cursor++], "entry_outside_replayed_session");
            foreach (var intent in live.Where(x => !x.Failed).ToArray())
                Fail(intent, intent.Evidence.SessionDeadlineUtc, "execution_window_ends_with_unresolved_order_or_exposure");
            return new(trades.OrderBy(x => x.EntryTimestamp).ThenBy(x => x.CandidateId, StringComparer.Ordinal).ToArray(),
                failures, dispositions, events.OrderBy(x => x.TimestampUtc).ToArray(), BuildState())
                { FillEvents = fillEvents.OrderBy(x => x.Event.OccurredAtUtc).ToArray() };
        }

        /// <summary>Check policy boundaries once; unsupported session or cost settings must never silently change the experiment.</summary>
        private static void ValidateRequest(IntradayPortfolioReplayRequest request)
        {
            if (request.Strategies.Count == 0 || request.Portfolio.StartingCapital <= 0 ||
                request.Portfolio.MaxConcurrentPositions <= 0 || request.StartUtc >= request.EndUtc ||
                !request.Portfolio.PreventOverlappingTickerPositions || request.Portfolio.MaxBarParticipationPct is < 0 or > 100 ||
                request.Strategies.Any(x => x.Timeframe != "5m" || x.Execution.Timeframe != "5m" ||
                    x.Session.IsContinuousMarket || x.Session.UseExtendedHours) ||
                request.Candidates.Any(x => x.Direction is not ("long" or "short") || x.IntradayExecution is null ||
                    x.ExecutionReplay?.PolicyId != ExecutionReplayPolicy.ForEntryMode(x.IntradayExecution.EntryOrderMode)))
                throw new InvalidDataException("Intraday OHLC replay requires five-minute regular sessions, versioned entry evidence, positive capital/slots and overlapping-ticker prevention.");
            if (request.Candidates.Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count)
                throw new InvalidDataException("Intraday candidate identities must be unique.");
            if (request.Candidates.Any(x => !request.Strategies.Any(s => s.StrategyId == x.StrategyId)))
                throw new InvalidDataException("Every intraday candidate must identify a selected strategy.");
        }

        /// <summary>Dequeue due completed decisions in frozen priority order, reserving capital before inspecting their execution bar.</summary>
        private void AdmitDue(DateTimeOffset clock, DateTimeOffset deadline)
        {
            while (cursor < candidates.Length && candidates[cursor].EntryTimestamp <= clock)
                Admit(candidates[cursor++], clock, deadline);
        }

        /// <summary>Keep session/rearm checks, slot admission and shared risk sizing separate from market matching.</summary>
        private void Admit(BacktestCandidateTrade candidate, DateTimeOffset clock, DateTimeOffset deadline)
        {
            var evidence = candidate.IntradayExecution!;
            var marketEntry = evidence.EntryOrderMode is IntradayEntryOrderMode.StopMarket or IntradayEntryOrderMode.NextBarMarket;
            if (!ValidOrderClock(evidence, clock, deadline, marketEntry))
            { Reject(candidate, "invalid_or_stale_intraday_order_clock"); return; }
            if (marketEntry && !CanAdmitMarketParent(candidate, clock)) return;
            if (live.Any(x => x.Candidate.Ticker.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase)) ||
                !marketEntry && resolved.TryGetValue(candidate.Ticker, out var last) && evidence.BreakoutTimestampUtc < last)
            { Reject(candidate, "crossing_occurred_before_previous_order_resolved"); return; }
            if (!TryConsumeMarketSignal(candidate, clock)) return;
            if (live.Count >= request.Portfolio.MaxConcurrentPositions)
            { Reject(candidate, "max_concurrent_positions_reached"); return; }
            if (unavailableDate is not null)
            { Reject(candidate, "portfolio_economics_unavailable_after_missing_execution"); return; }
            var intent = PlanIntent(candidate);
            if (intent is null) return;
            ReserveIntent(intent, clock);
        }

        /// <summary>Validate completed intent before capital admission; legacy market signals also consume their inside-range rearm.</summary>
        private bool TryConsumeMarketSignal(BacktestCandidateTrade candidate, DateTimeOffset clock)
        {
            if (candidate.IntradayExecution!.EntryOrderMode != IntradayEntryOrderMode.NextBarMarket) return true;
            if (!ValidCompletedMarketSignal(candidate, clock))
            { Reject(candidate, "completed_market_signal_evidence_invalid"); return false; }
            resolved[candidate.Ticker] = clock;
            rangeReturns.Remove(candidate.Ticker);
            rearmEvidence[candidate.Ticker] = candidate.IntradayExecution;
            return true;
        }

        /// <summary>Size from completed evidence with the shared risk planner and frozen capital caps; no future execution price is inspected.</summary>
        private Intent? PlanIntent(BacktestCandidateTrade candidate)
        {
            var evidence = candidate.IntradayExecution!;
            var strategy = strategies[candidate.StrategyId];
            var plannedRisk = candidate.Direction == "long" ? evidence.LimitPrice - candidate.StopLossPrice
                : candidate.StopLossPrice - evidence.LimitPrice;
            if (strategy.ExitRules.HasProfitTarget && evidence.LimitPrice + (candidate.Direction == "long" ? 1 : -1) * 3 * plannedRisk <= 0)
            { Reject(candidate, "initial_three_r_target_not_positive"); return null; }
            var equity = Equity();
            var available = Math.Max(0, equity - live.Sum(ReservedNotional));
            var notional = Math.Min(equity / request.Portfolio.MaxConcurrentPositions,
                Math.Max(0, available - request.Portfolio.FixedBuyFee - request.Portfolio.FixedSellFee));
            var side = candidate.Direction == "long" ? PlannedOrderSide.Long : PlannedOrderSide.Short;
            var entryFee = side == PlannedOrderSide.Long ? request.Portfolio.FixedBuyFee : request.Portfolio.FixedSellFee;
            var exitFee = side == PlannedOrderSide.Long ? request.Portfolio.FixedSellFee : request.Portfolio.FixedBuyFee;
            var plan = new SharedOrderRiskPlanner().Plan(new(candidate.Ticker, side, equity,
                evidence.LimitPrice, PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, candidate.StopLossPrice),
                new(request.Portfolio.AccountRiskBudgetPct, request.Portfolio.MaxPositionNotionalPct),
                new(strategy.Execution.SlippageBps, entryFee, exitFee), notional));
            if (!plan.IsAccepted || plan.Order is null)
            { Reject(candidate, "order_plan:" + plan.Rejection?.Code); return null; }
            var maximumNotional = Math.Min(notional, equity * request.Portfolio.MaxPositionNotionalPct / 100);
            // A sell limit cannot fill below its limit even when the risk planner estimates adverse short slippage below it.
            var quantityAtLimit = (int)Math.Min(Int32.MaxValue, Math.Floor(maximumNotional / evidence.LimitPrice));
            var quantity = Math.Min(plan.Order.Quantity, quantityAtLimit);
            if (quantity <= 0) { Reject(candidate, "limit_price_exceeds_frozen_notional_cap"); return null; }
            return new Intent(candidate, strategy, quantity, maximumNotional);
        }

        /// <summary>Reserve a planned parent once and publish its explicit matching contract before any market execution.</summary>
        private void ReserveIntent(Intent intent, DateTimeOffset clock)
        {
            var candidate = intent.Candidate;
            var evidence = intent.Evidence;
            intents.Add(intent);
            live.Add(intent);
            var pendingReason = evidence.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket ? "pending_next_open_market_reserved"
                : intent.MarketEntry ? "pending_stop_market_reserved" : "pending_limit_reserved";
            dispositions.Add(new(candidate.CandidateId, true, pendingReason, intent.Requested));
            Emit(intent, clock, "accepted", candidate.Direction == "short"
                ? "diagnostic_historical_borrow_unproved;short_proceeds_excluded_from_buying_power" : pendingReason);
        }

        /// <summary>Validate the original one-bar contract separately from an opening-range order that persists until the deadline.</summary>
        private static bool ValidOrderClock(IntradayExecutionEvidence evidence, DateTimeOffset clock,
            DateTimeOffset deadline, bool marketEntry)
        {
            if (evidence.ActiveAtUtc != clock || evidence.SessionDeadlineUtc != deadline || clock >= deadline || evidence.TickSize <= 0 ||
                evidence.PreviousSessionHigh is null || evidence.PreviousSessionLow is null)
                return false;
            if (!marketEntry) return evidence.ExpiresAtUtc == clock.AddMinutes(5) &&
                evidence.ConfirmationTimestampUtc.AddMinutes(5) == clock &&
                evidence.BreakoutTimestampUtc.AddMinutes(5) == evidence.ConfirmationTimestampUtc;
            if (evidence.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket &&
                (evidence.ConfirmationTimestampUtc.AddMinutes(5) != clock ||
                 evidence.BreakoutTimestampUtc != evidence.ConfirmationTimestampUtc ||
                 evidence.OpeningRangeCompletedAtUtc is not { } rangeEnd || evidence.BreakoutTimestampUtc < rangeEnd)) return false;
            return evidence.ExpiresAtUtc == deadline && evidence.OpeningRangeCompletedAtUtc is { } completed && completed <= clock &&
                evidence.OpeningRangeHigh is { } high && evidence.OpeningRangeLow is { } low && high > low &&
                evidence.MaxFilledParentTradesPerSession > 0;
        }

        /// <summary>Check the completed source close and rounded stop again at the execution boundary before committing cash.</summary>
        private bool ValidCompletedMarketSignal(BacktestCandidateTrade candidate, DateTimeOffset clock)
        {
            var e = candidate.IntradayExecution!;
            if (!bars.TryGetValue((candidate.Ticker.ToUpperInvariant(), e.ConfirmationTimestampUtc), out var bar) ||
                !ValidBar(bar) || bar.KnownAtUtc > clock || e.BreakoutTriggerPrice is not { } trigger ||
                e.LimitPrice != bar.Close || e.TickSize <= 0m) return false;
            var isLong = candidate.Direction == "long";
            var rawTrigger = isLong ? e.OpeningRangeHigh!.Value + e.TickSize : e.OpeningRangeLow!.Value - e.TickSize;
            var expectedTrigger = (isLong ? decimal.Ceiling(rawTrigger / e.TickSize) : decimal.Floor(rawTrigger / e.TickSize)) * e.TickSize;
            if (trigger != expectedTrigger) return false;
            var stop = (isLong ? decimal.Floor(bar.Low / e.TickSize) : decimal.Ceiling(bar.High / e.TickSize)) * e.TickSize;
            return candidate.StopLossPrice == stop && (isLong ? bar.Close > trigger : bar.Close < trigger);
        }

        /// <summary>Count filled parents once and require a completed return after resolution before a fresh stop may be routed.</summary>
        private bool CanAdmitMarketParent(BacktestCandidateTrade candidate, DateTimeOffset clock)
        {
            var e = candidate.IntradayExecution!;
            var count = filledParents.GetValueOrDefault((candidate.Ticker.ToUpperInvariant(), Date(clock)));
            if (count >= e.MaxFilledParentTradesPerSession)
            { Reject(candidate, "filled_parent_session_limit_reached"); return false; }
            var resolvedMarketSignal = e.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket &&
                resolved.TryGetValue(candidate.Ticker, out var priorResolution) && Date(priorResolution) == Date(clock);
            if ((count > 0 || resolvedMarketSignal) && e.RequireRangeReturnBeforeReentry &&
                (!rangeReturns.TryGetValue(candidate.Ticker, out var returned) || Date(returned) != Date(clock) ||
                 returned > clock || e.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket && e.BreakoutTimestampUtc < returned))
            { Reject(candidate, "completed_return_inside_opening_range_required"); return false; }
            return true;
        }

        /// <summary>Use marked equity and gross reserved exposure, so short-sale cash cannot become fresh buying power.</summary>
        private decimal Equity() => cash + live.Sum(x => x.Open * (x.Long ? 1m : -1m) * (x.Mark ?? x.Entry));

        /// <summary>Reserve all admitted parent shares at their fixed reference, including still-unfilled quantities.</summary>
        private static decimal ReservedNotional(Intent intent) =>
            (intent.Pending ? intent.Requested : intent.Open) * Math.Max(intent.Evidence.LimitPrice, intent.Entry);

        /// <summary>
        /// Resolve only active work against its exact interval; missing bars preserve unknown exposure instead of shifting
        /// fills. A confirmed quiet interval had no price-setting trade, so nothing can fill, trigger or exit in it.
        /// </summary>
        private void CompleteInterval(DateTimeOffset start, DateTimeOffset end)
        {
            foreach (var intent in live.Where(x => !x.Failed && x.Evidence.ActiveAtUtc <= start && start < x.Evidence.SessionDeadlineUtc).ToArray())
            {
                var symbol = intent.Candidate.Ticker.ToUpperInvariant();
                if (!bars.TryGetValue((symbol, start), out var bar))
                {
                    if (request.ConfirmedQuietIntervals?.GetValueOrDefault(symbol)?.Contains(start) == true) PassQuietInterval(intent, start, end);
                    else Fail(intent, start, "required_five_minute_execution_bar_missing");
                    continue;
                }
                if (!ValidBar(bar) || bar.KnownAtUtc > end)
                { Fail(intent, start, "execution_bar_invalid_or_unavailable_at_completion"); continue; }
                ProcessInterval(intent, bar, end);
            }
            ObserveRangeReturns(start, end);
        }

        /// <summary>Observe later completed rearm closes; historical bars without provider receipt stamps use their interval completion, while explicit future stamps remain ineligible.</summary>
        private void ObserveRangeReturns(DateTimeOffset start, DateTimeOffset end)
        {
            foreach (var pair in rearmEvidence)
            {
                if (!resolved.TryGetValue(pair.Key, out var resolution) || end <= resolution || start < resolution ||
                    Date(resolution) != Date(end) || live.Any(x => x.Candidate.Ticker.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))) continue;
                if (bars.TryGetValue((pair.Key.ToUpperInvariant(), start), out var bar) && ValidBar(bar) &&
                    (bar.KnownAtUtc is null || bar.KnownAtUtc <= end) &&
                    bar.Close >= pair.Value.OpeningRangeLow && bar.Close <= pair.Value.OpeningRangeHigh)
                    rangeReturns[pair.Key] = end;
            }
        }

        /// <summary>Existing protection gets first claim; newly filled parents receive protection within this same ambiguous OHLC interval.</summary>
        private void ProcessInterval(Intent intent, OhlcvBar bar, DateTimeOffset end)
        {
            if (intent.MarketEntry)
            {
                ProcessMarketEntryInterval(intent, bar, end);
                FinishInterval(intent, bar, end);
                return;
            }
            var used = 0;
            if (intent.Open > 0) used = Protect(intent, bar, end, 0);
            if (intent.Failed) return;
            if (intent.Open == 0 && !intent.Pending) { Resolve(intent, end); return; }
            if (intent.Pending && !intent.StopTriggered)
            {
                used += FillEntry(intent, bar, used);
                if (intent.Failed) return;
                if (intent.Open > 0) Protect(intent, bar, end, used, intent.EntryAt == bar.Timestamp);
            }
            if (intent.Pending && (end >= intent.Evidence.ExpiresAtUtc || InsidePreviousRange(intent, bar.Close)))
                CancelRemainder(intent, end, InsidePreviousRange(intent, bar.Close) ? "completed_close_back_inside_previous_range" : "one_bar_entry_expiry");
            FinishInterval(intent, bar, end);
        }

        /// <summary>
        /// A confirmed quiet interval had no price-setting trade: a resting stop or limit cannot trigger or fill in it.
        /// Market work due in it (a next-bar market entry, a triggered entry remainder or a triggered stop exit) would have
        /// traded at an unknown price, so the intent fails rather than filling later. A one-bar limit still expires.
        /// </summary>
        private void PassQuietInterval(Intent intent, DateTimeOffset start, DateTimeOffset end)
        {
            var marketDue = intent.Pending && (intent.EntryTriggered || intent.NextBarMarket && intent.Evidence.ActiveAtUtc == start) ||
                intent.Open > 0 && intent.StopTriggered;
            if (marketDue) { Fail(intent, start, "market_order_due_in_interval_without_price_setting_trade"); return; }
            if (intent.Pending && end >= intent.Evidence.ExpiresAtUtc) CancelRemainder(intent, end, "one_bar_entry_expiry");
            if (intent.Open == 0 && !intent.Pending) Resolve(intent, end);
        }

        /// <summary>Resolve known opening protection, then a triggered market remainder, before unknown later OHLC extremes.</summary>
        private void ProcessMarketEntryInterval(Intent intent, OhlcvBar bar, DateTimeOffset end)
        {
            var opening = bar with { High = bar.Open, Low = bar.Open, Close = bar.Open };
            var used = intent.Open > 0 ? Protect(intent, opening, end, 0, openingOnly: true) : 0;
            if (intent.Failed || intent.Open == 0 && !intent.Pending) return;
            if (intent.Pending && !intent.StopTriggered)
                used += intent.NextBarMarket ? FillNextBarMarketEntry(intent, bar, used) : FillStopMarketEntry(intent, bar, used);
            if (intent.Failed) return;
            if (intent.Open > 0) Protect(intent, bar, end, used, intent.LastFillAt <= bar.Timestamp);
        }

        /// <summary>Mark and advance completed-close management only after all causal execution phases have finished.</summary>
        private void FinishInterval(Intent intent, OhlcvBar bar, DateTimeOffset end)
        {
            if (intent.Failed) return;
            intent.Mark = bar.Close;
            intent.MarkAt = end;
            if (intent.Open == 0 && !intent.Pending) { Resolve(intent, end); return; }
            UpdateProtection(intent, bar, end);
        }

        /// <summary>Require a marketable opening or one-tick penetration before delegating partial quantity, adverse prices and costs.</summary>
        private int FillEntry(Intent intent, OhlcvBar bar, int used)
        {
            var e = intent.Evidence;
            var qualifies = intent.Long ? bar.Open <= e.LimitPrice || bar.Low <= e.LimitPrice - e.TickSize
                : bar.Open >= e.LimitPrice || bar.High >= e.LimitPrice + e.TickSize;
            if (!qualifies) { Emit(intent, bar.Timestamp.AddMinutes(5), "no_fill", "limit_touch_without_one_tick_penetration"); return 0; }
            var fill = Simulate(intent, bar, intent.Requested - intent.Entered, intent.Long,
                SimulatedOrderType.Limit, used, e.LimitPrice, null, true);
            if (fill.FilledQuantity == 0) return 0;
            var openingEntry = intent.Long ? bar.Open <= e.LimitPrice : bar.Open >= e.LimitPrice;
            Account(intent, fill, true, openingEntry ? bar.Timestamp : bar.Timestamp.AddMinutes(5), "entry_limit",
                bar.Timestamp, openingEntry ? "opening_entry" : "completed_interval_entry");
            InitializeFilledProtection(intent, openingEntry, openingEntry ? bar.Timestamp : bar.Timestamp.AddMinutes(5));
            return fill.FilledQuantity;
        }

        /// <summary>Execute the committed parent at this observed opening; partial shares retain their original market intent until protection or deadline.</summary>
        private int FillNextBarMarketEntry(Intent intent, OhlcvBar bar, int used)
        {
            var fill = Simulate(intent, bar, intent.Requested - intent.Entered, intent.Long,
                SimulatedOrderType.Market, used, null, null, true, true);
            if (fill.FilledQuantity == 0) return 0;
            Account(intent, fill, true, bar.Timestamp, "entry_next_open_market", bar.Timestamp, "opening_entry_lagged_liquidity");
            InitializeFilledProtection(intent, true, bar.Timestamp);
            return fill.FilledQuantity;
        }

        /// <summary>Trigger once, then retain a market remainder; opening fills use only the preceding completed liquidity observation.</summary>
        private int FillStopMarketEntry(Intent intent, OhlcvBar bar, int used)
        {
            var trigger = intent.Evidence.LimitPrice;
            var opening = intent.EntryTriggered || (intent.Long ? bar.Open >= trigger : bar.Open <= trigger);
            if (!intent.EntryTriggered && !(intent.Long ? bar.High >= trigger : bar.Low <= trigger)) return 0;
            if (!intent.EntryTriggered)
            {
                intent.EntryTriggered = true;
                Emit(intent, opening ? bar.Timestamp : bar.Timestamp.AddMinutes(5), "entry_triggered", "stop_market_trigger_retained_until_fill_or_cancellation");
            }
            var fill = Simulate(intent, bar, intent.Requested - intent.Entered, intent.Long,
                opening ? SimulatedOrderType.Market : SimulatedOrderType.Stop, used, null, opening ? null : trigger, true, opening);
            if (fill.FilledQuantity == 0) return 0;
            var at = opening ? bar.Timestamp : bar.Timestamp.AddMinutes(5);
            intent.EntryBarStart ??= bar.Timestamp;
            Account(intent, fill, true, at, "entry_stop_market", bar.Timestamp,
                opening ? "opening_entry_lagged_liquidity" : "completed_interval_entry");
            InitializeFilledProtection(intent, opening, at);
            return fill.FilledQuantity;
        }

        /// <summary>Rebase original risk on actual weighted fills while retaining the structural stop and submitted parent quantity.</summary>
        private void InitializeFilledProtection(Intent intent, bool openingEntry, DateTimeOffset at)
        {
            var e = intent.Evidence;
            if (intent.Entered == intent.Requested) intent.Pending = false;
            var risk = intent.Long ? intent.Entry - intent.Candidate.StopLossPrice : intent.Candidate.StopLossPrice - intent.Entry;
            if (risk <= 0)
            {
                intent.StopTriggered = true;
                intent.StopTriggeredAt = at;
                intent.ExitReason = "gap_fill_through_structural_stop";
                CancelRemainder(intent, at, "gap_fill_through_structural_stop");
            }
            else if (!intent.Strategy.ExitRules.HasProfitTarget)
            {
                // The published recipe has no target: the position holds until its stop or the session deadline.
                intent.InitialTarget = 0;
            }
            else
            {
                var rawTarget = intent.Entry + (intent.Long ? 1 : -1) * 3 * risk;
                intent.InitialTarget = (intent.Long ? Math.Ceiling(rawTarget / e.TickSize) : Math.Floor(rawTarget / e.TickSize)) * e.TickSize;
                if (intent.InitialTarget <= 0)
                {
                    intent.InitialTarget = 0;
                    intent.StopTriggered = true;
                    intent.StopTriggeredAt = at;
                    intent.ExitReason = "actual_fill_three_r_target_not_positive";
                    CancelRemainder(intent, at, intent.ExitReason);
                    Emit(intent, at, "risk_reduction_pending", intent.ExitReason, intent.Open);
                }
                var rules = intent.Strategy.ExitRules.ContinuationProtection;
                if (rules is not null && intent.InitialTarget > 0)
                    intent.Protection = intent.Protection is { } existing
                        ? ContinuationProtectionPolicy.RebaseActualFills(existing, intent.Entry, EstimatedCosts(intent))
                        : ContinuationProtectionPolicy.Create(intent.Candidate.Ticker,
                            intent.Long ? PlannedOrderSide.Long : PlannedOrderSide.Short,
                            intent.EntryAt!.Value, intent.Entry, intent.Candidate.StopLossPrice,
                            EstimatedCosts(intent), e.TickSize, rules);
                if (intent.Protection is { TargetPrice: <= 0 })
                {
                    intent.StopTriggered = true;
                    intent.StopTriggeredAt = at;
                    intent.ExitReason = "actual_fill_management_target_not_positive";
                    CancelRemainder(intent, at, intent.ExitReason);
                    Emit(intent, at, "risk_reduction_pending", intent.ExitReason, intent.Open);
                }
                if (intent.EntryNotional > intent.MaximumNotional + 0.000001m)
                {
                    intent.StopTriggered = true;
                    intent.StopTriggeredAt = at;
                    intent.ExitReason = "actual_fill_notional_limit_breach";
                    CancelRemainder(intent, at, "actual_fill_notional_limit_breach");
                    if (!openingEntry)
                        Emit(intent, at, "risk_reduction_pending",
                            "intrabar_notional_risk_waits_for_next_observed_opening;original_structural_stop_remains_active", intent.Open);
                }
            }
            Emit(intent, at, "protection_initialized", risk <= 0 ? "gap_fill_has_no_positive_r_target"
                : !intent.Strategy.ExitRules.HasProfitTarget ? "actual_fill_stop_only_protection"
                : intent.InitialTarget > 0 ? "actual_fill_three_r_initial_protection" : "actual_fill_three_r_target_not_positive",
                intent.Entered, intent.Entry);
        }

        /// <summary>Choose adverse protection within the requested phase; an opening-only pass cannot consume later newly effective levels.</summary>
        private int Protect(Intent intent, OhlcvBar bar, DateTimeOffset end, int used, bool allowTarget = true,
            bool openingOnly = false)
        {
            var (stop, target, price, opening) = ProtectionTrigger(intent, bar, allowTarget, openingOnly);
            if (!stop && !target) return 0;
            // A risk reduction first discovered at this interval's completed entry cannot consume its preceding opening.
            var alreadyTriggered = intent.StopTriggered && (intent.StopTriggeredAt ?? intent.EntryAt) <= bar.Timestamp;
            if (stop && !intent.StopTriggered) intent.StopTriggeredAt = opening ? bar.Timestamp : end;
            intent.StopTriggered |= stop;
            if (stop && intent.ExitReason is not ("gap_fill_through_structural_stop" or "actual_fill_notional_limit_breach" or
                "actual_fill_three_r_target_not_positive" or "actual_fill_management_target_not_positive"))
                // Without a target the stop is tight (a fraction of ATR); a stop inside the five-minute bar that filled the
                // entry is counted as hit (worst case) and labelled, because the order of the two within the bar is unknown.
                intent.ExitReason = !intent.Strategy.ExitRules.HasProfitTarget && intent.EntryBarStart == bar.Timestamp
                    ? "stop_loss_entry_bar_order_unknown" : "stop_loss";
            else if (target) intent.ExitReason = "take_profit";
            CancelRemainder(intent, opening ? bar.Timestamp : end,
                stop ? "protective_stop_cancels_entry_remainder" : "protective_target_cancels_entry_remainder");
            var type = stop ? alreadyTriggered ? SimulatedOrderType.Market : SimulatedOrderType.Stop : SimulatedOrderType.Limit;
            var fill = Simulate(intent, bar, intent.Open, !intent.Long, type, used,
                target ? price : null, stop && !alreadyTriggered ? price : null, false, intent.MarketEntry && opening);
            if (fill.FilledQuantity > 0) Account(intent, fill, false, opening ? bar.Timestamp : end, intent.ExitReason!,
                bar.Timestamp, opening ? "opening_protection" : "completed_interval_protection");
            if (!openingOnly) ActivateResidualStop(intent, bar, end, target && opening);
            return fill.FilledQuantity;
        }

        /// <summary>An opening target can consume all capacity; a later observed stop still activates market protection on the remaining shares.</summary>
        private void ActivateResidualStop(Intent intent, OhlcvBar bar, DateTimeOffset end, bool openingTarget)
        {
            if (!openingTarget || intent.Open <= 0 || !(intent.Long ? bar.Low <= intent.Stop : bar.High >= intent.Stop)) return;
            intent.StopTriggered = true;
            intent.StopTriggeredAt = end;
            intent.ExitReason = "stop_loss";
            Emit(intent, end, "stop_triggered", "later_stop_after_partial_opening_target_capacity_exhausted", intent.Open);
        }

        /// <summary>Known opening protection precedes ambiguous later extremes; a just-decided update cannot rewrite that exact opening.</summary>
        private static (bool Stop, bool Target, decimal Price, bool Opening) ProtectionTrigger(
            Intent intent, OhlcvBar bar, bool allowTarget, bool openingOnly)
        {
            if (intent.StopTriggered)
            {
                if ((intent.StopTriggeredAt ?? intent.EntryAt) > bar.Timestamp)
                    return !openingOnly && (intent.Long ? bar.Low <= intent.Candidate.StopLossPrice : bar.High >= intent.Candidate.StopLossPrice)
                        ? (true, false, intent.Candidate.StopLossPrice, false) : (false, false, 0, false);
                return (true, false, intent.Stop, true);
            }
            var boundary = intent.ProtectionEffectiveAfterUtc == bar.Timestamp;
            var openingStop = boundary ? intent.StopBeforeUpdate : intent.Stop;
            var openingTarget = boundary ? intent.TargetBeforeUpdate : intent.Target;
            var canUseOpening = intent.LastFillAt <= bar.Timestamp;
            if (canUseOpening && (intent.Long ? bar.Open <= openingStop : bar.Open >= openingStop))
                return (true, false, openingStop, true);
            if (canUseOpening && allowTarget && openingTarget > 0 && (intent.Long ? bar.Open >= openingTarget : bar.Open <= openingTarget))
                return (false, true, openingTarget, true);
            if (openingOnly) return (false, false, 0, false);
            if (intent.Long ? bar.Low <= intent.Stop : bar.High >= intent.Stop)
                return (true, false, intent.Stop, false);
            return allowTarget && intent.Target > 0 && (intent.Long ? bar.High >= intent.Target : bar.Low <= intent.Target)
                ? (false, true, intent.Target, false) : (false, false, 0, false);
        }

        /// <summary>Reuse the shared simulator while sharing one observed participation pool across both sides of a parent in a bar.</summary>
        private ExecutionSimulationResult Simulate(Intent intent, OhlcvBar bar, int quantity, bool buy,
            SimulatedOrderType type, int used, decimal? limit, decimal? stop, bool entry, bool openingOnly = false)
        {
            if (openingOnly)
            {
                if (request.Portfolio.MaxBarParticipationPct > 0 &&
                    (!bars.TryGetValue((intent.Candidate.Ticker.ToUpperInvariant(), bar.Timestamp.AddMinutes(-5)), out var prior) ||
                     !ValidBar(prior) || prior.KnownAtUtc > bar.Timestamp))
                {
                    Fail(intent, bar.Timestamp, "opening_execution_lagged_liquidity_evidence_missing");
                    return new(SimulatedFillStatus.NoFill, quantity, 0, quantity, null, 0, 0, 0, 0, "lagged_liquidity_missing");
                }
                var volume = bars.GetValueOrDefault((intent.Candidate.Ticker.ToUpperInvariant(), bar.Timestamp.AddMinutes(-5)))?.Volume ?? 0;
                bar = bar with { High = bar.Open, Low = bar.Open, Close = bar.Open, Volume = volume };
            }
            var capacity = DeterministicExecutionSimulator.CalculateFillableQuantity(Int32.MaxValue, bar.Volume,
                request.Portfolio.MaxBarParticipationPct, true);
            quantity = Math.Min(quantity, Math.Max(0, capacity - used));
            if (quantity == 0) return new(SimulatedFillStatus.NoFill, 0, 0, 0, null, 0, 0, 0, 0, "participation_capacity_exhausted");
            var feesCharged = entry ? intent.Entered > 0 : intent.Exited > 0;
            var tafCharged = entry ? intent.EntryTaf : intent.ExitTaf;
            var remainingTaf = request.Portfolio.FinraTafCap > 0 ? Math.Max(0, request.Portfolio.FinraTafCap - tafCharged) : 0;
            var policy = new ExecutionSimulationPolicy(request.Portfolio.MaxBarParticipationPct,
                intent.Strategy.Execution.SlippageBps, 0m, request.Portfolio.ImpactBpsAtMaxParticipation,
                feesCharged ? 0 : request.Portfolio.FixedBuyFee, feesCharged ? 0 : request.Portfolio.FixedSellFee,
                request.Portfolio.SecFeeRate, request.Portfolio.FinraTafCap > 0 && remainingTaf == 0 ? 0 : request.Portfolio.FinraTafPerShare,
                remainingTaf);
            return simulator.Simulate(new(buy ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell, type, quantity,
                new(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume), policy, limit, stop));
        }

        /// <summary>Apply actual fill cash, directional accounting and per-side fixed/regulatory fees once.</summary>
        private void Account(Intent intent, ExecutionSimulationResult fill, bool entry, DateTimeOffset at, string reason,
            DateTimeOffset sourceIntervalStartUtc, string executionPhase)
        {
            if (intent.LastFillAt is { } last && at < last || !entry && (intent.EntryAt is not { } first || at < first))
                throw new InvalidDataException("An actual fill cannot precede its parent entry or a previously observed fill; clocks must never be clamped.");
            intent.LastFillAt = at;
            var sequence = checked(++executionSequence);
            var price = fill.AverageFillPrice!.Value;
            var quantity = fill.FilledQuantity;
            var buy = entry ? intent.Long : !intent.Long;
            cash += (buy ? -1 : 1) * quantity * price - fill.Fees;
            intent.Fees += fill.Fees;
            totalFees += fill.Fees;
            realized -= fill.Fees;
            var taf = buy ? 0 : Math.Min(quantity * request.Portfolio.FinraTafPerShare,
                request.Portfolio.FinraTafCap > 0 ? Math.Max(0, request.Portfolio.FinraTafCap - (entry ? intent.EntryTaf : intent.ExitTaf)) : Decimal.MaxValue);
            if (entry)
            {
                if (intent.MarketEntry && intent.Entered == 0)
                {
                    var key = (intent.Candidate.Ticker.ToUpperInvariant(), Date(at));
                    filledParents[key] = filledParents.GetValueOrDefault(key) + 1;
                }
                intent.Entered += quantity;
                intent.LastEntryFillPrice = price;
                intent.EntryNotional += quantity * price;
                intent.EntryAt ??= at;
                intent.EntryTaf += taf;
            }
            else
            {
                realized += (intent.Long ? 1 : -1) * quantity * (price - intent.Entry);
                intent.Exited += quantity;
                intent.ExitNotional += quantity * price;
                intent.ExitAt = at;
                intent.ExitTaf += taf;
            }
            fills.Add(new(intent.Candidate.CandidateId, intent.Candidate.StrategyId, intent.Candidate.Ticker,
                buy, entry, quantity, price, fill.Fees, at, Date(at)));
            var remaining = entry ? intent.Requested - intent.Entered : intent.Open;
            var suffix = entry ? "entry" : reason == "take_profit" ? "target" : reason.Contains("stop", StringComparison.Ordinal) ? "stop" : "session-exit";
            var orderId = intent.Candidate.CandidateId + ":" + suffix;
            var cumulative = intent.OrderFilledQuantities.GetValueOrDefault(orderId) + quantity;
            intent.OrderFilledQuantities[orderId] = cumulative;
            fillEvents.Add(new(intent.Candidate.CandidateId, new(orderId,
                intent.Candidate.Ticker, remaining == 0 ? "filled" : "partially_filled", quantity,
                cumulative, remaining, price, fill.Fees, fill.SpreadBps,
                fill.SlippageBps, fill.ImpactBps, (intent.Long ? 1 : -1) * intent.Open,
                (intent.Long ? 1 : -1) * (intent.ExitNotional - intent.Exited * intent.Entry) - intent.Fees,
                at, reason, buy ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell, !entry))
                { SourceIntervalStartUtc = sourceIntervalStartUtc, ExecutionPhase = executionPhase, ExecutionSequence = sequence });
            events.Add(new(intent.Candidate.CandidateId, intent.Candidate.Ticker, at, "fill",
                reason + (executionPhase.Contains("lagged_liquidity", StringComparison.Ordinal) || intent.MarketEntry && executionPhase == "opening_protection"
                    ? ";diagnostic_lagged_completed_bar_volume_capacity;no_current_bar_volume_used"
                    : ";retrospective_bar_participation_assumption"), quantity, price, intent.Stop, intent.Target)
                { SourceIntervalStartUtc = sourceIntervalStartUtc, ExecutionPhase = executionPhase, ExecutionSequence = sequence });
        }

        /// <summary>Estimate both-side slippage and actual/frozen fees for the approved cost-covered stop offset.</summary>
        private decimal EstimatedCosts(Intent intent)
        {
            var p = request.Portfolio;
            return intent.Entry * (intent.Strategy.Execution.SlippageBps + p.ImpactBpsAtMaxParticipation) * 2 / 10000m +
                (p.FixedBuyFee + p.FixedSellFee) / intent.Entered +
                DeterministicExecutionSimulator.CalculateRegulatoryFees(intent.EntryNotional, intent.Entered,
                    p.SecFeeRate, p.FinraTafPerShare, p.FinraTafCap) / intent.Entered;
        }

        /// <summary>Apply paired changes after existing fills; the source bar can never execute a newly calculated stop or target.</summary>
        private void UpdateProtection(Intent intent, OhlcvBar bar, DateTimeOffset end)
        {
            if (intent.Open <= 0 || intent.StopTriggered || intent.Protection is null) return;
            var update = ContinuationProtectionPolicy.Evaluate(intent.Protection, bar, end, true, end);
            if (update.LevelsChanged)
            {
                intent.StopBeforeUpdate = intent.Stop;
                intent.TargetBeforeUpdate = intent.Target;
                intent.ProtectionEffectiveAfterUtc = update.EffectiveAfterUtc;
            }
            intent.Protection = update.State;
            if (update.LevelsChanged)
                events.Add(new(intent.Candidate.CandidateId, intent.Candidate.Ticker, end, "protection_updated", update.Reason,
                    intent.Open, null, update.State.StopPrice, update.State.TargetPrice, update.EffectiveAfterUtc));
        }

        /// <summary>At the final opening, inspect only its observed opening price; capped liquidity cannot be inferred from future bar volume.</summary>
        private void LiquidateDeadline(DateTimeOffset clock)
        {
            foreach (var intent in live.Where(x => !x.Failed && x.Evidence.SessionDeadlineUtc == clock).ToArray())
            {
                CancelRemainder(intent, clock, "session_deadline");
                if (intent.Open == 0) { Resolve(intent, clock); continue; }
                if (!bars.TryGetValue((intent.Candidate.Ticker.ToUpperInvariant(), clock), out var bar) || bar.Open <= 0)
                { Fail(intent, clock, "session_deadline_opening_price_missing"); continue; }
                OhlcvBar? prior = null;
                if (request.Portfolio.MaxBarParticipationPct > 0 &&
                    (!bars.TryGetValue((intent.Candidate.Ticker.ToUpperInvariant(), clock.AddMinutes(-5)), out prior) ||
                     !ValidBar(prior) || prior.KnownAtUtc > clock))
                { Fail(intent, clock, "session_deadline_lagged_liquidity_evidence_missing"); continue; }
                var opening = bar with { High = bar.Open, Low = bar.Open, Close = bar.Open, Volume = prior?.Volume ?? 0 };
                var stopPrice = intent.ProtectionEffectiveAfterUtc == clock ? intent.StopBeforeUpdate : intent.Stop;
                var targetPrice = intent.ProtectionEffectiveAfterUtc == clock ? intent.TargetBeforeUpdate : intent.Target;
                var stop = intent.StopTriggered || (intent.Long ? bar.Open <= stopPrice : bar.Open >= stopPrice);
                var target = !stop && targetPrice > 0 && (intent.Long ? bar.Open >= targetPrice : bar.Open <= targetPrice);
                var fill = Simulate(intent, opening, intent.Open, !intent.Long,
                    target ? SimulatedOrderType.Limit : SimulatedOrderType.Market, 0, target ? targetPrice : null, null, false);
                intent.ExitReason = stop ? "stop_loss" : target ? "take_profit" : "session_deadline";
                if (fill.FilledQuantity > 0)
                    Account(intent, fill, false, clock, intent.ExitReason + (request.Portfolio.MaxBarParticipationPct > 0
                        ? ";diagnostic_lagged_completed_bar_volume_capacity;no_final_bar_volume_used"
                        : ";unlimited_opening_liquidity_assumption"), clock, "scheduled_opening_liquidation");
                if (intent.Open > 0) Fail(intent, clock, "session_deadline_partial_liquidation_unresolved");
                else Resolve(intent, clock);
            }
        }

        /// <summary>Cancel only unfilled shares; actual entry fills remain protected and remain in the retained ledger.</summary>
        private void CancelRemainder(Intent intent, DateTimeOffset at, string reason)
        {
            if (!intent.Pending) return;
            intent.Pending = false;
            Emit(intent, at, "entry_remainder_canceled", reason, intent.Requested - intent.Entered);
        }

        /// <summary>Retire a resolved intent and record directional PnL without adding short proceeds to economic equity.</summary>
        private void Resolve(Intent intent, DateTimeOffset at)
        {
            if (intent.Pending || intent.Open > 0) return;
            live.Remove(intent);
            resolved[intent.Candidate.Ticker] = intent.ExitAt ?? at;
            if ((intent.StopMarket && intent.Entered > 0 || intent.NextBarMarket))
            {
                rangeReturns.Remove(intent.Candidate.Ticker);
                rearmEvidence[intent.Candidate.Ticker] = intent.Evidence;
            }
            if (intent.Entered == 0) { Emit(intent, at, "resolved", "expired_without_fill"); return; }
            var entryPrice = Decimal.Round(intent.Entry, 6);
            var exitPrice = Decimal.Round(intent.ExitNotional / intent.Exited, 6);
            var gross = Decimal.Round((intent.Long ? 1 : -1) * (exitPrice - entryPrice) * intent.Entered, 4);
            var fees = Decimal.Round(intent.Fees, 4);
            var trade = new BacktestTrade(intent.Candidate.Ticker, intent.Candidate.StrategyName, intent.Candidate.Direction,
                intent.EntryAt!.Value, intent.ExitAt!.Value, intent.Entered, entryPrice, exitPrice,
                intent.Candidate.StopLossPrice, intent.InitialTarget, intent.ExitReason!, gross, fees, Decimal.Round(gross - fees, 4),
                intent.Requested, intent.Entered < intent.Requested ? "partial_fill_remainder_canceled" : "filled",
                StrategyId: intent.Candidate.StrategyId, CandidateId: intent.Candidate.CandidateId);
            trades.Add(trade);
        }

        /// <summary>Preserve the first unknown execution boundary and actual partial fills; never manufacture a flat completed trade.</summary>
        private void Fail(Intent intent, DateTimeOffset at, string reason)
        {
            intent.Failed = true;
            var date = Date(at);
            unavailableDate = unavailableDate is { } old && old < date ? old : date;
            unavailableReasons.Add(reason);
            failures.Add(new(intent.Candidate.CandidateId, intent.Candidate.Ticker, intent.Candidate.StrategyName,
                intent.Candidate.StrategyId, intent.Candidate.Direction, intent.Candidate.EntryTimestamp, reason,
                intent.Requested, intent.Entered, intent.Exited, (intent.Long ? 1 : -1) * intent.Open,
                intent.EntryNotional, intent.ExitNotional, intent.Fees, intent.Mark, intent.MarkAt, true));
            Emit(intent, at, "unresolved", reason, intent.Open);
        }

        /// <summary>Observe every requested calendar session, including no-trade days; unknown exposure suppresses economics from that date.</summary>
        private void ObserveSession(DateOnly date)
        {
            var unknown = unavailableDate is { } failed && date >= failed;
            var positions = live.Where(x => x.Open > 0).ToArray();
            foreach (var p in positions)
            {
                var current = p.MarkAt is { } at && Date(at) == date;
                marks.Add(new(date, p.Candidate.CandidateId, p.Candidate.Ticker, (p.Long ? 1 : -1) * p.Open,
                    p.Entry, current ? p.Mark : null, current ? p.MarkAt?.AddMinutes(-5) : null));
            }
            var equity = Equity();
            var positionValue = positions.Sum(x => (x.Long ? 1 : -1) * x.Open * (x.Mark ?? x.Entry));
            var unrealized = positions.Sum(x => (x.Long ? 1 : -1) * x.Open * ((x.Mark ?? x.Entry) - x.Entry));
            var exposure = positions.Sum(x => x.Open * (x.Mark ?? x.Entry));
            observations.Add(new(date, cash, realized, totalFees, unknown ? null : positionValue,
                unknown ? null : unrealized, unknown ? null : equity, unknown ? null : equity <= 0 ? 0 : exposure / equity * 100,
                positions.Length, unknown ? String.Join(';', unavailableReasons.Order()) : null));
        }

        /// <summary>Publish the standard portfolio contract used by monthly attribution and terminal-exposure artifacts.</summary>
        private BacktestPortfolioState BuildState() => new(ValuationBasis, request.EndUtc,
            intents.Count, intents.Count(x => x.Entered > 0), trades.Count, fills.OrderBy(x => x.TimestampUtc).ToArray(),
            live.Where(x => x.Open > 0).Select(x => new BacktestOpenPosition(x.Candidate.CandidateId, x.Candidate.StrategyId,
                x.Candidate.Ticker, (x.Long ? 1 : -1) * x.Open, x.Entry,
                x.MarkAt is { } at && Date(at) == observations[^1].SessionDate ? x.Mark : null,
                x.MarkAt is { } at2 && Date(at2) == observations[^1].SessionDate ? x.MarkAt : null,
                x.MarkAt is { } at3 && Date(at3) == observations[^1].SessionDate && x.Mark is { } mark
                    ? (x.Long ? 1 : -1) * x.Open * (mark - x.Entry) : null)).ToArray(),
            live.Where(x => x.Pending).Select(x => new BacktestPendingOrder(x.Candidate.CandidateId, x.Candidate.StrategyId,
                x.Candidate.Ticker, x.Candidate.CandidateId + ":entry", x.Long ? "buy" : "sell",
                x.NextBarMarket ? "market" : x.StopMarket ? x.EntryTriggered ? "market" : "stop_market" : "limit",
                x.Requested - x.Entered, "unresolved", ReservedNotional(x), x.Evidence.ActiveAtUtc)).ToArray(),
            live.Where(x => x.Open > 0).Select(x => new BacktestPendingExit(x.Candidate.CandidateId, x.Candidate.StrategyId,
                x.Candidate.Ticker, x.Evidence.SessionDeadlineUtc, x.ExitReason ?? "session_deadline")).ToArray(),
            observations.Select(x => unavailableDate is { } date && x.SessionDate >= date
                ? x with { PositionValue = null, UnrealizedProfit = null, Equity = null, GrossExposurePct = null,
                    UnavailableReason = String.Join(';', unavailableReasons.Order()) } : x).ToArray())
        {
            StartUtc = request.StartUtc, SessionMarks = marks, UnavailableFromSession = unavailableDate,
            UnavailableReason = unavailableReasons.Count == 0 ? null : String.Join(';', unavailableReasons.Order()),
            AdmittedOrders = intents.Select(x => new BacktestAdmittedOrder(x.Candidate.CandidateId, x.Candidate.StrategyId,
                x.Candidate.Ticker, x.Requested, x.Evidence.LimitPrice, x.Candidate.Direction, x.Evidence.ActiveAtUtc)
                { DecisionStrategyId = x.Candidate.DecisionStrategyId }).ToArray()
        };

        /// <summary>Retain rejected candidates as admission evidence without pretending to route their orders.</summary>
        private void Reject(BacktestCandidateTrade candidate, string reason)
        {
            dispositions.Add(new(candidate.CandidateId, false, reason));
            events.Add(new(candidate.CandidateId, candidate.Ticker, candidate.EntryTimestamp, "rejected", reason));
        }

        /// <summary>Use closes only to cancel still-pending entry quantity, never to erase prior intrabar fills.</summary>
        private static bool InsidePreviousRange(Intent intent, decimal close) => intent.Long
            ? close <= intent.Evidence.PreviousSessionHigh : close >= intent.Evidence.PreviousSessionLow;

        /// <summary>Reject malformed OHLC evidence instead of letting the matcher invent an executable range.</summary>
        private static bool ValidBar(OhlcvBar bar) => bar.Open > 0 && bar.Low > 0 && bar.Close > 0 &&
            bar.Low <= Math.Min(bar.Open, bar.Close) && bar.High >= Math.Max(bar.Open, bar.Close) && bar.High >= bar.Low && bar.Volume >= 0;

        /// <summary>Emit small immutable order/fill evidence with current protection, preserving the actual policy explanation.</summary>
        private void Emit(Intent intent, DateTimeOffset at, string state, string reason, int quantity = 0, decimal? price = null) =>
            events.Add(new(intent.Candidate.CandidateId, intent.Candidate.Ticker, at, state, reason, quantity, price, intent.Stop, intent.Target));

        /// <summary>Map a UTC execution stamp to the exchange source-session date used by monthly accounting.</summary>
        private DateOnly Date(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timezone).DateTime);

        /// <summary>Resolve a calendar time to UTC using the selected strategy's exchange zone, including DST and early-close schedules.</summary>
        private DateTimeOffset At(DateOnly day, TimeOnly time)
        {
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, timezone.GetUtcOffset(local)).ToUniversalTime();
        }
    }
}
