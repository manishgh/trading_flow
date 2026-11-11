using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting;

// Opt-in execution rules of the published opening-range method (Zarattini, Barbon and Aziz; design
// docs/design-review/2026-11-02/orb-stocks-in-play-design.md, component 4): a gross exposure limit that sizes stop orders at
// their trigger, a stop measured from the actual fill, an exit at the official session close, and one-minute bars that
// order an entry and a stop inside one five-minute bar (design order of work, step 4). Runs that do not opt in
// never reach these operations.
public static partial class IntradayPortfolioExecution
{
    /// <summary>Event state recording an assumed order between stop orders that triggered in the same five-minute bar.</summary>
    public const string TriggerOrderAssumedState = "trigger_order_assumed";

    /// <summary>Two or more stop orders triggered in one five-minute bar without one-minute bars: processed in admission order.</summary>
    public const string SameBarTriggerOrderUnknown = "same_bar_trigger_order_unknown_admission_order_used";

    /// <summary>Two or more stop orders first reached their triggers in the same minute (or at the same opening): admission order.</summary>
    public const string SameMinuteTriggerOrderUnknown = "same_minute_trigger_order_unknown_admission_order_used";

    /// <summary>
    /// Event state recorded once for every entry five-minute bar that also touched the stop, with what one-minute bars said:
    /// one of the <see cref="EntryBarMinuteOrderReasons"/>. Results count these per reason.
    /// </summary>
    public const string EntryBarMinuteOrderState = "entry_bar_minute_order";

    /// <summary>Minutes show the stop touched after the entry minute: a known loss.</summary>
    public const string MinutesSettledStopAfterEntry = "settled_by_minutes_stop_after_entry";

    /// <summary>Minutes show no touch of the stop after the entry minute: the position stays open.</summary>
    public const string MinutesSettledNoStopAfterEntry = "settled_by_minutes_no_stop_after_entry";

    /// <summary>Entry and stop in the same minute: still unknown inside it, counted as a loss.</summary>
    public const string SameMinuteLoss = "same_minute_loss";

    /// <summary>No one-minute bars inside the entry bar: the worst case (a loss) stands.</summary>
    public const string UnknownNoMinuteBars = "unknown_no_minute_bars";

    /// <summary>One-minute bars exist but do not reproduce the five-minute high and low: the worst case (a loss) stands.</summary>
    public const string UnknownMinutesDoNotMatch = "unknown_minutes_do_not_match";

    /// <summary>Every reason an <see cref="EntryBarMinuteOrderState"/> event can carry, in reporting order.</summary>
    public static readonly IReadOnlyList<string> EntryBarMinuteOrderReasons =
        [MinutesSettledStopAfterEntry, MinutesSettledNoStopAfterEntry, SameMinuteLoss, UnknownNoMinuteBars, UnknownMinutesDoNotMatch];

    private sealed partial class Book
    {
        /// <summary>True when the run limits gross exposure (portfolio.max_gross_exposure_pct) instead of cash only.</summary>
        private bool GrossLimited => request.Portfolio.MaxGrossExposurePct is not null;

        /// <summary>
        /// Gross notional still available under the limit: the limit times marked equity, less every live reservation
        /// (filled or triggered positions; untriggered stop orders reserve nothing) and the fixed fees of one round trip.
        /// Per-share commission depends on the quantity, so <see cref="CapAtTrigger"/> charges it per share.
        /// </summary>
        private decimal GrossCapacity(decimal equity) =>
            Math.Max(0m, equity * request.Portfolio.MaxGrossExposurePct!.Value / 100m - live.Sum(Reserved) -
                request.Portfolio.FixedBuyFee - request.Portfolio.FixedSellFee);

        /// <summary>
        /// The gross room stop orders triggering inside one five-minute bar share. It is frozen when the bar opens (cash,
        /// completed-bar marks and reservations at that moment), and each trigger in the bar claims from it in processing
        /// order. Exits, cancellations and closes later in the same bar never add to it, because they may have happened after
        /// a trigger. Also records which triggers the room cut or canceled, and the same-bar order assumptions to report.
        /// </summary>
        private sealed class TriggerBudget(DateTimeOffset start, decimal room)
        {
            public DateTimeOffset Start { get; } = start;
            public decimal Remaining { get; private set; } = room;
            public HashSet<Intent> Limited { get; } = [];
            public List<(Intent[] Group, string Reason)> Assumptions { get; } = [];

