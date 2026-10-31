using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Execution;
using System.Text.Json;

namespace TradingFlow.Backtesting;

/// <summary>
/// Owns one immutable execution book for an entire portfolio replay. Candidate
/// decisions are submitted at their point-in-time entry timestamps; fills then
/// consume the real volume of each chronological market bar exactly once.
/// </summary>
internal sealed class ChronologicalBacktestExecutionCoordinator
{
    private readonly PortfolioConfig _portfolio;
    private readonly IDeterministicExecutionSimulator _simulator;
    private readonly TimelineBar[] _timeline;
    private readonly HashSet<MarketBarIdentity> _marketBarIdentities;
    private readonly HashSet<MarketBarIdentity> _duplicateBarIdentities;
    private readonly List<ExecutionTracker> _active = [];
    private readonly List<BacktestTrade> _completed = [];
    private readonly List<BacktestExecutionFailure> _failures = [];
    private readonly List<ExecutionTracker> _scheduled = [];
    private readonly List<BacktestPortfolioFill> _fills = [];
    private readonly Dictionary<string, TimelineBar> _lastBarsBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private ExecutionBookState _state = ExecutionBookState.Empty;
    private int _nextTimelineIndex;
    private long _nextCommandSequence;
    private readonly Dictionary<string, long> _sourceSequences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownIdentities = new(StringComparer.Ordinal);
    private readonly IExecutionEventSink? _executionEvidence;

    public ChronologicalBacktestExecutionCoordinator(
        PortfolioConfig portfolio,
        IReadOnlyCollection<OhlcvBar> marketBars,
        IDeterministicExecutionSimulator? simulator = null,
        IExecutionEventSink? executionEvidence = null)
    {
        _portfolio = portfolio;
        _simulator = simulator ?? new DeterministicExecutionSimulator();
        _executionEvidence = executionEvidence;
        _duplicateBarIdentities = marketBars.GroupBy(bar => new MarketBarIdentity(
            NormalizeSymbol(bar.Ticker), bar.Timeframe.Trim().ToLowerInvariant(), bar.Timestamp.ToUniversalTime()))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        _timeline = BuildTimeline(marketBars);
        _marketBarIdentities = marketBars
            .Select(bar => new MarketBarIdentity(
                NormalizeSymbol(bar.Ticker),
                bar.Timeframe.Trim().ToLowerInvariant(),
                bar.Timestamp.ToUniversalTime()))
            .ToHashSet();
    }

    public IReadOnlyList<BacktestTrade> CompletedTrades => _completed;

    public IReadOnlyList<BacktestExecutionFailure> Failures => _failures;

    public IReadOnlyList<ActiveBacktestExecution> ActiveExecutions => _active
        .Where(tracker => !tracker.IsTerminal)
        .Select(tracker => new ActiveBacktestExecution(
            tracker.Plan.Candidate.Ticker,
            tracker.Plan.Candidate.StrategyName,
            tracker.Plan.Strategy.StrategyId,
            tracker.Plan.Candidate.EntryTimestamp,
            tracker.Plan.RequestedQuantity,
            tracker.Plan.RequestedQuantity * (tracker.Plan.Candidate.PlanningReferencePrice ?? tracker.Plan.Candidate.EntryPrice)))
        .ToArray();

    public void AdvanceThrough(DateTimeOffset timestamp)
    {
        var cutoff = timestamp.ToUniversalTime();
        if (_active.Count == 0)
        {
            _nextTimelineIndex = FindFirstTimelineBarAfter(cutoff, _nextTimelineIndex);
            return;
        }

        while (_nextTimelineIndex < _timeline.Length && _timeline[_nextTimelineIndex].AvailableAtUtc <= cutoff)
        {
            if (_failures.Count > 0) return;
            AdvanceBar(_timeline[_nextTimelineIndex++]);
        }
    }

    public bool TrySchedule(ChronologicalExecutionPlan plan, out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (String.IsNullOrWhiteSpace(plan.Candidate.CandidateId))
        {
            rejectionReason = "candidate_id_missing";
            return false;
        }

        var entryTimestamp = plan.Candidate.EntryTimestamp.ToUniversalTime();
        if (plan.Candidate.ExitTimestamp <= plan.Candidate.EntryTimestamp)
        {
            rejectionReason = "same_bar_entry_exit_is_ambiguous";
            return false;
        }

        if (plan.Candidate.ExecutionReplay is { } replay &&
            (replay.PolicyId != ExecutionReplayPolicy.DailyRthFiveMinuteV1 || replay.RequiredSliceStarts.Count == 0 ||
             replay.RequiredSliceStarts[0] != entryTimestamp))
            throw new InvalidOperationException("Unknown or mismatched execution replay policy.");
        if (!HasExecutionBar(plan.Candidate.Ticker, plan.ExecutionTimeframe, entryTimestamp))
        {
            if (plan.Candidate.ExecutionReplay is not null)
                Fail(new ExecutionTracker(plan.Candidate.CandidateId, plan,
                    $"entry-{plan.Candidate.CandidateId}", $"exit-{plan.Candidate.CandidateId}"), "entry_execution_bar_missing");
            rejectionReason = "entry_execution_bar_missing";
            return false;
        }

        var identity = plan.Candidate.CandidateId;
        var slippageBps = Math.Max(0m, plan.Strategy.Execution.SlippageBps);
        var hasMixedExecutionPolicy = _active.Any(tracker =>
            !tracker.IsTerminal &&
            tracker.Plan.Candidate.Ticker.Equals(plan.Candidate.Ticker, StringComparison.OrdinalIgnoreCase) &&
            Math.Max(0m, tracker.Plan.Strategy.Execution.SlippageBps) != slippageBps);
        if (hasMixedExecutionPolicy)
        {
            rejectionReason = "mixed_execution_slippage_policy_requires_separate_run";
            return false;
        }

        var hasMixedTimeframeExposure = _active.Any(tracker =>
            !tracker.IsTerminal &&
            tracker.Plan.Candidate.Ticker.Equals(plan.Candidate.Ticker, StringComparison.OrdinalIgnoreCase) &&
            !tracker.Plan.ExecutionTimeframe.Equals(
                plan.ExecutionTimeframe,
                StringComparison.OrdinalIgnoreCase));
        if (hasMixedTimeframeExposure)
        {
            rejectionReason = "mixed_execution_timeframe_requires_explicit_normalization";
            return false;
        }

        if (_knownIdentities.Contains(identity))
        {
            rejectionReason = "duplicate_execution_candidate";
            return false;
        }

        if (_portfolio.MaxExecutionOrderHistory <= 0 ||
            _knownIdentities.Count >= _portfolio.MaxExecutionOrderHistory)
        {
            throw new InvalidOperationException(
                $"Execution candidates exceed portfolio.max_execution_order_history ({_portfolio.MaxExecutionOrderHistory:N0}).");
        }
        _knownIdentities.Add(identity);

        var tracker = new ExecutionTracker(
            identity,
            plan,
            $"entry-{identity}",
            $"exit-{identity}");
        _active.Add(tracker);
        _scheduled.Add(tracker);
        rejectionReason = null;
        return true;
    }

