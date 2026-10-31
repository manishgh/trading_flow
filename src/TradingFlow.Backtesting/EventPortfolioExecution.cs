using System.Collections.Frozen;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

public sealed record EventPortfolioReplayRequest(string RunName, PortfolioConfig Portfolio,
    IReadOnlyList<StrategyDefinition> Strategies, IReadOnlyList<BacktestCandidateTrade> Candidates,
    IReadOnlyCollection<OhlcvBar> MarketBars, IReadOnlyDictionary<string, string> ExcludedCandidates);
public sealed record EventPortfolioReplayBinding(
    Func<EventPortfolioReplayRequest, CancellationToken, Task<EventPortfolioReplayOutcome>> ExecuteAsync);
public sealed record EventPortfolioDisposition(string CandidateId, bool Accepted, string Reason, int RequestedQuantity = 0);
public sealed record EventPortfolioReplayOutcome(IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<BacktestExecutionFailure> Failures, IReadOnlyList<EventPortfolioDisposition> Dispositions,
    IReadOnlyList<TimedSimulatedExecutionEvent> Events, long? UnavailableFromNanoseconds,
    IReadOnlyList<string> UnavailableReasons);

/// <summary>One cash ledger and shared book per portfolio; scenarios share source reading, never capital.</summary>
public static partial class EventPortfolioExecution
{
    /// <summary>Consume verified source once and advance each independently funded scenario through the same frames.</summary>
    public static async Task<IReadOnlyList<EventPortfolioReplayOutcome>> ReplayManyAsync(
        IReadOnlyList<EventPortfolioReplayRequest> requests, IAsyncEnumerable<SipPortfolioExecutionFrame> source,
        Func<EventPortfolioReplayRequest, ExecutionEventSimulationPolicy> policy,
        CancellationToken cancellationToken = default)
    {
        var controllers = requests.Select(x => new Controller(x, policy(x))).ToArray();
        await foreach (var frame in source.WithCancellation(cancellationToken))
            foreach (var controller in controllers)
                controller.Advance(frame);
        return controllers.Select(x => x.Complete()).ToArray();
    }

    /// <summary>Per-parent accounting is updated only from new fills; source events remain an append-only audit.</summary>
    private sealed class PositionIntent
    {
        /// <summary>Freeze the admitted quantity and candidate terms before any market execution.</summary>
        public PositionIntent(BacktestCandidateTrade candidate, int quantity)
        {
            Candidate = candidate;
            Quantity = quantity;
        }

        public BacktestCandidateTrade Candidate { get; }
        public int Quantity { get; }
        public bool ExitSubmitted { get; set; }
        public bool Submitted { get; set; }
        public int EntryQuantity { get; set; }
        public int ExitQuantity { get; set; }
        public decimal EntryNotional { get; set; }
        public decimal ExitNotional { get; set; }
        public decimal Fees { get; set; }
        public long? FirstEntryAt { get; set; }
        public long? LastExitAt { get; set; }
        public long LatestEntryReport { get; set; }
        public long LatestReductionReport { get; set; }
        public string? LastExitOrderId { get; set; }
        public bool Long => Candidate.Direction == "long";
        public int OpenQuantity => EntryQuantity - ExitQuantity;
        public decimal MaximumNotional { get; set; }
        public decimal OriginalTarget { get; set; }
        public ContinuationProtectionState? Protection { get; set; }
        public int ProtectionRevision { get; set; }
        public string? CurrentStopId { get; set; }
        public string? CurrentTargetId { get; set; }
        public string? ExitReason { get; set; }
        public string EntryId => Candidate.CandidateId + ":entry";
        public string StopId => Candidate.CandidateId + ":stop";
        public string TargetId => Candidate.CandidateId + ":target";
        public string ExitId => Candidate.CandidateId + ":liquidation";
    }