            /// <summary>Takes <paramref name="amount"/> of the room for a sized trigger; the room never goes below zero.</summary>
            public void Claim(decimal amount) => Remaining = Math.Max(0m, Remaining - amount);
        }

        private TriggerBudget? triggerBudget;

        /// <summary>Freezes the gross room at the opening of the bar starting at <paramref name="start"/>, before any of its work.</summary>
        private void OpenTriggerBudget(DateTimeOffset start) => triggerBudget = new(start, GrossCapacity(Equity()));

        /// <summary>
        /// Ends the bar's budget and reports each assumed same-bar trigger order that changed an outcome: a later order of the
        /// group was cut below its admitted size or canceled for lack of room. An assumption that changed nothing is not reported.
        /// </summary>
        private void CloseTriggerBudget(DateTimeOffset start)
        {
            var budget = RequireTriggerBudget(start);
            foreach (var (group, reason) in budget.Assumptions.Where(item => item.Group.Skip(1).Any(budget.Limited.Contains)))
                foreach (var intent in group) Emit(intent, start, TriggerOrderAssumedState, reason);
            triggerBudget = null;
        }

        /// <summary>The budget of the bar starting at <paramref name="start"/>; failing loudly if sizing ran outside that bar.</summary>
        private TriggerBudget RequireTriggerBudget(DateTimeOffset start) =>
            triggerBudget is { } budget && budget.Start == start
                ? budget : throw new InvalidOperationException("Gross exposure sizing ran outside its bar's frozen budget.");

        /// <summary>
        /// The deadline an order of this candidate's strategy must carry in a session: the close itself when the strategy exits
        /// at the official close, otherwise the opening of the last five-minute bar.
        /// </summary>
        private DateTimeOffset SessionDeadline(BacktestCandidateTrade candidate, DateTimeOffset close) =>
            strategies[candidate.StrategyId].EntryRules.OpeningRangeBreakout is { ExitAtSessionClose: true } ? close : close.AddMinutes(-5);

        /// <summary>
        /// Sizes a triggered stop order under the gross exposure limit, in the order orders trigger: at most what the remaining
        /// gross capacity buys at the reference price plus adverse slippage, with the per-share commission of both the entry
        /// and the exit set aside from the same headroom. Cancels the order when nothing is left; otherwise lowers its quantity
        /// and records the cap. Returns false when the order was canceled. The room is the bar's frozen budget
        /// (<see cref="TriggerBudget"/>) less what earlier triggers in the same bar claimed, so nothing that happens later in
        /// that bar (a close, an exit, a released reservation) can enlarge it.
        /// </summary>
        private bool CapAtTrigger(Intent intent, decimal referencePrice, DateTimeOffset at, DateTimeOffset intervalStart)
        {
            var budget = RequireTriggerBudget(intervalStart);
            var friction = (intent.Strategy.Execution.SlippageBps + request.Portfolio.ImpactBpsAtMaxParticipation) / 10000m;
            var perShare = referencePrice * (1m + friction) + 2 * request.Portfolio.CommissionPerShare;
            var quantity = perShare <= 0m ? 0 : (int)Math.Min(Int32.MaxValue, Math.Floor(budget.Remaining / perShare));
            if (quantity <= 0)
            {
                budget.Limited.Add(intent);
                CancelRemainder(intent, at, "gross_exposure_limit_reached_at_trigger");
                return false;
            }

            if (quantity < intent.Requested)
            {
                budget.Limited.Add(intent);
                intent.LowerRequestedTo(quantity);
                Emit(intent, at, "quantity_capped", "gross_exposure_limit_at_trigger", quantity);
            }

            budget.Claim(intent.Requested * perShare);
            return true;
        }

