using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting;

public static partial class EventPortfolioExecution
{
    private sealed partial class Controller
    {
        private static readonly long ManagementInterval = TimeSpan.FromMinutes(5).Ticks * 100;
        private readonly bool intraday;
        private readonly Dictionary<(string Symbol, DateOnly Day), int> intradayEntries = [];
        private readonly Dictionary<(string Symbol, long Start), OhlcvBar> managementBars = [];
        private long nextManagementClock = Int64.MaxValue;
        private long lastManagementClock;

        /// <summary>Keep the quote-based zone contract separate from the retained daily native-bracket contract.</summary>
        private void ValidateIntradayRequest()
        {
            if (policy.ProtectionActivation != ExecutionProtectionActivation.CustomPerFill ||
                policy.FillReportLatencyNanoseconds != 0 || policy.MaximumEntrySpreadBps is not > 0 ||
                policy.Costs.SyntheticSpreadBps != 0 || !request.Portfolio.PreventOverlappingTickerPositions ||
                request.Portfolio.MaxConcurrentPositions != 1 || request.Candidates.Any(candidate =>
                    candidate.Direction is not ("long" or "short") || candidate.ZoneExecution is not { } zone ||
                    zone.ActiveAtUtc != candidate.EntryTimestamp || zone.SessionDeadlineUtc != candidate.ExitTimestamp ||
                    candidate.ExecutionReplay?.PolicyId != ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 ||
                    candidate.ExecutionReplay.EventExecutionEndUtc is not { } end || end <= candidate.ExitTimestamp ||
                    zone.InitialStopPrice != candidate.StopLossPrice || zone.TickSize <= 0 ||
                    zone.MaxFilledParentTradesPerSession != request.Strategies[0].EntryRules.IntradayZone!.MaxFilledParentTradesPerSession ||
                    zone.ConfirmationBar.Timestamp.AddMinutes(5) != zone.ActiveAtUtc))
                throw new InvalidDataException("Intraday SIP execution requires causal zone evidence, both-side per-fill protection, a spread ceiling and complete session bounds.");
        }

        /// <summary>Index immutable completed candles once; timer progression never scans a growing history.</summary>
        private void InitializeIntradayClocks()
        {
            if (!intraday || candidates.Length == 0) return;
            foreach (var bar in request.MarketBars.Where(bar => bar.Timeframe == "5m"))
                if (!managementBars.TryAdd((bar.Ticker.ToUpperInvariant(), Ns(bar.Timestamp)), bar))
                    throw new InvalidDataException("Duplicate management candle timestamp.");
            nextManagementClock = (Ns(candidates[0].EntryTimestamp) / ManagementInterval + 1) * ManagementInterval;
            lastManagementClock = candidates.Max(candidate => Ns(candidate.ExitTimestamp));
        }

        /// <summary>Retain each policy's original expiry; zone market intents remain valid only until the session deadline.</summary>
        private long EntryExpiry(BacktestCandidateTrade candidate) => intraday
            ? Ns(candidate.ZoneExecution!.SessionDeadlineUtc) : checked(Ns(candidate.EntryTimestamp) + EntryWindow);

        /// <summary>Use the session encoded by the authoritative deadline, independently of the host timezone.</summary>
        private static (string, DateOnly) IntradaySession(BacktestCandidateTrade candidate) =>
            (candidate.Ticker.ToUpperInvariant(), DateOnly.FromDateTime(candidate.ZoneExecution!.SessionDeadlineUtc.UtcDateTime));

        /// <summary>Reject new exposure outside its deadline, daily cap or already-known quote admission limits.</summary>
        private bool AdmitIntradayEntry(BacktestCandidateTrade candidate, long clock)
        {
            string? reason = null;
            var zone = candidate.ZoneExecution!;
            if (clock >= Ns(zone.SessionDeadlineUtc)) reason = "session_entry_deadline_passed";
            else if (intradayEntries.GetValueOrDefault(IntradaySession(candidate)) >= zone.MaxFilledParentTradesPerSession)
                reason = "maximum_filled_parents_per_session";
            else if (!quotes.TryGetValue(candidate.Ticker, out var quote) ||
                quote.Quote.EventTimeNanoseconds >= clock || quote.KnownAtNanoseconds > clock ||
                clock - quote.Quote.EventTimeNanoseconds > policy.MaximumQuoteAgeNanoseconds)
                reason = "entry_prior_fresh_quote_unavailable";
            else if ((quote.Quote.Ask - quote.Quote.Bid) / ((quote.Quote.Ask + quote.Quote.Bid) / 2m) * 10_000m > policy.MaximumEntrySpreadBps)
                reason = "entry_spread_above_common_limit";
            if (reason is null) return true;
            dispositions.Add(new(candidate.CandidateId, false, reason));
            return false;
        }