    // A bounded study can retain known open state; the original unbounded completion still requires liquidation.
    public void Complete(DateTimeOffset? cutoffUtc = null)
    {
        if (_active.Count == 0)
        {
            return;
        }

        while (_nextTimelineIndex < _timeline.Length)
        {
            if (cutoffUtc is { } cutoff && _timeline[_nextTimelineIndex].AvailableAtUtc > cutoff) break;
            if (_failures.Count > 0) return;
            AdvanceBar(_timeline[_nextTimelineIndex++]);
        }

        foreach (var tracker in _active.Where(item => !item.IsTerminal).ToArray())
        {
            if (cutoffUtc is not null && tracker.Plan.Candidate.ExecutionReplay is null) continue;
            Fail(tracker, tracker.EntryFilledQuantity == 0
                ? "entry_not_filled_before_end_of_data"
                : "exit_not_filled_before_end_of_data");
        }

        _active.RemoveAll(item => item.IsTerminal && !HasWorkingOrders(item));
    }

    // Export the admitted book without mutating or cancelling its still-working orders.
    public BacktestPortfolioState Snapshot(DateTimeOffset cutoffUtc)
    {
        var positions = _scheduled.Where(t => t.EntryFilledQuantity > t.ExitFilledQuantity).Select(t =>
        {
            var candidate = t.Plan.Candidate;
            var quantity = (t.EntryFilledQuantity - t.ExitFilledQuantity) * (t.IsShort ? -1 : 1);
            var basis = t.EntryFilledNotional / t.EntryFilledQuantity;
            _lastBarsBySymbol.TryGetValue(NormalizeSymbol(candidate.Ticker), out var mark);
            return new BacktestOpenPosition(candidate.CandidateId, candidate.StrategyId, candidate.Ticker,
                quantity, basis, mark?.Bar.Close, mark?.AvailableAtUtc,
                mark is null ? null : quantity * (mark.Bar.Close - basis));
        }).ToArray();
        var orders = _state.WorkingOrders.Values.Select(order =>
        {
            var owner = _active.Single(t => t.OwnsOrder(order.Command.ClientOrderId));
            var c = owner.Plan.Candidate;
            var entry = order.Command.ClientOrderId == owner.EntryClientOrderId;
            return new BacktestPendingOrder(c.CandidateId, c.StrategyId, c.Ticker, order.Command.ClientOrderId,
                order.Command.Side.ToString(), order.Command.OrderType.ToString(), order.RemainingQuantity,
                order.FilledQuantity == 0 ? "working" : "partially_filled", entry ? order.RemainingQuantity * c.EntryPrice : 0,
                order.Command.KnownAtUtc);
        }).ToArray();
        var pending = _scheduled.Where(t => !t.IsTerminal && t.EntryFilledQuantity > t.ExitFilledQuantity &&
                t.Plan.Candidate.PendingExitDecision is { } exit && exit.KnownAtUtc <= cutoffUtc)
            .Select(t => new BacktestPendingExit(t.Identity, t.Plan.Candidate.StrategyId, t.Plan.Candidate.Ticker,
                t.Plan.Candidate.PendingExitDecision!.KnownAtUtc, t.Plan.Candidate.PendingExitDecision.Reason)).ToArray();
        var snapshot = new BacktestPortfolioState(NativeDailySessionLedger.ValuationBasis, cutoffUtc.ToUniversalTime(), _scheduled.Count,
            _scheduled.Count(t => t.EntryFilledQuantity > 0), _completed.Count, _fills.ToArray(), positions, orders, pending, [])
        {
            AdmittedOrders = _scheduled.Select(t => new BacktestAdmittedOrder(t.Identity, t.Plan.Candidate.StrategyId,
                t.Plan.Candidate.Ticker, t.Plan.RequestedQuantity, t.Plan.Candidate.EntryPrice,
                t.Plan.Candidate.Direction, t.Plan.Candidate.EntryTimestamp)
                { DecisionStrategyId = t.Plan.Candidate.DecisionStrategyId }).ToArray()
        };
        AppendCutoffEvidence(snapshot);
        return snapshot;
    }