        /// <summary>
        /// Under a gross exposure limit, decides the order in which stop orders that first trigger in the same five-minute bar
        /// claim the remaining capacity. With complete one-minute bars for every such stock, they are processed by the minute
        /// in which each first reached its trigger (an opening through the trigger counts as the bar's open); otherwise, and
        /// between orders that triggered in the same minute, the admission order is kept and the group is recorded as an
        /// assumption; <see cref="CloseTriggerBudget"/> reports it with a named <see cref="TriggerOrderAssumedState"/> event
        /// only if it changed an outcome. Only the positions of those orders in the interval's work list change; every other
        /// order keeps its place.
        /// </summary>
        private Intent[] OrderSameBarTriggers(Intent[] active, DateTimeOffset start)
        {
            var triggering = Enumerable.Range(0, active.Length).Where(index => TriggersNow(active[index], start)).ToArray();
            if (triggering.Length < 2) return active;
            var assumptions = RequireTriggerBudget(start).Assumptions;
            var minutes = triggering.Select(index => TriggerMinute(active[index], bars[(Symbol(active[index]), start)])).ToArray();
            if (minutes.Any(minute => minute is null))
            {
                assumptions.Add((triggering.Select(index => active[index]).ToArray(), SameBarTriggerOrderUnknown));
                return active;
            }

            var ordered = triggering.Zip(minutes, (index, minute) => (Index: index, Minute: minute!.Value))
                .OrderBy(item => item.Minute).ThenBy(item => item.Index).ToArray();
            foreach (var tie in ordered.GroupBy(item => item.Minute).Where(group => group.Count() > 1))
                assumptions.Add((tie.Select(item => active[item.Index]).ToArray(), SameMinuteTriggerOrderUnknown));
            var result = (Intent[])active.Clone();
            for (var slot = 0; slot < triggering.Length; slot++) result[triggering[slot]] = active[ordered[slot].Index];
            return result;
        }

        /// <summary>
        /// True when an untriggered stop order would first reach its trigger in the valid, available five-minute bar starting
        /// at <paramref name="start"/>, the case in which <see cref="CapAtTrigger"/> sizes it during this interval.
        /// </summary>
        private bool TriggersNow(Intent intent, DateTimeOffset start)
        {
            if (!intent.StopMarket || !intent.Pending || intent.EntryTriggered || intent.StopTriggered) return false;
            if (!bars.TryGetValue((Symbol(intent), start), out var bar) || !ValidBar(bar) || bar.KnownAtUtc > start.AddMinutes(5)) return false;
            var trigger = intent.Evidence.LimitPrice;
            return intent.Long ? bar.High >= trigger : bar.Low <= trigger;
        }

        /// <summary>
        /// Minute offset inside <paramref name="bar"/> at which the stop order first reached its trigger: -1 when the bar opened
        /// through it, 0-4 from complete one-minute bars, or null when the one-minute bars are missing or incomplete.
        /// </summary>
        private int? TriggerMinute(Intent intent, OhlcvBar bar)
        {
            var trigger = intent.Evidence.LimitPrice;
            if (intent.Long ? bar.Open >= trigger : bar.Open <= trigger) return -1;
            if (CompleteMinutes(intent, bar) is not { } minutes) return null;
            var first = Array.FindIndex(minutes, minute => intent.Long ? minute.High >= trigger : minute.Low <= trigger);
            return first < 0 ? null : (int)(minutes[first].Timestamp - bar.Timestamp).TotalMinutes;
        }

        /// <summary>
        /// Re-measures the protective stop from the actual entry fill when the strategy asks for it: the planned stop distance
        /// (trigger to planned stop) applied to the fill, rounded outward to the tick, so a gap past the trigger keeps the
        /// planned risk per share. It is set once, from the first entry fill, and never moved afterwards: a later partial fill
        /// of the same order would otherwise shift a stop that was already resting (and may already have been compared with a
        /// bar), and a stop whose exit has triggered is never moved. The simpler of the two correct options was chosen.
        /// </summary>
        private static void RebaseStopOnFill(Intent intent)
        {
            if (intent.Strategy.EntryRules.OpeningRangeBreakout is not { StopFromFill: true } || intent.Entered == 0 ||
                intent.FillStop is not null || intent.StopTriggered) return;
            var evidence = intent.Evidence;
            var distance = Math.Abs(evidence.LimitPrice - intent.Candidate.StopLossPrice);
            var raw = intent.Long ? intent.Entry - distance : intent.Entry + distance;
            intent.FillStop = (intent.Long ? decimal.Floor(raw / evidence.TickSize) : decimal.Ceiling(raw / evidence.TickSize)) * evidence.TickSize;
        }