    private sealed partial class Controller
    {
        private const long EntryWindow = 300_000_000_000L;
        private readonly EventPortfolioReplayRequest request;
        private readonly ExecutionEventSimulationPolicy policy;
        private readonly DeterministicExecutionSimulator simulator = new();
        private readonly BacktestCandidateTrade[] candidates;
        private readonly List<PositionIntent> admitted = [];
        private readonly Dictionary<string, PositionIntent> live = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PositionIntent> orderOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BacktestTrade> completed = new(StringComparer.Ordinal);
        private readonly List<EventPortfolioDisposition> dispositions = [];
        private readonly List<TimedSimulatedExecutionEvent> events = [];
        private readonly Dictionary<string, ExecutionEventQuoteState> quotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unavailable = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExecutionEventOrderCommand>> pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> engineSequences = new(StringComparer.OrdinalIgnoreCase);
        private ExecutionEventBookState state = ExecutionEventBookState.Empty;
        private decimal cash;
        private decimal realizedEquity;
        private int nextCandidate;
        private long sequence;
        private long? failedAt;
        private long latestTime;
        private long nextDueExit = Int64.MaxValue;
        private long nextUnsubmittedExpiry = Int64.MaxValue;
        private long nextBrokerExpiry = Int64.MaxValue;

        /// <summary>Validate the bounded zero-delay policy and freeze deterministic pre-capital ordering.</summary>
        public Controller(EventPortfolioReplayRequest request, ExecutionEventSimulationPolicy policy)
        {
            this.request = request;
            this.policy = policy;
            intraday = request.Strategies.Count == 1 && request.Strategies[0].EntryRules.IntradayZone is not null;
            if (intraday) ValidateIntradayRequest();
            else if (request.Strategies.Count != 1 ||
                request.Candidates.Any(x => x.Direction != "long" ||
                    x.ExecutionReplay?.PolicyId != ExecutionReplayPolicy.DailyRthSipPortfolioV1 ||
                    x.ExecutionReplay.EventExecutionEndUtc is not { } end || end <= x.ExitTimestamp) ||
                policy.ProtectionActivation != ExecutionProtectionActivation.NativeParentFullyFilled ||
                policy.FillReportLatencyNanoseconds != 0 ||
                policy.Costs.SyntheticSpreadBps != 0 ||
                !request.Portfolio.PreventOverlappingTickerPositions ||
                request.Portfolio.MaxConcurrentPositions <= 0)
                throw new InvalidDataException("Portfolio event v1 requires one long strategy, native precommit, positive slots, zero report delay and actual quote spread.");
            candidates = request.Candidates.OrderBy(x => x.EntryTimestamp)
                .ThenByDescending(x => x.SelectionScore)
                .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.StrategyId, StringComparer.Ordinal)
                .ThenBy(x => x.CandidateId, StringComparer.Ordinal).ToArray();
            if (candidates.Select(x => x.CandidateId).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                throw new InvalidDataException("Candidate IDs must be unique within a portfolio.");
            cash = request.Portfolio.StartingCapital;
            realizedEquity = request.Portfolio.StartingCapital;
            InitializeIntradayClocks();
        }