    // Retain actual command state independently of later result projection for artifact reconciliation.
    private void AppendCutoffEvidence(BacktestPortfolioState snapshot)
    {
        var pendingOrders = snapshot.PendingOrders.ToLookup(p => p.CandidateId, StringComparer.Ordinal);
        var pendingExits = snapshot.PendingExits.ToLookup(p => p.CandidateId, StringComparer.Ordinal);
        var quantities = snapshot.OpenPositions.ToDictionary(p => p.CandidateId, p => p.SignedQuantity, StringComparer.Ordinal);
        for (var index = 0; index < snapshot.AdmittedOrders.Count; index++)
        {
            var order = snapshot.AdmittedOrders[index];
            var candidate = _scheduled[index].Plan.Candidate;
            _executionEvidence?.Append(new ExecutionEvent(order.Ticker, candidate.StrategyName, snapshot.CutoffUtc,
                ExecutionState.OrderStateChanged, "Known native book state at study cutoff", "portfolio_execution",
                order.CandidateId, JsonSerializer.Serialize(new
                {
                    snapshot.CutoffUtc,
                    AdmittedOrder = order,
                    PendingOrders = pendingOrders[order.CandidateId].ToArray(),
                    PendingExits = pendingExits[order.CandidateId].ToArray(),
                    TerminalSignedQuantity = quantities.GetValueOrDefault(order.CandidateId)
                })));
        }
    }

    private bool HasWorkingOrders(ExecutionTracker tracker) => _state.WorkingOrders.Keys.Any(tracker.OwnsOrder);