        /// <summary>Reserve gross exposure for both directions; short-sale cash is not extra risk capital.</summary>
        private decimal IntradayAvailableCapital()
        {
            var used = live.Values.Sum(intent => Math.Max(0, intent.Quantity - intent.EntryQuantity) * intent.Candidate.EntryPrice +
                (intent.EntryQuantity == 0 ? 0 : intent.OpenQuantity * intent.EntryNotional / intent.EntryQuantity));
            return Math.Max(0m, realizedEquity - used);
        }

        /// <summary>Count a parent once on its first actual fill, including a partial fill.</summary>
        private void CountIntradayEntry(PositionIntent intent)
        {
            var key = IntradaySession(intent.Candidate);
            intradayEntries[key] = checked(intradayEntries.GetValueOrDefault(key) + 1);
        }

        /// <summary>Commit the market parent with pre-fill sizing; protection is derived only from received fills.</summary>
        private void CommitIntradayEntry(BacktestCandidateTrade candidate, int quantity, long ready)
        {
            var intent = new PositionIntent(candidate, quantity)
            {
                MaximumNotional = realizedEquity * request.Portfolio.MaxPositionNotionalPct / 100m
            };
            admitted.Add(intent);
            live.Add(candidate.CandidateId, intent);
            orderOwners.Add(intent.EntryId, intent);
            orderOwners.Add(intent.ExitId, intent);
            dispositions.Add(new(candidate.CandidateId, true, "market_parent_committed_with_per_fill_protection", quantity));
            Add(candidate.Ticker, new(new(ExecutionCommandKind.Submit, intent.EntryId, candidate.Ticker,
                intent.Long ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell, SimulatedOrderType.Market,
                quantity, ExecutionEventTime.ToUtc(ready), sequence++, false, SimulatedTimeInForce.Day,
                FixedFeeGroupId: intent.EntryId), ready, ExpiresAtNanoseconds: EntryExpiry(candidate)));
            RefreshDeadlines();
        }

        /// <summary>Advance candle/closing timers before later prints, and after equal-time prints have used the old protection.</summary>
        private void AdvanceIntradayClocks(long clock, bool inclusive)
        {
            while (failedAt is null && nextManagementClock <= lastManagementClock &&
                (nextManagementClock < clock || inclusive && nextManagementClock == clock))
            {
                var at = nextManagementClock;
                nextManagementClock = checked(nextManagementClock + ManagementInterval);
                foreach (var intent in live.Values.ToArray())
                {
                    if (at >= Ns(intent.Candidate.ExitTimestamp))
                        QueueIntradayLiquidation(intent, at, "session_deadline");
                    else if (intent.OpenQuantity > 0 && !intent.ExitSubmitted)
                        UpdateIntradayManagement(intent, at);
                }
                FlushIntradayCommands(at);
            }
        }

        /// <summary>Evaluate only a fully held, completed candle; missing management evidence is not a no-change observation.</summary>
        private void UpdateIntradayManagement(PositionIntent intent, long at)
        {
            if (intent.Protection is null || CurrentStopTriggered(intent)) return;
            var start = at - ManagementInterval;
            if (start < intent.FirstEntryAt) return;
            if (!managementBars.TryGetValue((intent.Candidate.Ticker.ToUpperInvariant(), start), out var bar) ||
                bar.KnownAtUtc > ExecutionEventTime.ToUtc(at))
            {
                Fail(at, "completed_management_candle_unavailable:" + intent.Candidate.Ticker);
                return;
            }
            var update = ContinuationProtectionPolicy.Evaluate(intent.Protection, bar,
                ExecutionEventTime.ToUtc(at), true, ExecutionEventTime.ToUtc(at));
            intent.Protection = update.State;
            if (update.LevelsChanged) QueueIntradayProtection(intent, at);
        }