        /// <summary>
        /// True when a stop touched in <paramref name="bar"/> may have come before the entry: the bar is the entry's own bar,
        /// the strategy has no target (a tight stop), and the entry filled inside the bar rather than at its open. An entry at
        /// the open has the whole bar after it, so a stop in that bar is an ordinary stop loss.
        /// </summary>
        private static bool StopMayPrecedeEntry(Intent intent, OhlcvBar bar) =>
            !intent.Strategy.ExitRules.HasProfitTarget && intent.EntryBarStart == bar.Timestamp && intent.EntryAt != bar.Timestamp;

        /// <summary>What one-minute bars say about an entry and a stop that fall in the same five-minute bar.</summary>
        private enum EntryBarMinuteOrder
        {
            /// <summary>Not an entry bar with a stop touch, so nothing was asked of the minutes.</summary>
            Unknown,

            /// <summary>No one-minute bars inside the bar: the worst case (stop counted) stands.</summary>
            NoMinuteBars,

            /// <summary>One-minute bars that do not reproduce the bar's high and low: the worst case stands.</summary>
            MinutesDoNotMatch,

            /// <summary>The stop was touched in a minute after the entry's minute: a known stop-out.</summary>
            StopAfterEntry,

            /// <summary>Entry and stop in the same minute: still unknown inside it, counted as a loss as the design requires.</summary>
            SameMinute,

            /// <summary>No minute after the entry touched the stop: the five-minute low or high came before the entry.</summary>
            NoStopAfterEntry
        }

        private Dictionary<(string Symbol, DateTimeOffset Time), OhlcvBar>? minuteBarIndex;

        // One-minute bars by stock and minute start, built once on first use; duplicates are refused like duplicate five-minute bars.
        private Dictionary<(string Symbol, DateTimeOffset Time), OhlcvBar>? MinuteBarIndex => request.MinuteBars is null ? null
            : minuteBarIndex ??= request.MinuteBars.Where(bar => bar.Timeframe == "1m")
                .ToDictionary(bar => (bar.Ticker.ToUpperInvariant(), bar.Timestamp));

        /// <summary>The stock's upper-case symbol, the key of every bar index.</summary>
        private static string Symbol(Intent intent) => intent.Candidate.Ticker.ToUpperInvariant();

        /// <summary>
        /// The valid one-minute bars inside <paramref name="bar"/> in time order, or null when there are none or they do not
        /// reproduce the five-minute high and low exactly (incomplete minutes cannot order anything).
        /// </summary>
        private OhlcvBar[]? CompleteMinutes(Intent intent, OhlcvBar bar)
        {
            var minutes = MinutesInside(intent, bar);
            return minutes.Length == 0 || minutes.Max(minute => minute.High) != bar.High || minutes.Min(minute => minute.Low) != bar.Low
                ? null : minutes;
        }

        /// <summary>The valid one-minute bars of the stock inside <paramref name="bar"/>, in time order; empty when there are none.</summary>
        private OhlcvBar[] MinutesInside(Intent intent, OhlcvBar bar)
        {
            if (MinuteBarIndex is not { } index) return [];
            var symbol = Symbol(intent);
            return Enumerable.Range(0, 5)
                .Select(minute => index.GetValueOrDefault((symbol, bar.Timestamp.AddMinutes(minute))))
                .OfType<OhlcvBar>().Where(ValidBar).ToArray();
        }