        /// <summary>Preserve nanosecond clocks when converting completed-data decision timestamps.</summary>
        private static long Ns(DateTimeOffset value) => checked((value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
        // SIP policies still require their explicit precommitted executable exit.
        private static long Ns(DateTimeOffset? value) => Ns(value ?? throw new InvalidDataException("SIP execution requires a declared exit timestamp."));

        /// <summary>Retain the earliest economically relevant unknown boundary and every distinct reason.</summary>
        private void Fail(long at, string reason)
        {
            failedAt = failedAt is { } prior ? Math.Min(prior, at) : at;
            unavailable.Add(reason);
        }

        /// <summary>Advance admissions and exits before source matching; only earlier frames fund current admissions.</summary>
        public void Advance(SipPortfolioExecutionFrame frame)
        {
            latestTime = frame.EventTimeNanoseconds;
            if (failedAt is not null) return; // The caller still consumes the complete source for hash verification.
            if (frame.KnownAtNanoseconds != frame.EventTimeNanoseconds ||
                frame.Batches.Any(x => x.Market.EventTimeNanoseconds != frame.EventTimeNanoseconds ||
                    x.Market.KnownAtNanoseconds != frame.KnownAtNanoseconds))
                throw new InvalidDataException("Portfolio event v1 supports coherent frames in the declared zero source-delay scenario.");
            if (intraday) AdvanceIntradayClocks(frame.EventTimeNanoseconds, inclusive: false);
            AdvanceDueTimers(frame.EventTimeNanoseconds);
            if (failedAt is not null) return;
            if (frame.EventTimeNanoseconds >= nextUnsubmittedExpiry)
            {
                var missed = live.Values.Where(x => !x.Submitted &&
                    EntryExpiry(x.Candidate) <= frame.EventTimeNanoseconds)
                    .OrderBy(x => x.Candidate.EntryTimestamp).FirstOrDefault();
                if (missed is not null)
                {
                    Fail(Ns(missed.Candidate.EntryTimestamp), "entry_route_window_has_no_observed_source:" + missed.Candidate.CandidateId);
                    return;
                }
            }
            foreach (var intent in live.Values)
                if (Ns(intent.Candidate.ExecutionReplay!.EventExecutionEndUtc!.Value) <= frame.EventTimeNanoseconds)
                    Fail(Ns(intent.Candidate.ExecutionReplay.EventExecutionEndUtc.Value), "execution_horizon_ends_with_unresolved_route_or_exposure");
            if (failedAt is not null) return;
            AdmitDueCandidates(frame.EventTimeNanoseconds);
            if (failedAt is not null) return;
            if (!intraday && frame.EventTimeNanoseconds >= nextDueExit) ScheduleDailyExits(frame);
            foreach (var batch in frame.Batches)
            {
                ProcessBatch(batch);
                if (failedAt is not null) return;
            }
            if (intraday) AdvanceIntradayClocks(frame.EventTimeNanoseconds, inclusive: true);
        }

        /// <summary>Apply price-free precommitted broker expiries before admissions, even when that symbol has no observation.</summary>
        private void AdvanceDueTimers(long clock)
        {
            while (nextBrokerExpiry != Int64.MaxValue && (nextBrokerExpiry < clock || !intraday && nextBrokerExpiry == clock))
            {
                var next = state.Book.WorkingOrders.Values
                    .Where(order => state.OrderExpiries is { } expiries && expiries.ContainsKey(order.Command.ClientOrderId))
                    .OrderBy(order => state.OrderExpiries![order.Command.ClientOrderId])
                    .ThenBy(order => order.Command.Symbol, StringComparer.Ordinal).First();
                var at = state.OrderExpiries![next.Command.ClientOrderId];
                // This is a broker timer, with no invented quote, price, print or traded volume.
                var timer = new ExecutionEventBatch(next.Command.Symbol, at, at, 0, [], [], CoverageComplete: true);
                ApplyStep(timer, Array.Empty<ExecutionEventOrderCommand>());
                if (failedAt is not null) return;
            }
        }

        /// <summary>Dequeue each due candidate once; admission policy stays readable in the single-candidate method.</summary>
        private void AdmitDueCandidates(long clock)
        {
            while (nextCandidate < candidates.Length && Ns(candidates[nextCandidate].EntryTimestamp) <= clock)
            {
                AdmitCandidate(candidates[nextCandidate++], clock);
                if (failedAt is not null) return;
            }
        }

        /// <summary>Apply eligibility, route, ticker/slot and cash/risk checks in that order, then reserve one entry.</summary>
        private void AdmitCandidate(BacktestCandidateTrade candidate, long clock)
        {
            if (request.ExcludedCandidates.TryGetValue(candidate.CandidateId, out var excluded))
            {
                dispositions.Add(new(candidate.CandidateId, false, excluded));
                return;
            }
            var ready = Ns(candidate.EntryTimestamp);
            if (clock >= EntryExpiry(candidate))
            {
                Fail(ready, "entry_route_window_has_no_observed_source:" + candidate.CandidateId);
                return;
            }
            if (intraday && !AdmitIntradayEntry(candidate, clock)) return;
            if (request.Portfolio.PreventOverlappingTickerPositions &&
                live.Values.Any(x => x.Candidate.Ticker.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase)))
            {
                dispositions.Add(new(candidate.CandidateId, false, "overlapping_ticker_position"));
                return;
            }
            if (live.Count >= request.Portfolio.MaxConcurrentPositions)
            {
                dispositions.Add(new(candidate.CandidateId, false, "max_concurrent_positions_reached"));
                return;
            }
            var reserved = live.Values.Sum(x => Math.Max(0, x.Quantity - x.EntryQuantity) * x.Candidate.EntryPrice);
            var available = intraday ? IntradayAvailableCapital() : Math.Max(0m, cash - reserved);
            var requested = Math.Min(realizedEquity / request.Portfolio.MaxConcurrentPositions, available);
            var plan = new SharedOrderRiskPlanner().Plan(new(candidate.Ticker,
                candidate.Direction == "long" ? PlannedOrderSide.Long : PlannedOrderSide.Short, realizedEquity,
                candidate.EntryPrice, PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, candidate.StopLossPrice),
                new(request.Portfolio.AccountRiskBudgetPct, request.Portfolio.MaxPositionNotionalPct),
                new(intraday ? policy.Costs.BaseSlippageBps + policy.Costs.ImpactBpsAtMaxParticipation : 0,
                    request.Portfolio.FixedBuyFee, request.Portfolio.FixedSellFee), requested));
            if (!plan.IsAccepted || plan.Order is null)
            {
                dispositions.Add(new(candidate.CandidateId, false, "order_plan:" + plan.Rejection?.Code));
                return;
            }
            CommitEntry(candidate, plan.Order.Quantity, ready);
        }