    private void AdvanceBar(TimelineBar timelineBar)
    {
        foreach (var active in _active.Where(t => !t.IsTerminal && t.Plan.Candidate.ExecutionReplay is not null).ToArray())
        {
            var required = active.Plan.Candidate.ExecutionReplay!.RequiredSliceStarts;
            if (active.NextRequiredSlice < required.Count)
            {
                var expected = required[active.NextRequiredSlice];
                if (expected.AddMinutes(5) <= timelineBar.AvailableAtUtc &&
                    !HasExecutionBar(active.Plan.Candidate.Ticker, ExecutionReplayPolicy.Timeframe, expected))
                {
                    Fail(active, $"required_execution_slice_missing:{expected:O}");
                    return;
                }
            }
        }
        var symbol = NormalizeSymbol(timelineBar.Bar.Ticker);
        var trackers = _active
            .Where(tracker => !tracker.IsTerminal || HasWorkingOrders(tracker))
            .Where(tracker => NormalizeSymbol(tracker.Plan.Candidate.Ticker).Equals(
                symbol,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (trackers.Length == 0 || !IsExecutionTimeframe(timelineBar, trackers))
        {
            return;
        }

        // A fine-policy order requires every authoritative regular-session slice
        // while active. Never turn a gap into a no-touch observation.
        var fineTrackers = trackers.Where(t => !t.IsTerminal && t.Plan.Candidate.ExecutionReplay is not null).ToArray();
        foreach (var tracker in fineTrackers)
        {
            var required = tracker.Plan.Candidate.ExecutionReplay!.RequiredSliceStarts;
            if (tracker.NextRequiredSlice >= required.Count) { Fail(tracker, "execution_past_terminal_boundary"); return; }
            var expected = required[tracker.NextRequiredSlice];
            if (timelineBar.StartUtc < expected) return; // extended hours, never executable under this policy
            if (timelineBar.StartUtc != expected) { Fail(tracker, $"required_execution_slice_missing:{expected:O}"); return; }
            var observed = timelineBar.Bar;
            if (observed.Volume <= 0 || observed.Low <= 0 || observed.Low > observed.High ||
                observed.Open < observed.Low || observed.Open > observed.High ||
                observed.Close < observed.Low || observed.Close > observed.High ||
                _duplicateBarIdentities.Contains(new MarketBarIdentity(symbol, ExecutionReplayPolicy.Timeframe, expected)))
            { Fail(tracker, $"required_execution_slice_invalid:{expected:O}"); return; }
            if (timelineBar.AvailableAtUtc != timelineBar.EndUtc)
            { Fail(tracker, "delayed_execution_evidence_unsupported"); return; }
            tracker.NextRequiredSlice++;
        }
        var bar = timelineBar.Bar;
        var terminalOpen = trackers.Any(tracker => !tracker.IsTerminal && tracker.Plan.Candidate.ExitReason == "scheduled_study_exit" &&
            tracker.Plan.Candidate.ExitTimestamp?.ToUniversalTime() == timelineBar.StartUtc) ||
            fineTrackers.Any(tracker => tracker.Plan.Candidate.ExecutionReplay!.TerminalOpenUtc == timelineBar.StartUtc);
        if (terminalOpen)
        {
            if (!_lastBarsBySymbol.TryGetValue(symbol, out var previous) || previous.AvailableAtUtc > timelineBar.StartUtc || previous.Bar.Volume <= 0)
            {
                foreach (var tracker in trackers) Fail(tracker, "terminal_open_prior_liquidity_unavailable");
                _active.RemoveAll(item=>item.IsTerminal);
                return;
            }
            // Opening-point execution proxy: final HL/C/volume are unavailable to this scheduled action.
            // Capacity uses the prior completed slice, not hindsight final-session volume.
            bar = bar with { High = bar.Open, Low = bar.Open, Close = bar.Open, Volume = previous.Bar.Volume };
        }
        _lastBarsBySymbol[symbol] = timelineBar;
        var commands = BuildCommands(timelineBar.StartUtc, trackers);
        var hasWorkingOrder = _state.WorkingOrders.Values.Any(order =>
            NormalizeSymbol(order.Command.Symbol).Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (commands.Count == 0 && !hasWorkingOrder)
        {
            return;
        }

        var sequence = _sourceSequences.TryGetValue(symbol, out var priorSequence)
            ? priorSequence + 1
            : 0;
        _sourceSequences[symbol] = sequence;

            var executionSlippagePolicies = trackers
                .Select(tracker => Math.Max(0m, tracker.Plan.Strategy.Execution.SlippageBps))
                .Distinct()
                .ToArray();
            if (executionSlippagePolicies.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Symbol '{symbol}' has mixed execution slippage policies in one market slice.");
            }

            var baseSlippageBps = executionSlippagePolicies[0];
            var policy = new ExecutionSimulationPolicy(
                _portfolio.MaxBarParticipationPct,
                baseSlippageBps,
                SyntheticSpreadBps: 0m,
                _portfolio.ImpactBpsAtMaxParticipation,
                _portfolio.FixedBuyFee,
                _portfolio.FixedSellFee,
                _portfolio.SecFeeRate,
                _portfolio.FinraTafPerShare,
                _portfolio.FinraTafCap,
                InputPriceIncludesExecutionFriction: baseSlippageBps == 0m,
                MaximumTrackedOrderContracts: _portfolio.MaxExecutionOrderHistory);
            var step = _simulator.Advance(new ExecutionStepRequest(
                _state,
                new ExecutionMarketSlice(
                    symbol,
                    timelineBar.StartUtc,
                    timelineBar.EndUtc,
                    timelineBar.AvailableAtUtc,
                    sequence,
                    IsCompleted: true,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume),
                commands,
                policy));
            _state = step.State;
            AppendExecutionEvidence(step.Events);
            ApplyEvents(step.Events, bar);
            FinalizeTerminalTrackers();
            foreach (var tracker in fineTrackers.Where(t => !t.IsTerminal))
            {
                if (tracker.EntrySubmitted && tracker.EntryFilledQuantity == 0)
                    tracker.IsTerminal = true; // IOC non-fill is a recorded observation, not an open position
                else if (timelineBar.StartUtc == tracker.Plan.Candidate.ExecutionReplay!.TerminalOpenUtc)
                    Fail(tracker, "terminal_open_quantity_unresolved");
            }
        _active.RemoveAll(item => item.IsTerminal && !HasWorkingOrders(item));
    }

    private void AppendExecutionEvidence(IReadOnlyCollection<SimulatedExecutionEvent> events)
    {
        if (_executionEvidence is null) return;
        foreach (var item in events)
        {
            var tracker = _active.FirstOrDefault(value => value.OwnsOrder(item.ClientOrderId));
            if (tracker is null)
                throw new InvalidOperationException($"Execution event references unknown order '{item.ClientOrderId}'.");
            _executionEvidence.Append(new ExecutionEvent(
                item.Symbol,
                tracker.Plan.Candidate.StrategyName,
                item.OccurredAtUtc,
                item.LastFillQuantity > 0 ? ExecutionState.OrderFilled : ExecutionState.OrderStateChanged,
                $"Portfolio simulator order {item.ClientOrderId} is {item.State}: {item.Reason}.",
                "portfolio_execution",
                tracker.Plan.Candidate.CandidateId,
                JsonSerializer.Serialize(item)));
        }
    }

    private List<SimulatedOrderCommand> BuildCommands(
        DateTimeOffset timestamp,
        IReadOnlyCollection<ExecutionTracker> trackers)
    {
        var commands = new List<SimulatedOrderCommand>();
        foreach (var tracker in trackers
                     .OrderBy(item => item.Plan.Candidate.EntryTimestamp)
                     .ThenBy(item => item.Identity, StringComparer.Ordinal))
        {
            if (tracker.IsTerminal)
            {
                // A flattened trade retains ownership until every remaining order is cancelled.
                // Cancellation precedes any new entry on the next source slice and consumes no liquidity.
                foreach (var order in _state.WorkingOrders.Where(order => tracker.OwnsOrder(order.Key)))
                    commands.Add(new SimulatedOrderCommand(ExecutionCommandKind.Cancel,
                        $"retire-{order.Key}-{_nextCommandSequence}",order.Value.Command.Symbol,order.Value.Command.Side,
                        order.Value.Command.OrderType,0,timestamp,_nextCommandSequence++,true,TargetClientOrderId:order.Key));
                continue;
            }

            if (!tracker.EntrySubmitted && tracker.Plan.Candidate.EntryTimestamp.ToUniversalTime() <= timestamp)
            {
                tracker.EntrySubmitted = true;
                commands.Add(new SimulatedOrderCommand(
                    ExecutionCommandKind.Submit,
                    tracker.EntryClientOrderId,
                    tracker.Plan.Candidate.Ticker,
                    tracker.IsShort ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy,
                    SimulatedOrderType.Market,
                    tracker.Plan.RequestedQuantity,
                    tracker.Plan.Candidate.EntryTimestamp.ToUniversalTime(),
                    _nextCommandSequence++,
                    IsRiskReducing: false,
                    tracker.Plan.Candidate.ExecutionReplay is null ? SimulatedTimeInForce.Day : SimulatedTimeInForce.ImmediateOrCancel,
                    FixedFeeGroupId: $"entry-fee-{tracker.Identity}"));
            }

            var priceTriggeredExitIsDue = IsPriceTriggeredExit(tracker.Plan.Candidate.ExitReason) &&
                tracker.Plan.Candidate.ExitTimestamp?.ToUniversalTime() <= timestamp;
            if (priceTriggeredExitIsDue && _state.WorkingOrders.ContainsKey(tracker.EntryClientOrderId))
            {
                commands.Add(new SimulatedOrderCommand(
                    ExecutionCommandKind.Cancel,
                    $"cancel-{tracker.EntryClientOrderId}",
                    tracker.Plan.Candidate.Ticker,
                    tracker.IsShort ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy,
                    SimulatedOrderType.Market,
                    0,
                    timestamp,
                    _nextCommandSequence++,
                    IsRiskReducing: false,
                    TargetClientOrderId: tracker.EntryClientOrderId));
                if (tracker.EntryFilledQuantity == 0)
                {
                    tracker.ExitSubmitted = true;
                    Fail(tracker, "entry_not_filled_before_planned_exit");
                    continue;
                }
            }

            if (tracker.MarketExitSubmitted)
            {
                CancelProtectiveOrders(timestamp, tracker, commands);
                continue;
            }
            if (IsPriceTriggeredExit(tracker.Plan.Candidate.ExitReason) ||
                tracker.Plan.Candidate.ExitTimestamp is null ||
                tracker.Plan.Candidate.ExitTimestamp.Value.ToUniversalTime() > timestamp)
            {
                AddProtectiveOrderCommands(timestamp, tracker, commands);
                continue;
            }


            CancelProtectiveOrders(timestamp, tracker, commands);

            if (_state.WorkingOrders.ContainsKey(tracker.EntryClientOrderId))
            {
                commands.Add(new SimulatedOrderCommand(
                    ExecutionCommandKind.Cancel,
                    $"cancel-{tracker.EntryClientOrderId}",
                    tracker.Plan.Candidate.Ticker,
                    tracker.IsShort ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy,
                    SimulatedOrderType.Market,
                    0,
                    tracker.Plan.Candidate.ExitTimestamp.Value.ToUniversalTime(),
                    _nextCommandSequence++,
                    IsRiskReducing: false,
                    TargetClientOrderId: tracker.EntryClientOrderId));
            }

            tracker.ExitSubmitted = true;
            tracker.MarketExitSubmitted = true;
            if (tracker.EntryFilledQuantity == 0)
            {
                Fail(tracker, "entry_not_filled_before_planned_exit");
                continue;
            }

            commands.Add(new SimulatedOrderCommand(
                ExecutionCommandKind.Submit,
                tracker.ExitClientOrderId,
                tracker.Plan.Candidate.Ticker,
                tracker.IsShort ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell,
                SimulatedOrderType.Market,
                tracker.EntryFilledQuantity - tracker.ExitFilledQuantity,
                tracker.Plan.Candidate.ExitTimestamp.Value.ToUniversalTime(),
                _nextCommandSequence++,
                IsRiskReducing: true,
                SimulatedTimeInForce.Day,
                FixedFeeGroupId: $"exit-fee-{tracker.Identity}", OcoGroupId: tracker.Identity));
        }

        return commands;
    }

    /// <summary>
    /// Installs stop and target orders only after an entry fill has become known.
    /// Commands created at a bar boundary cannot consume the bar that produced the
    /// fill, so same-bar high/low information can never manufacture an exit.
    /// </summary>
    private void AddProtectiveOrderCommands(
        DateTimeOffset timestamp,
        ExecutionTracker tracker,
        ICollection<SimulatedOrderCommand> commands)
    {
        var openQuantity = tracker.EntryFilledQuantity - tracker.ExitFilledQuantity;
        if (openQuantity <= 0)
        {
            return;
        }

        var exitSide = tracker.IsShort ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell;
        var stopPrice = ResolveEffectiveStopPrice(tracker, timestamp);
        UpsertProtectiveOrder(
            tracker,
            ProtectiveOrderKind.Stop,
            timestamp,
            exitSide,
            SimulatedOrderType.Stop,
            openQuantity,
            stopPrice: stopPrice,
            commands: commands);
        UpsertProtectiveOrder(
            tracker,
            ProtectiveOrderKind.Target,
            timestamp,
            exitSide,
            SimulatedOrderType.Limit,
            openQuantity,
            limitPrice: tracker.Plan.Candidate.TakeProfitPrice,
            commands: commands);
    }

    private void UpsertProtectiveOrder(
        ExecutionTracker tracker,
        ProtectiveOrderKind kind,
        DateTimeOffset timestamp,
        SimulatedOrderSide side,
        SimulatedOrderType orderType,
        int openQuantity,
        decimal? limitPrice = null,
        decimal? stopPrice = null,
        ICollection<SimulatedOrderCommand>? commands = null)
    {
        var currentOrderId = tracker.GetProtectiveOrderId(kind);
        if (currentOrderId is not null &&
            _state.WorkingOrders.TryGetValue(currentOrderId, out var current) &&
            current.RemainingQuantity == openQuantity &&
            current.Command.LimitPrice == limitPrice &&
            current.Command.StopPrice == stopPrice)
        {
            return;
        }

        var nextOrderId = tracker.NextProtectiveOrderId(kind);
        var commandKind = currentOrderId is not null && _state.WorkingOrders.ContainsKey(currentOrderId)
            ? ExecutionCommandKind.Replace
            : ExecutionCommandKind.Submit;
        var totalQuantity = openQuantity;
        if (commandKind == ExecutionCommandKind.Replace)
        {
            totalQuantity += _state.WorkingOrders[currentOrderId!].FilledQuantity;
        }

        commands!.Add(new SimulatedOrderCommand(
            commandKind,
            nextOrderId,
            tracker.Plan.Candidate.Ticker,
            side,
            orderType,
            totalQuantity,
            timestamp,
            _nextCommandSequence++,
            IsRiskReducing: true,
            SimulatedTimeInForce.GoodTillCanceled,
            LimitPrice: limitPrice,
            StopPrice: stopPrice,
            TargetClientOrderId: commandKind == ExecutionCommandKind.Replace ? currentOrderId : null,
            FixedFeeGroupId: $"exit-fee-{tracker.Identity}", OcoGroupId: tracker.Identity));
        tracker.SetProtectiveOrderId(kind, nextOrderId);
        if (kind == ProtectiveOrderKind.Stop && stopPrice != tracker.Plan.Candidate.StopLossPrice)
        {
            tracker.MarkTrailingStop(nextOrderId);
        }
    }

    private static decimal ResolveEffectiveStopPrice(ExecutionTracker tracker, DateTimeOffset timestamp)
    {
        var candidate = tracker.Plan.Candidate;
        return candidate.ExitReason.Equals("trailing_stop", StringComparison.OrdinalIgnoreCase) &&
            candidate.ExitTimestamp?.ToUniversalTime() <= timestamp
                ? candidate.ExitPrice ?? throw new InvalidDataException("A price-triggered exit requires its price.")
                : candidate.StopLossPrice;
    }

    private void CancelProtectiveOrders(
        DateTimeOffset timestamp,
        ExecutionTracker tracker,
        ICollection<SimulatedOrderCommand> commands)
    {
        foreach (var orderId in tracker.ProtectiveOrderIds)
        {
            if (!_state.WorkingOrders.TryGetValue(orderId, out var order))
            {
                continue;
            }

            commands.Add(new SimulatedOrderCommand(
                ExecutionCommandKind.Cancel,
                $"cancel-{orderId}-{_nextCommandSequence}",
                tracker.Plan.Candidate.Ticker,
                order.Command.Side,
                order.Command.OrderType,
                0,
                timestamp,
                _nextCommandSequence++,
                IsRiskReducing: true,
                TargetClientOrderId: orderId));
        }
    }

    private static bool IsPriceTriggeredExit(string exitReason) =>
        exitReason.Equals("take_profit", StringComparison.OrdinalIgnoreCase) ||
        exitReason.Equals("trailing_stop", StringComparison.OrdinalIgnoreCase) ||
        exitReason.StartsWith("stop_loss", StringComparison.OrdinalIgnoreCase);

    private void ApplyEvents(IReadOnlyCollection<SimulatedExecutionEvent> events, OhlcvBar bar)
    {
        foreach (var executionEvent in events.Where(item => item.LastFillQuantity > 0))
        {
            var tracker = _active.FirstOrDefault(item => item.OwnsOrder(executionEvent.ClientOrderId));
            if (tracker is null)
            {
                continue;
            }

            if (executionEvent.ClientOrderId.Equals(tracker.EntryClientOrderId, StringComparison.Ordinal))
            {
                tracker.EntryFilledQuantity += executionEvent.LastFillQuantity;
                tracker.EntryFilledNotional += executionEvent.LastFillQuantity * executionEvent.FillPrice!.Value;
                tracker.EntryLiquidityVolume += bar.Volume;
                tracker.FirstEntryFillUtc ??= executionEvent.OccurredAtUtc;
            }
            else
            {
                tracker.RecordExitFill(executionEvent.ClientOrderId);
                tracker.ExitSubmitted = true;
                tracker.ExitFilledQuantity += executionEvent.LastFillQuantity;
                tracker.ExitFilledNotional += executionEvent.LastFillQuantity * executionEvent.FillPrice!.Value;
                tracker.LastExitFillUtc = executionEvent.OccurredAtUtc;
            }

            tracker.ExecutionFees += executionEvent.Fees;
            _fills.Add(new(tracker.Identity, tracker.Plan.Candidate.StrategyId, tracker.Plan.Candidate.Ticker,
                executionEvent.Side == SimulatedOrderSide.Buy,
                executionEvent.ClientOrderId == tracker.EntryClientOrderId, executionEvent.LastFillQuantity,
                executionEvent.FillPrice!.Value, executionEvent.Fees, executionEvent.OccurredAtUtc,
                NativeDailySessionLedger.SourceSessionDate(executionEvent.OccurredAtUtc)));
        }
    }

    private void FinalizeTerminalTrackers()
    {
        foreach (var tracker in _active
                     .Where(item => !item.IsTerminal)
                     .Where(item => item.ExitSubmitted &&
                         item.EntryFilledQuantity > 0 &&
                         item.ExitFilledQuantity == item.EntryFilledQuantity)
                     .OrderBy(item => item.Identity, StringComparer.Ordinal)
                     .ToArray())
        {
            var entryPrice = tracker.EntryFilledNotional / tracker.EntryFilledQuantity;
            var exitPrice = tracker.ExitFilledNotional / tracker.ExitFilledQuantity;
            var grossProfit = tracker.IsShort
                ? (entryPrice - exitPrice) * tracker.EntryFilledQuantity
                : (exitPrice - entryPrice) * tracker.EntryFilledQuantity;
            var participation = tracker.EntryLiquidityVolume > 0m
                ? tracker.EntryFilledQuantity / tracker.EntryLiquidityVolume * 100m
                : 0m;
            var candidate = tracker.Plan.Candidate;
            var exitReason = tracker.FilledExitReason ?? candidate.ExitReason;

            tracker.IsTerminal = true;
            _completed.Add(new BacktestTrade(
                candidate.Ticker,
                candidate.StrategyName,
                candidate.Direction,
                tracker.FirstEntryFillUtc ?? candidate.EntryTimestamp.ToUniversalTime(),
                tracker.LastExitFillUtc ?? candidate.ExitTimestamp?.ToUniversalTime() ?? throw new InvalidDataException("A completed position requires an actual exit fill."),
                tracker.EntryFilledQuantity,
                Decimal.Round(entryPrice, 6),
                Decimal.Round(exitPrice, 6),
                candidate.StopLossPrice,
                candidate.TakeProfitPrice,
                exitReason,
                Decimal.Round(grossProfit, 4),
                Decimal.Round(tracker.ExecutionFees, 4),
                Decimal.Round(grossProfit - tracker.ExecutionFees, 4),
                tracker.Plan.RequestedQuantity,
                tracker.EntryFilledQuantity == tracker.Plan.RequestedQuantity
                    ? "filled"
                    : "partial_fill_remainder_canceled",
                Decimal.Round(participation, 6),
                candidate.StrategyId,
                candidate.CandidateId));
        }
    }

    private void Fail(ExecutionTracker tracker, string reason)
    {
        if (tracker.IsTerminal)
        {
            return;
        }

        tracker.IsTerminal = true;
        var symbol = NormalizeSymbol(tracker.Plan.Candidate.Ticker);
        _lastBarsBySymbol.TryGetValue(symbol, out var lastBar);
        var openQuantity = tracker.EntryFilledQuantity - tracker.ExitFilledQuantity;
        var signedOpenQuantity = tracker.IsShort ? -openQuantity : openQuantity;
        var failure = new BacktestExecutionFailure(
            tracker.Plan.Candidate.CandidateId,
            tracker.Plan.Candidate.Ticker,
            tracker.Plan.Candidate.StrategyName,
            tracker.Plan.Candidate.StrategyId,
            tracker.Plan.Candidate.Direction,
            tracker.Plan.Candidate.EntryTimestamp,
            reason,
            tracker.Plan.RequestedQuantity,
            tracker.EntryFilledQuantity,
            tracker.ExitFilledQuantity,
            signedOpenQuantity,
            Decimal.Round(tracker.EntryFilledNotional, 6),
            Decimal.Round(tracker.ExitFilledNotional, 6),
            Decimal.Round(tracker.ExecutionFees, 6),
            lastBar?.Bar.Close,
            lastBar?.AvailableAtUtc);
        _failures.Add(failure);
        // Preserve every open exposure when a fine treatment aborts globally.
        if (tracker.Plan.Candidate.ExecutionReplay is not null)
            foreach (var other in _active.Where(item => !item.IsTerminal).ToArray())
                Fail(other, "treatment_aborted_after_missing_or_invalid_execution_evidence");
        _executionEvidence?.Append(new ExecutionEvent(
            failure.Ticker,
            failure.StrategyName,
            failure.LastMarketTimestampUtc ?? DateTimeOffset.UtcNow,
            ExecutionState.OrderStateChanged,
            $"Portfolio execution ended incomplete: {reason}; open signed quantity {signedOpenQuantity}.",
            "portfolio_execution",
            failure.CandidateId,
            JsonSerializer.Serialize(failure)));
    }

    private bool HasExecutionBar(string ticker, string timeframe, DateTimeOffset timestamp) =>
        _marketBarIdentities.Contains(new MarketBarIdentity(
            NormalizeSymbol(ticker),
            timeframe.Trim().ToLowerInvariant(),
            timestamp));

    private int FindFirstTimelineBarAfter(DateTimeOffset cutoff, int minimumIndex)
    {
        var low = minimumIndex;
        var high = _timeline.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_timeline[middle].AvailableAtUtc <= cutoff)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static bool IsExecutionTimeframe(
        TimelineBar bar,
        IReadOnlyCollection<ExecutionTracker> trackers)
    {
        var executionTimeframes = trackers
            .Select(tracker => tracker.Plan.ExecutionTimeframe.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (executionTimeframes.Length != 1)
        {
            throw new InvalidOperationException(
                "A symbol execution slice requires one explicit normalized timeframe.");
        }

        var executionTimeframe = executionTimeframes[0];
        return bar.Bar.Timeframe.Trim().Equals(executionTimeframe, StringComparison.OrdinalIgnoreCase);
    }

    private static TimelineBar[] BuildTimeline(
        IReadOnlyCollection<OhlcvBar> marketBars)
    {
        var timeline = marketBars
            .Where(bar => !String.IsNullOrWhiteSpace(bar.Ticker) && !String.IsNullOrWhiteSpace(bar.Timeframe))
            .GroupBy(bar => new
            {
                Symbol = NormalizeSymbol(bar.Ticker),
                Timeframe = bar.Timeframe.Trim().ToLowerInvariant(),
                Timestamp = bar.Timestamp.ToUniversalTime()
            })
            .Select(group => group
                .OrderByDescending(bar => bar.KnownAtUtc)
                .ThenBy(bar => bar.DataFeed, StringComparer.OrdinalIgnoreCase)
                .First())
            .Select(bar =>
            {
                var startUtc = bar.Timestamp.ToUniversalTime();
                var endUtc = startUtc.Add(TimeframeParser.Parse(bar.Timeframe));
                var knownAtUtc = bar.KnownAtUtc?.ToUniversalTime() ?? endUtc;
                return new TimelineBar(bar, startUtc, endUtc, knownAtUtc > endUtc ? knownAtUtc : endUtc);
            })
            .OrderBy(item => item.AvailableAtUtc)
            .ThenBy(item => item.StartUtc)
            .ThenBy(item => NormalizeSymbol(item.Bar.Ticker), StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => TimeframeParser.Parse(item.Bar.Timeframe))
            .ToArray();

        foreach (var stream in timeline.GroupBy(item => new
                 {
                     Symbol = NormalizeSymbol(item.Bar.Ticker),
                     Timeframe = item.Bar.Timeframe.Trim().ToLowerInvariant()
                 }))
        {
            var lastStartUtc = DateTimeOffset.MinValue;
            foreach (var item in stream)
            {
                if (item.StartUtc <= lastStartUtc)
                {
                    throw new InvalidDataException(
                        $"Market bars for {stream.Key.Symbol} {stream.Key.Timeframe} become available out of event-time order at {item.StartUtc:O}.");
                }

                lastStartUtc = item.StartUtc;
            }
        }

        return timeline;
    }

    private static string NormalizeSymbol(string value) => value.Trim().ToUpperInvariant();

    private sealed record TimelineBar(
        OhlcvBar Bar,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        DateTimeOffset AvailableAtUtc);

    private sealed record MarketBarIdentity(
        string Symbol,
        string Timeframe,
        DateTimeOffset StartUtc);

    private sealed class ExecutionTracker(
        string identity,
        ChronologicalExecutionPlan plan,
        string entryClientOrderId,
        string exitClientOrderId)
    {
        public string Identity { get; } = identity;
        public ChronologicalExecutionPlan Plan { get; } = plan;
        public string EntryClientOrderId { get; } = entryClientOrderId;
        public string ExitClientOrderId { get; } = exitClientOrderId;
        public string? StopClientOrderId { get; private set; }
        public string? TargetClientOrderId { get; private set; }
        public int StopOrderVersion { get; private set; }
        public int TargetOrderVersion { get; private set; }
        private HashSet<string> TrailingStopOrderIds { get; } = new(StringComparer.Ordinal);
        private HashSet<string> StopOrderIds { get; } = new(StringComparer.Ordinal);
        private HashSet<string> TargetOrderIds { get; } = new(StringComparer.Ordinal);
        public bool IsShort => Plan.Candidate.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        public bool EntrySubmitted { get; set; }
        public bool ExitSubmitted { get; set; }
        public bool MarketExitSubmitted { get; set; }
        public int NextRequiredSlice { get; set; }
        public bool IsTerminal { get; set; }
        public int EntryFilledQuantity { get; set; }
        public decimal EntryFilledNotional { get; set; }
        public decimal EntryLiquidityVolume { get; set; }
        public int ExitFilledQuantity { get; set; }
        public decimal ExitFilledNotional { get; set; }
        public decimal ExecutionFees { get; set; }
        public DateTimeOffset? FirstEntryFillUtc { get; set; }
        public DateTimeOffset? LastExitFillUtc { get; set; }
        public string? FilledExitReason { get; private set; }

        public IEnumerable<string> ProtectiveOrderIds
        {
            get
            {
                if (StopClientOrderId is not null)
                {
                    yield return StopClientOrderId;
                }

                if (TargetClientOrderId is not null)
                {
                    yield return TargetClientOrderId;
                }
            }
        }

        public bool OwnsOrder(string clientOrderId)
        {
            if (EntryClientOrderId.Equals(clientOrderId, StringComparison.Ordinal) ||
                ExitClientOrderId.Equals(clientOrderId, StringComparison.Ordinal))
            {
                return true;
            }

            return StopOrderIds.Contains(clientOrderId) ||
                TargetOrderIds.Contains(clientOrderId);
        }

        public string? GetProtectiveOrderId(ProtectiveOrderKind kind) =>
            kind == ProtectiveOrderKind.Stop ? StopClientOrderId : TargetClientOrderId;

        public string NextProtectiveOrderId(ProtectiveOrderKind kind)
        {
            if (kind == ProtectiveOrderKind.Stop)
            {
                return $"stop-{Identity}-{++StopOrderVersion}";
            }

            return $"target-{Identity}-{++TargetOrderVersion}";
        }

        public void SetProtectiveOrderId(ProtectiveOrderKind kind, string orderId)
        {
            if (kind == ProtectiveOrderKind.Stop)
            {
                StopClientOrderId = orderId;
                StopOrderIds.Add(orderId);
            }
            else
            {
                TargetClientOrderId = orderId;
                TargetOrderIds.Add(orderId);
            }
        }

        public void MarkTrailingStop(string orderId) => TrailingStopOrderIds.Add(orderId);

        public void RecordExitFill(string clientOrderId)
        {
            if (StopOrderIds.Contains(clientOrderId))
            {
                FilledExitReason = TrailingStopOrderIds.Contains(clientOrderId)
                    ? "trailing_stop"
                    : "stop_loss";
            }
            else if (TargetOrderIds.Contains(clientOrderId))
            {
                FilledExitReason = "take_profit";
            }
        }
    }

    private enum ProtectiveOrderKind
    {
        Stop,
        Target
    }
}

internal sealed record ChronologicalExecutionPlan(
    BacktestCandidateTrade Candidate,
    StrategyDefinition Strategy,
    int RequestedQuantity)
{
    public string ExecutionTimeframe => Candidate.ExecutionReplay is null
        ? Strategy.Execution.Timeframe : ExecutionReplayPolicy.Timeframe;
}

internal sealed record ActiveBacktestExecution(
    string Ticker,
    string StrategyName,
    string StrategyId,
    DateTimeOffset EntryTimestamp,
    int RequestedQuantity,
    decimal ReservedNotional);