        /// <summary>
        /// Orders an entry and a stop inside one five-minute bar from its one-minute bars. The entry minute is the first that
        /// reaches the trigger; the stop counts only from that minute on. Without minutes, or with minutes that do not reproduce
        /// the bar's high and low, the order stays unknown and the reason says which.
        /// </summary>
        private EntryBarMinuteOrder EntryBarStopOrder(Intent intent, OhlcvBar bar)
        {
            if (CompleteMinutes(intent, bar) is not { } minutes)
                return MinutesInside(intent, bar).Length == 0 ? EntryBarMinuteOrder.NoMinuteBars : EntryBarMinuteOrder.MinutesDoNotMatch;
            var trigger = intent.Evidence.LimitPrice;
            var entry = Array.FindIndex(minutes, minute => intent.Long ? minute.High >= trigger : minute.Low <= trigger);
            if (entry < 0) return EntryBarMinuteOrder.MinutesDoNotMatch;
            var stop = intent.Stop;
            bool Touches(OhlcvBar minute) => intent.Long ? minute.Low <= stop : minute.High >= stop;
            if (Touches(minutes[entry])) return EntryBarMinuteOrder.SameMinute;
            return minutes.Skip(entry + 1).Any(Touches) ? EntryBarMinuteOrder.StopAfterEntry : EntryBarMinuteOrder.NoStopAfterEntry;
        }

        /// <summary>Records what the minutes said about one entry bar with a stop touch (<see cref="EntryBarMinuteOrderState"/>).</summary>
        private void EmitEntryBarMinuteOrder(Intent intent, DateTimeOffset at, EntryBarMinuteOrder order) =>
            Emit(intent, at, EntryBarMinuteOrderState, order switch
            {
                EntryBarMinuteOrder.StopAfterEntry => MinutesSettledStopAfterEntry,
                EntryBarMinuteOrder.NoStopAfterEntry => MinutesSettledNoStopAfterEntry,
                EntryBarMinuteOrder.SameMinute => SameMinuteLoss,
                EntryBarMinuteOrder.MinutesDoNotMatch => UnknownMinutesDoNotMatch,
                _ => UnknownNoMinuteBars
            }, intent.Open);

        /// <summary>
        /// At 16:00, after the last five-minute bar has completed, cancels unfilled entry shares of orders that run until the
        /// close and exits open shares at the session's official closing price. Liquidity, when limited, uses the just-completed
        /// last bar's volume less the shares this order already filled against that bar. A missing closing price leaves the
        /// exposure unknown instead of inventing an exit.
        /// </summary>
        private void LiquidateAtSessionClose(DateTimeOffset close)
        {
            foreach (var intent in live.Where(x => !x.Failed && x.Evidence.SessionDeadlineUtc == close).ToArray())
            {
                CancelRemainder(intent, close, "session_close");
                if (intent.Open == 0) { Resolve(intent, close); continue; }
                var symbol = Symbol(intent);
                if (request.SessionClosePrices?.TryGetValue((symbol, Date(close)), out var price) != true || price <= 0m)
                { Fail(intent, close, "session_close_price_missing"); continue; }
                var lastStart = close.AddMinutes(-5);
                OhlcvBar? last = null;
                if (request.Portfolio.MaxBarParticipationPct > 0 &&
                    (!bars.TryGetValue((symbol, lastStart), out last) || !ValidBar(last)))
                { Fail(intent, close, "session_close_lagged_liquidity_evidence_missing"); continue; }
                var auction = new OhlcvBar(symbol, close, "5m", price, price, price, price, last?.Volume ?? 0m);
                var fill = Simulate(intent, auction, intent.Open, !intent.Long, SimulatedOrderType.Market, intent.FilledIn(lastStart),
                    null, null, false);
                intent.ExitReason = intent.StopTriggered ? intent.ExitReason ?? "stop_loss" : "session_close";
                if (fill.FilledQuantity > 0)
                    Account(intent, fill, false, close, intent.ExitReason + ";official_session_close_price", lastStart, "session_close_auction");
                if (intent.Open > 0) Fail(intent, close, "session_close_partial_liquidation_unresolved");
                else Resolve(intent, close);
            }
        }
    }
}