        /// <summary>Route timer commands without inventing prices, liquidity, or trade observations.</summary>
        private void FlushIntradayCommands(long at)
        {
            foreach (var symbol in pending.Keys.ToArray())
            {
                // Equal-clock market evidence has already been processed. Its resulting
                // commands activate on the next observation, never a second same-time batch.
                if (state.LastEventTimes.TryGetValue(symbol, out var previous) && previous >= at) continue;
                if (pending[symbol].Any(command => command.KnownAtNanoseconds > at)) continue;
                var commands = pending[symbol].ToArray();
                ApplyStep(new(symbol, at, at, 0, [], [], CoverageComplete: true), commands);
                if (failedAt is not null) return;
            }
        }

        /// <summary>Initialize or rebase protective terms after fills; stop elections cancel remaining entry risk.</summary>
        private void RefreshIntradayProtection(ExecutionEventBatch batch, IReadOnlyList<TimedSimulatedExecutionEvent> emitted)
        {
            foreach (var intent in live.Values.Where(intent => intent.Candidate.Ticker.Equals(batch.Symbol, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (CurrentStopTriggered(intent))
                {
                    // The parent may also fill in the election batch before its report
                    // reaches us. Preserve those fills and flatten all owned shares later.
                    QueueIntradayLiquidation(intent, batch.KnownAtNanoseconds, "stop_loss");
                    continue;
                }
                var entryChanged = emitted.Any(value => value.Event.ClientOrderId == intent.EntryId && value.Event.LastFillQuantity > 0);
                var reduction = emitted.Any(value => value.Event.IsRiskReducing && value.Event.LastFillQuantity > 0 &&
                    orderOwners.GetValueOrDefault(value.Event.ClientOrderId) == intent);
                if (reduction) QueueCancel(intent, intent.EntryId, batch.KnownAtNanoseconds);
                if (intent.OpenQuantity <= 0)
                {
                    if (intent.EntryQuantity > 0) QueueCancel(intent, intent.EntryId, batch.KnownAtNanoseconds);
                    continue;
                }
                if ((!entryChanged && !reduction) || intent.ExitSubmitted) continue;
                if (TryRebaseIntradayProtection(intent, batch.KnownAtNanoseconds))
                    QueueIntradayProtection(intent, batch.KnownAtNanoseconds);
            }
        }

        /// <summary>Keep actual gap fills visible and reduce invalid or over-cap exposure instead of deleting the trade.</summary>
        private bool TryRebaseIntradayProtection(PositionIntent intent, long at)
        {
            var entry = intent.EntryNotional / intent.EntryQuantity;
            var risk = (entry - intent.Candidate.StopLossPrice) * (intent.Long ? 1 : -1);
            var tick = intent.Candidate.ZoneExecution!.TickSize;
            var targetR = request.Strategies[0].ExitRules.TargetRMultiple;
            var rawTarget = entry + (intent.Long ? 1 : -1) * targetR * risk;
            intent.OriginalTarget = (intent.Long ? Math.Ceiling(rawTarget / tick) : Math.Floor(rawTarget / tick)) * tick;
            var reason = risk <= 0 ? "gap_fill_through_structural_stop" : intent.OriginalTarget <= 0
                ? "actual_fill_three_r_target_not_positive" : intent.EntryNotional > intent.MaximumNotional
                    ? "actual_fill_notional_limit_breach" : null;
            if (reason is not null)
            {
                QueueIntradayLiquidation(intent, at, reason);
                return false;
            }
            var costs = EstimatedIntradayCosts(intent, entry);
            intent.Protection = intent.Protection is { } previous
                ? ContinuationProtectionPolicy.RebaseActualFills(previous, entry, costs)
                : ContinuationProtectionPolicy.Create(intent.Candidate.Ticker,
                    intent.Long ? PlannedOrderSide.Long : PlannedOrderSide.Short,
                    ExecutionEventTime.ToUtc(intent.FirstEntryAt!.Value), entry, intent.Candidate.StopLossPrice,
                    costs, tick, request.Strategies[0].ExitRules.ContinuationProtection!, targetR);
            return true;
        }

        /// <summary>Reuse the existing cost-cover convention with frozen fees and actual weighted entry.</summary>
        private decimal EstimatedIntradayCosts(PositionIntent intent, decimal entry) =>
            entry * (policy.Costs.BaseSlippageBps + request.Portfolio.ImpactBpsAtMaxParticipation) * 2m / 10_000m +
            (request.Portfolio.FixedBuyFee + request.Portfolio.FixedSellFee) / intent.EntryQuantity +
            DeterministicExecutionSimulator.CalculateRegulatoryFees(intent.EntryNotional, intent.EntryQuantity,
                request.Portfolio.SecFeeRate, request.Portfolio.FinraTafPerShare, request.Portfolio.FinraTafCap) / intent.EntryQuantity;

        /// <summary>A triggered stop remains a market reduction; continuation cannot turn it back into a dormant stop.</summary>
        private bool CurrentStopTriggered(PositionIntent intent) => intent.CurrentStopId is { } id &&
            state.Book.WorkingOrders.TryGetValue(id, out var stop) && stop.StopTriggered;

        /// <summary>Replace the paired protection from known owned shares; revisions keep one fixed exit-fee group.</summary>
        private void QueueIntradayProtection(PositionIntent intent, long at)
        {
            if (intent.Protection is null || intent.OpenQuantity <= 0 || intent.ExitSubmitted || CurrentStopTriggered(intent)) return;
            QueueCancel(intent, intent.CurrentStopId, at);
            QueueCancel(intent, intent.CurrentTargetId, at);
            intent.ProtectionRevision++;
            intent.CurrentStopId = intent.StopId + ":" + intent.ProtectionRevision;
            intent.CurrentTargetId = intent.TargetId + ":" + intent.ProtectionRevision;
            foreach (var (id, type, price) in new[]
            {
                (intent.CurrentStopId, SimulatedOrderType.Stop, intent.Protection.StopPrice),
                (intent.CurrentTargetId, SimulatedOrderType.Limit, intent.Protection.TargetPrice)
            })
            {
                orderOwners[id] = intent;
                Add(intent.Candidate.Ticker, new(new(ExecutionCommandKind.Submit, id, intent.Candidate.Ticker,
                    intent.Long ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy, type, intent.OpenQuantity,
                    ExecutionEventTime.ToUtc(at), sequence++, true, SimulatedTimeInForce.GoodTillCanceled,
                    LimitPrice: type == SimulatedOrderType.Limit ? price : null,
                    StopPrice: type == SimulatedOrderType.Stop ? price : null,
                    FixedFeeGroupId: intent.Candidate.CandidateId + ":exit-fee", OcoGroupId: intent.Candidate.CandidateId),
                    at, intent.EntryId, IsProtection: true));
            }
        }

        /// <summary>Cancel only known working orders; repeated observations cannot recreate an already issued cancellation.</summary>
        private void QueueCancel(PositionIntent intent, string? id, long at)
        {
            if (id is null) return;
            // A not-yet-routed revision can be withdrawn without inventing a broker cancel.
            if (pending.TryGetValue(intent.Candidate.Ticker, out var queued))
            {
                queued.RemoveAll(command => command.Command.ClientOrderId == id &&
                    command.Command.Kind == ExecutionCommandKind.Submit);
                if (queued.Count == 0) pending.Remove(intent.Candidate.Ticker);
            }
            if (!state.Book.WorkingOrders.TryGetValue(id, out var order) ||
                pending.GetValueOrDefault(intent.Candidate.Ticker)?.Any(command => command.Command.TargetClientOrderId == id) == true) return;
            Add(intent.Candidate.Ticker, new(new(ExecutionCommandKind.Cancel, id + ":cancel:" + sequence,
                intent.Candidate.Ticker, order.Command.Side, order.Command.OrderType, 0,
                ExecutionEventTime.ToUtc(at), sequence++, true, TargetClientOrderId: id), at));
        }

        /// <summary>Cancel unfilled entry/protection before submitting a received-position market reduction.</summary>
        private void QueueIntradayLiquidation(PositionIntent intent, long at, string reason)
        {
            if (intent.ExitSubmitted) return;
            QueueCancel(intent, intent.EntryId, at);
            QueueCancel(intent, intent.CurrentStopId, at);
            QueueCancel(intent, intent.CurrentTargetId, at);
            if (intent.OpenQuantity > 0)
                Add(intent.Candidate.Ticker, new(new(ExecutionCommandKind.Submit, intent.ExitId, intent.Candidate.Ticker,
                    intent.Long ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy, SimulatedOrderType.Market,
                    intent.OpenQuantity, ExecutionEventTime.ToUtc(at), sequence++, true,
                    SimulatedTimeInForce.GoodTillCanceled, FixedFeeGroupId: intent.Candidate.CandidateId + ":exit-fee"), at, intent.EntryId));
            intent.ExitSubmitted = true;
            intent.ExitReason = reason;
        }
    }
}