        /// <summary>Reserve the parent immediately and queue immutable native legs before any equal-open fill.</summary>
        private void CommitEntry(BacktestCandidateTrade candidate, int quantity, long ready)
        {
            if (intraday) { CommitIntradayEntry(candidate, quantity, ready); return; }
            var intent = new PositionIntent(candidate, quantity);
            admitted.Add(intent);
            live.Add(candidate.CandidateId, intent);
            foreach (var id in new[] { intent.EntryId, intent.StopId, intent.TargetId, intent.ExitId })
                orderOwners.Add(id, intent);
            dispositions.Add(new(candidate.CandidateId, true, "native_parent_and_children_committed", quantity));
            Add(candidate.Ticker, new(new(ExecutionCommandKind.Submit, intent.EntryId, candidate.Ticker,
                SimulatedOrderSide.Buy, SimulatedOrderType.Market, quantity, ExecutionEventTime.ToUtc(ready),
                sequence++, false, SimulatedTimeInForce.Day, FixedFeeGroupId: intent.EntryId), ready,
                ExpiresAtNanoseconds: checked(ready + EntryWindow)));
            foreach (var (id, type, price) in new[] { (intent.StopId, SimulatedOrderType.Stop, candidate.StopLossPrice),
                (intent.TargetId, SimulatedOrderType.Limit, candidate.TakeProfitPrice) })
                Add(candidate.Ticker, new(new(ExecutionCommandKind.Submit, id, candidate.Ticker,
                    SimulatedOrderSide.Sell, type, quantity, ExecutionEventTime.ToUtc(ready), sequence++, true,
                    SimulatedTimeInForce.GoodTillCanceled, LimitPrice: type == SimulatedOrderType.Limit ? price : null,
                    StopPrice: type == SimulatedOrderType.Stop ? price : null, FixedFeeGroupId: candidate.CandidateId + ":exit-fee",
                    OcoGroupId: candidate.CandidateId), ready, intent.EntryId, IsProtection: true, NativeParentCommitted: true));
            RefreshDeadlines();
        }

        /// <summary>Queue commands by symbol without routing them on another symbol's observation.</summary>
        private void Add(string symbol, ExecutionEventOrderCommand command)
        {
            if (!pending.TryGetValue(symbol, out var list)) pending[symbol] = list = [];
            list.Add(command);
        }

        /// <summary>Commit due liquidations from earlier used reports, not from the first subsequent observed print.</summary>
        private void ScheduleDailyExits(SipPortfolioExecutionFrame frame)
        {
            foreach (var intent in live.Values.Where(x => x.Submitted && !x.ExitSubmitted &&
                Ns(x.Candidate.ExitTimestamp) <= frame.EventTimeNanoseconds).ToArray())
            {
                if (!frame.Batches.Any(x => x.InRegularSession &&
                    x.Market.Symbol.Equals(intent.Candidate.Ticker, StringComparison.OrdinalIgnoreCase))) continue;
                var ready = Math.Max(Ns(intent.Candidate.ExitTimestamp),
                    Math.Max(intent.LatestEntryReport, intent.LatestReductionReport));
                if (ready > frame.EventTimeNanoseconds)
                    throw new InvalidDataException("A liquidation cannot use an unreceived fill report.");
                foreach (var id in new[] { intent.EntryId, intent.StopId, intent.TargetId })
                    if (state.Book.WorkingOrders.TryGetValue(id, out var order))
                        Add(intent.Candidate.Ticker, new(new(ExecutionCommandKind.Cancel, id + ":cancel", intent.Candidate.Ticker,
                            order.Command.Side, order.Command.OrderType, 0, ExecutionEventTime.ToUtc(ready), sequence++, true,
                            TargetClientOrderId: id), ready));
                var quantity = intent.EntryQuantity - intent.ExitQuantity;
                if (quantity > 0)
                    Add(intent.Candidate.Ticker, new(new(ExecutionCommandKind.Submit, intent.ExitId, intent.Candidate.Ticker,
                        SimulatedOrderSide.Sell, SimulatedOrderType.Market, quantity, ExecutionEventTime.ToUtc(ready), sequence++, true,
                        SimulatedTimeInForce.GoodTillCanceled, FixedFeeGroupId: intent.Candidate.CandidateId + ":exit-fee"),
                        ready, intent.EntryId));
                intent.ExitSubmitted = true;
            }
            RefreshDeadlines();
        }

        /// <summary>Process only relevant regular-session work; prefix quotes seed reference state without fills.</summary>
        private void ProcessBatch(SipPortfolioExecutionBatch item)
        {
            var batch = item.Market;
            if (!item.InRegularSession)
            {
                ObserveQuotes(batch, item.UnknownEvidenceReasons.Length > 0);
                return;
            }
            IReadOnlyList<ExecutionEventOrderCommand> commands = pending.TryGetValue(batch.Symbol, out var queued)
                ? queued : Array.Empty<ExecutionEventOrderCommand>();
            var hasWorking = false;
            var dueTimer = false;
            foreach (var order in state.Book.WorkingOrders.Values)
            {
                if (!order.Command.Symbol.Equals(batch.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
                hasWorking = true;
                dueTimer |= state.OrderExpiries is { } expiries &&
                    expiries.TryGetValue(order.Command.ClientOrderId, out var expiry) && expiry <= batch.EventTimeNanoseconds;
            }
            if ((hasWorking || commands.Count > 0) && item.UnknownEvidenceReasons.Length > 0)
            {
                Fail(batch.EventTimeNanoseconds, "relevant_unknown_source:" + batch.Symbol + ":" +
                    String.Join('|', item.UnknownEvidenceReasons));
                return;
            }
            if (batch.Trades.Count > 0 && commands.Any(command => command.KnownAtNanoseconds == batch.EventTimeNanoseconds &&
                command.Command.Kind == ExecutionCommandKind.Submit && command.Command.IsRiskReducing &&
                command.Command.OrderType == SimulatedOrderType.Market) &&
                state.Book.WorkingOrders.Values.Any(order => order.Command.IsRiskReducing &&
                    order.Command.Symbol.Equals(batch.Symbol, StringComparison.OrdinalIgnoreCase)))
            {
                // The old native leg and a newly due client liquidation can race at
                // this exact nanosecond. Neither an invented ordering nor a thrown
                // half-built matcher step may turn it into a completed outcome.
                Fail(batch.EventTimeNanoseconds, "same_nanosecond_liquidation_and_native_protection_ambiguous");
                return;
            }
            if (commands.Count == 0 && (!hasWorking || batch.Trades.Count == 0 && !dueTimer))
            {
                ObserveQuotes(batch, false);
                return;
            }
            ApplyStep(batch, commands);
        }

        /// <summary>Advance the shared matcher once, account only new fills, and retire closed or canceled live intents.</summary>
        private void ApplyStep(ExecutionEventBatch batch, IReadOnlyList<ExecutionEventOrderCommand> commands)
        {
            state = state with { Quotes = quotes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) };
            // Source sequence belongs to the archive. Broker timers and processed market
            // steps need their own monotone book cursor so neither can collide with it.
            var nextSequence = checked(engineSequences.GetValueOrDefault(batch.Symbol) + 1);
            engineSequences[batch.Symbol] = nextSequence;
            var step = simulator.AdvanceEvent(new(state, batch with { Sequence = nextSequence }, commands, policy));
            state = step.State;
            events.AddRange(step.Events);
            pending.Remove(batch.Symbol);
            foreach (var emitted in step.Events) AccountFill(emitted);
            if (intraday) RefreshIntradayProtection(batch, step.Events);
            foreach (var intent in live.Values.Where(x =>
                x.Candidate.Ticker.Equals(batch.Symbol, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                intent.Submitted |= state.Book.KnownOrderContracts.ContainsKey(intent.EntryId);
                if (!intent.Submitted || state.Book.WorkingOrders.ContainsKey(intent.EntryId) ||
                    intent.EntryQuantity != intent.ExitQuantity) continue;
                if (intent.EntryQuantity > 0)
                {
                    var trade = BuildCompletedTrade(intent);
                    completed.Add(intent.Candidate.CandidateId, trade);
                    realizedEquity += trade.NetProfit;
                }
                live.Remove(intent.Candidate.CandidateId);
            }
            quotes.Clear();
            foreach (var pair in state.Quotes) quotes[pair.Key] = pair.Value;
            foreach (var reason in step.UnavailableReasons) Fail(batch.EventTimeNanoseconds, batch.Symbol + ":" + reason);
            foreach (var rejection in step.Events.Where(x => x.Event.State == "rejected"))
                Fail(batch.EventTimeNanoseconds, "broker_route_rejected:" + rejection.Event.Reason);
            if (cash < 0) Fail(batch.EventTimeNanoseconds, "cash_overdrawn_no_borrowing_modeled");
            RefreshDeadlines();
        }

        /// <summary>Update cash and the owning parent's cumulative fills once; never rescan the audit history.</summary>
        private void AccountFill(TimedSimulatedExecutionEvent emitted)
        {
            var fill = emitted.Event;
            if (fill.LastFillQuantity <= 0) return;
            if (!orderOwners.TryGetValue(fill.ClientOrderId, out var intent) ||
                fill.FillPrice is not { } price || emitted.KnownAtNanoseconds != emitted.OccurredAtNanoseconds)
                throw new InvalidDataException("An event fill lacks its portfolio owner, price or zero-delay receipt.");
            var notional = fill.LastFillQuantity * price;
            cash += (fill.Side == SimulatedOrderSide.Buy ? -notional : notional) - fill.Fees;
            intent.Fees += fill.Fees;
            if (!fill.IsRiskReducing)
            {
                if (intraday && intent.EntryQuantity == 0) CountIntradayEntry(intent);
                intent.EntryQuantity = checked(intent.EntryQuantity + fill.LastFillQuantity);
                intent.EntryNotional += notional;
                intent.FirstEntryAt ??= emitted.OccurredAtNanoseconds;
                intent.LatestEntryReport = Math.Max(intent.LatestEntryReport, emitted.KnownAtNanoseconds);
            }
            else
            {
                intent.ExitQuantity = checked(intent.ExitQuantity + fill.LastFillQuantity);
                intent.ExitNotional += notional;
                intent.LastExitAt = emitted.OccurredAtNanoseconds;
                intent.LatestReductionReport = Math.Max(intent.LatestReductionReport, emitted.KnownAtNanoseconds);
                intent.LastExitOrderId = fill.ClientOrderId;
            }
            if (intent.ExitQuantity > intent.EntryQuantity)
                throw new InvalidDataException("A portfolio exit exceeds its actual parent entry fills.");
        }

        /// <summary>Recompute small live-set deadlines after state changes; no historical orders appear in frame timers.</summary>
        private void RefreshDeadlines()
        {
            nextDueExit = Int64.MaxValue;
            nextUnsubmittedExpiry = Int64.MaxValue;
            nextBrokerExpiry = Int64.MaxValue;
            foreach (var intent in live.Values)
            {
                if (intent.Submitted && !intent.ExitSubmitted)
                    nextDueExit = Math.Min(nextDueExit, Ns(intent.Candidate.ExitTimestamp));
                if (!intent.Submitted)
                    nextUnsubmittedExpiry = Math.Min(nextUnsubmittedExpiry, EntryExpiry(intent.Candidate));
            }
            foreach (var order in state.Book.WorkingOrders.Values)
                if (state.OrderExpiries is { } expiries && expiries.TryGetValue(order.Command.ClientOrderId, out var expiry))
                    nextBrokerExpiry = Math.Min(nextBrokerExpiry, expiry);
        }

        /// <summary>Refresh only a unique eligible quote; repeated identity never replenishes displayed capacity.</summary>
        private void ObserveQuotes(ExecutionEventBatch batch, bool unknown)
        {
            if (batch.Quotes.Count == 0) return;
            if (!unknown && ExecutionEventQuotes.UniqueEligible(batch.Quotes) is { } quote)
            {
                if (quotes.GetValueOrDefault(batch.Symbol)?.Quote.EventId == quote.EventId)
                {
                    Fail(batch.EventTimeNanoseconds, "reused_quote_event_identity");
                    return;
                }
                quotes[batch.Symbol] = new(quote, quote.BidSizeShares, quote.AskSizeShares, batch.KnownAtNanoseconds);
            }
            else quotes.Remove(batch.Symbol);
        }

        /// <summary>Construct one closed-parent record from cumulative fills, preserving actual fees and partial-fill quantity.</summary>
        private static BacktestTrade BuildCompletedTrade(PositionIntent intent)
        {
            var c = intent.Candidate;
            var quantity = intent.EntryQuantity;
            var entry = intent.EntryNotional / quantity;
            var exit = intent.ExitNotional / quantity;
            var gross = (intent.ExitNotional - intent.EntryNotional) * (intent.Long ? 1 : -1);
            var reason = intent.ExitReason ?? (intent.LastExitOrderId == (intent.CurrentStopId ?? intent.StopId) ? "stop_loss" :
                intent.LastExitOrderId == (intent.CurrentTargetId ?? intent.TargetId) ? "take_profit" : c.ExitReason);
            return new(c.Ticker, c.StrategyName, c.Direction,
                ExecutionEventTime.ToUtc(intent.FirstEntryAt!.Value), ExecutionEventTime.ToUtc(intent.LastExitAt!.Value),
                quantity, entry, exit, c.StopLossPrice, intent.OriginalTarget > 0 ? intent.OriginalTarget : c.TakeProfitPrice, reason, gross, intent.Fees,
                gross - intent.Fees, intent.Quantity, quantity < intent.Quantity ? "partial_fill" : "filled",
                StrategyId: c.StrategyId, CandidateId: c.CandidateId);
        }

        /// <summary>Resolve every route/exposure and date missing evidence from its first required decision, never source end.</summary>
        public EventPortfolioReplayOutcome Complete()
        {
            if (nextCandidate < candidates.Length)
                Fail(Ns(candidates[nextCandidate].EntryTimestamp), "source_ends_before_all_candidate_routes");
            foreach (var intent in live.Values)
            {
                if (!intent.Submitted)
                    Fail(Ns(intent.Candidate.EntryTimestamp), "accepted_entry_never_registered_source_unavailable");
                else if (state.Book.WorkingOrders.ContainsKey(intent.EntryId))
                    Fail(Ns(intent.Candidate.EntryTimestamp), "entry_route_unresolved_at_source_end");
                else if (intent.EntryQuantity > intent.ExitQuantity)
                    Fail(Ns(intent.Candidate.ExitTimestamp), intent.ExitSubmitted
                        ? "liquidation_unresolved_at_source_end" : "scheduled_exit_source_unavailable");
            }
            if (pending.Count > 0 && failedAt is null)
                Fail(pending.Values.SelectMany(x => x).Min(x => x.KnownAtNanoseconds), "commands_unresolved_at_source_end");
            if (state.Book.WorkingOrders.Count > 0 && failedAt is null)
                Fail(latestTime, "working_orders_unresolved_at_source_end");
            if (state.Book.Positions.Values.Any(x => x.SignedQuantity != 0) && failedAt is null)
                Fail(latestTime, "position_open_at_source_end");
            var trades = completed.Values.OrderBy(x => x.EntryTimestamp).ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ToArray();
            var failures = failedAt is null ? [] : candidates.Where(x => !completed.ContainsKey(x.CandidateId) &&
                !dispositions.Any(y => y.CandidateId == x.CandidateId && !y.Accepted))
                .Select(BuildFailure).ToArray();
            if (failedAt is not null && failures.Length == 0 && candidates.Length > 0)
                failures = [BuildFailure(candidates[^1])];
            return new(trades, failures, dispositions, events, failedAt, unavailable.Order().ToArray());
        }

        /// <summary>Publish an unavailable candidate without inventing its missing executions or return.</summary>
        private BacktestExecutionFailure BuildFailure(BacktestCandidateTrade candidate)
        {
            var intent = admitted.FirstOrDefault(x => x.Candidate.CandidateId == candidate.CandidateId);
            return new(candidate.CandidateId, candidate.Ticker, candidate.StrategyName, candidate.StrategyId,
                candidate.Direction, candidate.EntryTimestamp, String.Join(';', unavailable.Order()),
                intent?.Quantity ?? 0, intent?.EntryQuantity ?? 0, intent?.ExitQuantity ?? 0,
                ((intent?.EntryQuantity ?? 0) - (intent?.ExitQuantity ?? 0)) * (candidate.Direction == "long" ? 1 : -1),
                intent?.EntryNotional ?? 0, intent?.ExitNotional ?? 0, intent?.Fees ?? 0, null,
                latestTime == 0 ? null : ExecutionEventTime.ToUtc(latestTime), EvidenceUnavailable: true);
        }
    }
}
