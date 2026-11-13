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
    private readonly HoldingDataRules? _holdingRules;
    private readonly Dictionary<string, DateOnly> _lastDailySessionBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private int _nextHoldingSession;
    private readonly Dictionary<DateOnly, int> _holdingSessionIndex = [];
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Exit reason of a holding closed at its last real close because its daily bars stopped.</summary>
    public const string DataEndedExitReason = "data_ended_last_close";

    /// <summary>Exit reason of a holding closed at the last close before a split's ex-date.</summary>
    public const string SplitExitReason = "split_exit_before_ex_date";

    // Decision-hour execution (sub-daily fills of daily strategies): session schedules, the unresolved window of each session
    // by (symbol, window start) with its kind, and the published counts of the resting-order and remainder rules.
    private readonly DecisionHourExecution? _decisionHour;
    private readonly Dictionary<(string Symbol, DateTimeOffset Start), string> _windowKinds = [];
    private readonly SortedDictionary<string, int> _decisionHourCounts = new(StringComparer.Ordinal);

    /// <summary>Published decision-hour execution counts (gap and first-half-hour touches, fill-bar exits, cancelled remainders).</summary>
    public IReadOnlyDictionary<string, int> DecisionHourCounts => _decisionHourCounts;

    // holdingRules (research-screen daily runs only): the run's trading sessions and split ex-dates, so a holding is closed at
    // its last real close before a split or when its bars never resume; null keeps the original behaviour exactly.
    // decisionHour: the schedules and symbols of decision-hour candidates, whose unresolved windows (DecisionHourDailyBars)
    // join the bar stream for resting orders; null (every other run) adds nothing.
    public ChronologicalBacktestExecutionCoordinator(
        PortfolioConfig portfolio,
        IReadOnlyCollection<OhlcvBar> marketBars,
        IDeterministicExecutionSimulator? simulator = null,
        IExecutionEventSink? executionEvidence = null,
        HoldingDataRules? holdingRules = null,
        DecisionHourExecution? decisionHour = null)
    {
        _portfolio = portfolio;
        _simulator = simulator ?? new DeterministicExecutionSimulator();
        _executionEvidence = executionEvidence;
        _holdingRules = holdingRules;
        _decisionHour = decisionHour;
        for (var index = 0; index < (holdingRules?.Sessions.Count ?? 0); index++) _holdingSessionIndex[holdingRules!.Sessions[index]] = index;
        if (holdingRules is { Sessions.Count: > 0 })
            foreach (var bar in marketBars.Where(bar => bar.Timeframe.Trim().Equals("1d", StringComparison.OrdinalIgnoreCase)))
            {
                var (symbol, session) = (NormalizeSymbol(bar.Ticker), SessionOf(bar.Timestamp));
                if (session <= holdingRules.Sessions[^1] && (!_lastDailySessionBySymbol.TryGetValue(symbol, out var known) || known < session))
                    _lastDailySessionBySymbol[symbol] = session;
            }
        _duplicateBarIdentities = marketBars.GroupBy(bar => new MarketBarIdentity(
            NormalizeSymbol(bar.Ticker), bar.Timeframe.Trim().ToLowerInvariant(), bar.Timestamp.ToUniversalTime()))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        _timeline = BuildTimeline(marketBars, UnresolvedWindows(marketBars));
        _marketBarIdentities = marketBars
            .Select(bar => new MarketBarIdentity(
                NormalizeSymbol(bar.Ticker),
                bar.Timeframe.Trim().ToLowerInvariant(),
                bar.Timestamp.ToUniversalTime()))
            .ToHashSet();
    }

    public IReadOnlyList<BacktestTrade> CompletedTrades => _completed;

    public IReadOnlyList<BacktestExecutionFailure> Failures => _failures;

    /// <summary>
    /// The live positions as admission sees them: planned cost, the planned exit (for same-open swaps) and the close of the
    /// latest bar processed since this position's own entry bar (for marked equity); no close yet means none is reported.
    /// </summary>
    public IReadOnlyList<ActiveBacktestExecution> ActiveExecutions => _active
        .Where(tracker => !tracker.IsTerminal)
        .Select(tracker => new ActiveBacktestExecution(
            tracker.Plan.Candidate.Ticker,
            tracker.Plan.Candidate.StrategyName,
            tracker.Plan.Strategy.StrategyId,
            tracker.Plan.Candidate.EntryTimestamp,
            tracker.Plan.RequestedQuantity,
            tracker.Plan.RequestedQuantity * (tracker.Plan.Candidate.PlanningReferencePrice ?? tracker.Plan.Candidate.EntryPrice))
        {
            PlannedExitTimestamp = tracker.Plan.Candidate.ExitTimestamp,
            PlannedExitReason = tracker.Plan.Candidate.ExitReason,
            LastClose = CloseSinceEntry(tracker)
        })
        .ToArray();

    // The latest processed close of the tracker's ticker, only when that bar is its entry bar or later: a fund bought again
    // must never be marked at the close left over from an earlier holding.
    private decimal? CloseSinceEntry(ExecutionTracker tracker) =>
        _lastBarsBySymbol.TryGetValue(NormalizeSymbol(tracker.Plan.Candidate.Ticker), out var last) &&
        last.StartUtc >= tracker.Plan.Candidate.EntryTimestamp.ToUniversalTime()
            ? last.Bar.Close
            : null;

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
            ForceExitsThrough(_timeline[_nextTimelineIndex].AvailableAtUtc, inclusive: false);
            AdvanceBar(_timeline[_nextTimelineIndex++]);
        }
        ForceExitsThrough(cutoff, inclusive: true);
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
            ForceExitsThrough(_timeline[_nextTimelineIndex].AvailableAtUtc, inclusive: false);
            AdvanceBar(_timeline[_nextTimelineIndex++]);
        }
        ForceExitsThrough(DateTimeOffset.MaxValue, inclusive: true);

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
        SellBeforeSplitAtLastHour(symbol, trackers, timelineBar);
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

            var policy = SlicePolicy(symbol, trackers);
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
            if (_windowKinds.TryGetValue((symbol, timelineBar.StartUtc), out var windowKind))
                CountProtectiveFills(step.Events, windowKind, bar.Open);
            FinalizeTerminalTrackers();
            CheckFillBarProtection(symbol, trackers, timelineBar);
            foreach (var tracker in fineTrackers.Where(t => !t.IsTerminal))
            {
                if (tracker.EntrySubmitted && tracker.EntryFilledQuantity == 0)
                    tracker.IsTerminal = true; // IOC non-fill is a recorded observation, not an open position
                else if (timelineBar.StartUtc == tracker.Plan.Candidate.ExecutionReplay!.TerminalOpenUtc)
                    Fail(tracker, "terminal_open_quantity_unresolved");
            }
        _active.RemoveAll(item => item.IsTerminal && !HasWorkingOrders(item));
    }

    // Decision-hour holdings whose entry first filled on this bar: their stop and target are placed just after the fill and
    // checked against the rest of the same bar (the simulator's worst case: stop first when both are touched), as the bar's
    // range after the fill is real. Skipped while another market order of the symbol still works, so no liquidity is reused.
    private void CheckFillBarProtection(string symbol, IReadOnlyCollection<ExecutionTracker> trackers, TimelineBar bar)
    {
        var filled = trackers.Where(t => !t.IsTerminal && t.Plan.Candidate.FillTimeframe is not null &&
            t.FirstEntryFillUtc == bar.EndUtc && t.EntryFilledQuantity > t.ExitFilledQuantity).ToArray();
        if (filled.Length == 0 || _state.WorkingOrders.Values.Any(order => order.Command.OrderType == SimulatedOrderType.Market &&
                NormalizeSymbol(order.Command.Symbol) == symbol)) return;
        var start = bar.StartUtc.AddTicks(1);
        var commands = new List<SimulatedOrderCommand>();
        foreach (var tracker in filled) AddProtectiveOrderCommands(start, tracker, commands);
        if (commands.Count == 0) return;
        var sequence = _sourceSequences.TryGetValue(symbol, out var prior) ? prior + 1 : 0;
        _sourceSequences[symbol] = sequence;
        var step = _simulator.Advance(new ExecutionStepRequest(_state, new ExecutionMarketSlice(symbol, start, bar.EndUtc,
            bar.AvailableAtUtc, sequence, IsCompleted: true, bar.Bar.Open, bar.Bar.High, bar.Bar.Low, bar.Bar.Close, bar.Bar.Volume),
            commands, SlicePolicy(symbol, filled)));
        _state = step.State;
        AppendExecutionEvidence(step.Events);
        ApplyEvents(step.Events, bar.Bar);
        CountProtectiveFills(step.Events, "fill_bar", null);
        FinalizeTerminalTrackers();
    }

    // Counts the stop and target fills of decision-hour holdings in one step as "{kind}_stop" / "{kind}_target"; when the
    // slice opens (open) already through the level, the fill is a gap and counts as "gap_open_stop" / "gap_open_target".
    private void CountProtectiveFills(IReadOnlyCollection<SimulatedExecutionEvent> events, string kind, decimal? open)
    {
        foreach (var fill in events.Where(item => item.LastFillQuantity > 0))
        {
            var tracker = _active.FirstOrDefault(item => item.OwnsOrder(fill.ClientOrderId));
            if (tracker?.Plan.Candidate.FillTimeframe is null || tracker.ProtectiveKindOf(fill.ClientOrderId) is not { } level) continue;
            var candidate = tracker.Plan.Candidate;
            var gap = open is { } price && (level == "stop"
                ? tracker.IsShort ? price >= candidate.StopLossPrice : price <= candidate.StopLossPrice
                : tracker.IsShort ? price <= candidate.TakeProfitPrice : price >= candidate.TakeProfitPrice);
            Count($"{(gap ? "gap_open" : kind)}_{level}");
        }
    }

    // Adds one to a published decision-hour count.
    private void Count(string name) => _decisionHourCounts[name] = _decisionHourCounts.GetValueOrDefault(name) + 1;

    // Build the shared cost policy for one symbol slice; every tracker on a symbol must carry one slippage setting.
    private ExecutionSimulationPolicy SlicePolicy(string symbol, IReadOnlyCollection<ExecutionTracker> trackers)
    {
        var executionSlippagePolicies = trackers
            .Select(tracker => Math.Max(0m, tracker.Plan.Strategy.Execution.SlippageBps))
            .Distinct()
            .ToArray();
        if (executionSlippagePolicies.Length != 1)
            throw new InvalidOperationException($"Symbol '{symbol}' has mixed execution slippage policies in one market slice.");
        var baseSlippageBps = executionSlippagePolicies[0];
        return new ExecutionSimulationPolicy(
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
    }

    // Walk every trading session whose New York start is before (or, when inclusive, at) the clock, once each, and close
    // the holdings that must not be carried into it. Called before a bar is processed (exclusive: the bars available at a
    // session's start belong to the previous session) and after an advance to an admission time (inclusive), so a freed
    // slot is visible to that session's admissions.
    private void ForceExitsThrough(DateTimeOffset clock, bool inclusive)
    {
        if (_holdingRules is null) return;
        while (_nextHoldingSession < _holdingRules.Sessions.Count && _failures.Count == 0)
        {
            var session = _holdingRules.Sessions[_nextHoldingSession];
            var start = SessionStartUtc(session);
            if (inclusive ? start > clock : start >= clock) return;
            _nextHoldingSession++;
            foreach (var group in HoldingsToClose(session).GroupBy(item => NormalizeSymbol(item.Tracker.Plan.Candidate.Ticker)))
                CloseAtLastClose(group.Key, group.ToArray());
        }
    }

    // Holdings entering this session that must close at their last real close: a split takes effect on it (ex-date), or the
    // stock has no bar on it or on any later session of the run (its data ended). A gap after which bars resume is held.
    private (ExecutionTracker Tracker, string Reason)[] HoldingsToClose(DateOnly session) => _active
        .Where(t => !t.IsTerminal && t.EntryFilledQuantity > t.ExitFilledQuantity)
        .Where(t => _lastBarsBySymbol.TryGetValue(NormalizeSymbol(t.Plan.Candidate.Ticker), out var last) && SessionOf(last.StartUtc) < session)
        .Select(t => (Tracker: t, Reason: ForcedExitReason(NormalizeSymbol(t.Plan.Candidate.Ticker), session)))
        .Where(item => item.Reason is not null)
        .OrderBy(item => item.Tracker.Identity, StringComparer.Ordinal)
        .Select(item => (item.Tracker, item.Reason!)).ToArray();

    // The forced-exit reason for a held stock entering a session, or null to keep holding it.
    private string? ForcedExitReason(string symbol, DateOnly session) =>
        _holdingRules!.SplitExDates.Contains((symbol, session)) ? SplitExitReason
        : !_lastDailySessionBySymbol.TryGetValue(symbol, out var lastSession) || lastSession < session ? DataEndedExitReason
        : null;

    // Sell (or cover) each holding at its last real close through the shared simulator. The slice reuses that bar's close and
    // volume and ends at the bar's own completion time, so the fill belongs to the last real session and no bar enters the bar
    // stream.
    private void CloseAtLastClose(string symbol, IReadOnlyList<(ExecutionTracker Tracker, string Reason)> holdings)
    {
        var last = _lastBarsBySymbol[symbol];
        ForceExitAt(symbol, holdings, new ExecutionMarketSlice(symbol, last.EndUtc.AddTicks(-1), last.EndUtc, last.AvailableAtUtc, 0,
            IsCompleted: true, last.Bar.Close, last.Bar.Close, last.Bar.Close, last.Bar.Close, last.Bar.Volume), last.Bar,
            "forced_last_close_exit_not_filled");
    }

    // Sub-daily holdings (decision-hour timing) whose next session is a split's ex-date are sold (or covered) at the open of
    // this session's last-hour bar, the timing's exit point, before the bar's own orders run; the one-tick slice at the open
    // ends inside this session. Daily holdings keep the last-close rule above.
    private void SellBeforeSplitAtLastHour(string symbol, IReadOnlyCollection<ExecutionTracker> trackers, TimelineBar bar)
    {
        if (!IsLastHourBeforeExDate(symbol, bar.StartUtc)) return;
        var holdings = trackers
            .Where(t => !t.IsTerminal && t.Plan.Candidate.FillTimeframe is not null && t.EntryFilledQuantity > t.ExitFilledQuantity)
            .OrderBy(t => t.Identity, StringComparer.Ordinal).Select(t => (t, SplitExitReason)).ToArray();
        if (holdings.Length == 0) return;
        var open = bar.Bar.Open;
        var end = bar.StartUtc.AddTicks(1);
        ForceExitAt(symbol, holdings, new ExecutionMarketSlice(symbol, bar.StartUtc, end, end, 0, IsCompleted: true,
            open, open, open, open, bar.Bar.Volume), bar.Bar, "forced_split_exit_not_filled");
    }

    // True when the bar starting at barStart is the last-hour bar (DecisionHourDailyBars) of a session whose next trading
    // session is a split ex-date of the symbol; false without the session schedules.
    private bool IsLastHourBeforeExDate(string symbol, DateTimeOffset barStart)
    {
        if (_holdingRules?.Schedules is not { } schedules) return false;
        var session = SessionOf(barStart);
        if (!schedules.TryGetValue(session, out var schedule) || !schedule.IsTradingDay ||
            TradingFlow.Engine.Indicators.DecisionHourDailyBars.DecisionTimeUtc(schedule, NewYork) != barStart ||
            !_holdingSessionIndex.TryGetValue(session, out var index) || index + 1 >= _holdingRules.Sessions.Count)
            return false;
        return _holdingRules.SplitExDates.Contains((symbol, _holdingRules.Sessions[index + 1]));
    }

    // Run the forced exits of the given holdings on one price-point slice through the shared simulator: working entry, exit and
    // protective orders are cancelled first and a market order closes the rest. No participation cap applies, as the position
    // must close; impact keeps the configured rate per unit of participation, so a larger exit costs proportionally more. A
    // holding still open afterwards fails with failureReason.
    private void ForceExitAt(string symbol, IReadOnlyList<(ExecutionTracker Tracker, string Reason)> holdings,
        ExecutionMarketSlice slice, OhlcvBar source, string failureReason)
    {
        var commands = new List<SimulatedOrderCommand>();
        foreach (var (tracker, reason) in holdings) AddForcedExit(tracker, reason, slice.StartUtc, commands);
        var sequence = _sourceSequences.TryGetValue(symbol, out var prior) ? prior + 1 : 0;
        _sourceSequences[symbol] = sequence;
        var trackers = holdings.Select(item => item.Tracker).ToArray();
        var policy = SlicePolicy(symbol, trackers);
        policy = policy with
        {
            MaxParticipationPct = 0m,
            ImpactBpsAtMaxParticipation = policy.MaxParticipationPct > 0m
                ? policy.ImpactBpsAtMaxParticipation * 100m / policy.MaxParticipationPct : policy.ImpactBpsAtMaxParticipation
        };
        var step = _simulator.Advance(new ExecutionStepRequest(_state, slice with { SourceSequence = sequence }, commands, policy));
        _state = step.State;
        AppendExecutionEvidence(step.Events);
        ApplyEvents(step.Events, source);
        FinalizeTerminalTrackers();
        foreach (var tracker in trackers.Where(t => !t.IsTerminal)) Fail(tracker, failureReason);
        _active.RemoveAll(item => item.IsTerminal && !HasWorkingOrders(item));
    }

    // Queue the cancellations and the risk-reducing market exit for one holding, under the client order ID naming its reason.
    private void AddForcedExit(ExecutionTracker tracker, string reason, DateTimeOffset knownAt, ICollection<SimulatedOrderCommand> commands)
    {
        CancelProtectiveOrders(knownAt, tracker, commands);
        foreach (var orderId in new[] { tracker.EntryClientOrderId, tracker.ExitClientOrderId }
                     .Where(id => _state.WorkingOrders.ContainsKey(id)))
        {
            var order = _state.WorkingOrders[orderId];
            commands.Add(new SimulatedOrderCommand(ExecutionCommandKind.Cancel, $"cancel-{orderId}-{_nextCommandSequence}",
                tracker.Plan.Candidate.Ticker, order.Command.Side, order.Command.OrderType, 0, knownAt, _nextCommandSequence++,
                order.Command.IsRiskReducing, TargetClientOrderId: orderId));
        }
        tracker.ExitSubmitted = true;
        tracker.MarketExitSubmitted = true;
        commands.Add(new SimulatedOrderCommand(ExecutionCommandKind.Submit, tracker.ForcedExitClientOrderId(reason),
            tracker.Plan.Candidate.Ticker, tracker.IsShort ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell,
            SimulatedOrderType.Market, tracker.EntryFilledQuantity - tracker.ExitFilledQuantity, knownAt, _nextCommandSequence++,
            IsRiskReducing: true, SimulatedTimeInForce.Day,
            FixedFeeGroupId: $"exit-fee-{tracker.Identity}", OcoGroupId: tracker.Identity));
    }

    // The UTC instant a New York trading date starts (midnight), when its admissions are decided.
    private static DateTimeOffset SessionStartUtc(DateOnly session) =>
        new(TimeZoneInfo.ConvertTimeToUtc(session.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);

    // New York trading date of a bar opening or execution stamp.
    private static DateOnly SessionOf(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, NewYork).DateTime);

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

            if (ExpireSessionMarketOrders(timestamp, tracker, commands)) continue;
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
                tracker.Plan.Candidate.ExitTimestamp.Value.ToUniversalTime() > timestamp ||
                tracker.ExitRetryAtUtc > timestamp)
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
                tracker.ExitRetryAtUtc ?? tracker.Plan.Candidate.ExitTimestamp.Value.ToUniversalTime(),
                _nextCommandSequence++,
                IsRiskReducing: true,
                SimulatedTimeInForce.Day,
                FixedFeeGroupId: $"exit-fee-{tracker.Identity}", OcoGroupId: tracker.Identity));
        }

        return commands;
    }

    // Decision-hour market orders are day orders of their session: a remainder still working when a later session's first
    // slice arrives is cancelled at that boundary (no fill happened after the close) and counted. An entry that never filled
    // ends without a position (returns true); an exit remainder gets a new order submitted at this session's last hour.
    private bool ExpireSessionMarketOrders(DateTimeOffset timestamp, ExecutionTracker tracker, ICollection<SimulatedOrderCommand> commands)
    {
        if (tracker.Plan.Candidate.FillTimeframe is null || _decisionHour is null) return false;
        var session = SessionOf(timestamp);
        if (_state.WorkingOrders.TryGetValue(tracker.EntryClientOrderId, out var entry) && SessionOf(entry.Command.KnownAtUtc) < session)
        {
            commands.Add(CancelCommand(entry, timestamp));
            Count("entry_remainder_cancelled");
            if (tracker.EntryFilledQuantity == 0)
            {
                tracker.IsTerminal = true;
                return true;
            }
        }
        if (_state.WorkingOrders.TryGetValue(tracker.ExitClientOrderId, out var exit) && SessionOf(exit.Command.KnownAtUtc) < session)
        {
            commands.Add(CancelCommand(exit, timestamp));
            Count("exit_remainder_cancelled");
            tracker.RenewExitOrder(_decisionHour.Schedules.TryGetValue(session, out var schedule) && schedule.IsTradingDay
                ? TradingFlow.Engine.Indicators.DecisionHourDailyBars.DecisionTimeUtc(schedule, NewYork) : timestamp);
        }
        return false;
    }

    // A cancel command for one working order, known at the given instant.
    private SimulatedOrderCommand CancelCommand(SimulatedWorkingOrder order, DateTimeOffset knownAt) =>
        new(ExecutionCommandKind.Cancel, $"expire-{order.Command.ClientOrderId}-{_nextCommandSequence}", order.Command.Symbol,
            order.Command.Side, order.Command.OrderType, 0, knownAt, _nextCommandSequence++, order.Command.IsRiskReducing,
            TargetClientOrderId: order.Command.ClientOrderId);

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
        // A price of 0 means the strategy declares no protective order of that kind (published no-stop rules).
        if (tracker.Plan.Candidate.StopLossPrice > 0m)
            UpsertProtectiveOrder(
                tracker,
                ProtectiveOrderKind.Stop,
                timestamp,
                exitSide,
                SimulatedOrderType.Stop,
                openQuantity,
                stopPrice: ResolveEffectiveStopPrice(tracker, timestamp),
                commands: commands);
        if (tracker.Plan.Candidate.TakeProfitPrice > 0m)
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
            // A sub-daily fill (decision-hour timing) is booked to its own session; a daily one keeps the daily convention.
            var fillTimeframe = tracker.Plan.Candidate.FillTimeframe;
            _fills.Add(new(tracker.Identity, tracker.Plan.Candidate.StrategyId, tracker.Plan.Candidate.Ticker,
                executionEvent.Side == SimulatedOrderSide.Buy,
                executionEvent.ClientOrderId == tracker.EntryClientOrderId, executionEvent.LastFillQuantity,
                executionEvent.FillPrice!.Value, executionEvent.Fees, executionEvent.OccurredAtUtc,
                NativeDailySessionLedger.FillSessionDate(executionEvent.OccurredAtUtc, fillTimeframe, NativeDailySessionLedger.ValuationBasis))
                { FillTimeframe = fillTimeframe });
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

    // The unresolved window of every session of each decision-hour symbol (DecisionHourDailyBars.OpeningWindow over its daily
    // bar and regular-session hourly bars), as timeline slices from the regular open to the first hourly bar or the close; each
    // window's kind is kept to classify the resting-order fills on it. Empty without decision-hour candidates.
    private List<TimelineBar> UnresolvedWindows(IReadOnlyCollection<OhlcvBar> marketBars)
    {
        var windows = new List<TimelineBar>();
        if (_decisionHour is not { } decisionHour) return windows;
        var bySymbol = marketBars.Where(bar => decisionHour.Symbols.Contains(NormalizeSymbol(bar.Ticker)))
            .GroupBy(bar => NormalizeSymbol(bar.Ticker));
        foreach (var symbol in bySymbol)
        {
            var hourly = symbol.Where(bar => bar.Timeframe.Trim().Equals(ExecutionRules.DecisionHourFillTimeframe, StringComparison.OrdinalIgnoreCase))
                .ToLookup(bar => SessionOf(bar.Timestamp));
            foreach (var daily in symbol.Where(bar => bar.Timeframe.Trim().Equals("1d", StringComparison.OrdinalIgnoreCase)))
            {
                var session = SessionOf(daily.Timestamp);
                if (!decisionHour.Schedules.TryGetValue(session, out var schedule) || !schedule.IsTradingDay) continue;
                var regular = hourly[session].Where(bar => TradingFlow.Engine.Indicators.DecisionHourDailyBars.IsRegularSessionHour(bar, schedule, NewYork)).ToArray();
                if (TradingFlow.Engine.Indicators.DecisionHourDailyBars.OpeningWindow(daily, regular, schedule, NewYork) is not { } window) continue;
                windows.Add(new TimelineBar(window.Bar, window.Bar.Timestamp, window.EndUtc, window.EndUtc));
                _windowKinds[(symbol.Key, window.Bar.Timestamp)] = window.Kind;
            }
        }
        return windows;
    }

    private static TimelineBar[] BuildTimeline(
        IReadOnlyCollection<OhlcvBar> marketBars, IReadOnlyCollection<TimelineBar> windows)
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
            .Concat(windows)
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
        public string ExitClientOrderId { get; private set; } = exitClientOrderId;
        private readonly HashSet<string> exitOrderIds = new(StringComparer.Ordinal) { exitClientOrderId };
        private int exitRetries;

        // When a decision-hour market exit whose remainder was cancelled at the session close may be submitted again.
        public DateTimeOffset? ExitRetryAtUtc { get; private set; }

        // A fresh client order ID for the remainder of a cancelled market exit, submitted again at retryAtUtc.
        public void RenewExitOrder(DateTimeOffset retryAtUtc)
        {
            ExitClientOrderId = $"exit-{Identity}-retry-{++exitRetries}";
            exitOrderIds.Add(ExitClientOrderId);
            ExitRetryAtUtc = retryAtUtc;
            MarketExitSubmitted = false;
        }
        private string DataEndedClientOrderId => $"data-ended-{Identity}";
        private string SplitExitClientOrderId => $"split-exit-{Identity}";

        // The client order ID of a forced exit, which names its reason so the filled trade carries it.
        public string ForcedExitClientOrderId(string reason) =>
            reason == SplitExitReason ? SplitExitClientOrderId : DataEndedClientOrderId;
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
                exitOrderIds.Contains(clientOrderId) ||
                DataEndedClientOrderId.Equals(clientOrderId, StringComparison.Ordinal) ||
                SplitExitClientOrderId.Equals(clientOrderId, StringComparison.Ordinal))
            {
                return true;
            }

            return StopOrderIds.Contains(clientOrderId) ||
                TargetOrderIds.Contains(clientOrderId);
        }

        // "stop" or "target" for one of this holding's protective orders; null for any other order.
        public string? ProtectiveKindOf(string clientOrderId) =>
            StopOrderIds.Contains(clientOrderId) ? "stop" : TargetOrderIds.Contains(clientOrderId) ? "target" : null;

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
            else if (DataEndedClientOrderId.Equals(clientOrderId, StringComparison.Ordinal))
            {
                FilledExitReason = DataEndedExitReason;
            }
            else if (SplitExitClientOrderId.Equals(clientOrderId, StringComparison.Ordinal))
            {
                FilledExitReason = SplitExitReason;
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
    // The bars this plan's orders fill on: the fine replay slices, the candidate's own fill timeframe (decision-hour: hourly
    // bars), or the strategy's execution timeframe.
    public string ExecutionTimeframe => Candidate.ExecutionReplay is not null
        ? ExecutionReplayPolicy.Timeframe : Candidate.FillTimeframe ?? Strategy.Execution.Timeframe;
}

/// <summary>
/// Research-screen daily holding rules: the run's ordered trading sessions (as the native ledger resolves them) and each
/// stock's split ex-dates (symbol upper case). A holding is closed at its last real close before an ex-date, or when its
/// bars never resume within these sessions. A holding that fills on sub-daily bars (decision-hour timing) is instead sold
/// at the open of the last-hour bar of the session before the ex-date, which needs the session schedules.
/// </summary>
internal sealed record HoldingDataRules(IReadOnlyList<DateOnly> Sessions, IReadOnlySet<(string Symbol, DateOnly ExDate)> SplitExDates)
{
    /// <summary>The exchange schedule of each session (regular or early close); null when no holding fills on sub-daily bars.</summary>
    public IReadOnlyDictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule>? Schedules { get; init; }
}

/// <summary>
/// Decision-hour execution inputs of one book: the exchange schedule of each session and the symbols (upper case) of the
/// candidates that fill on sub-daily bars, whose sessions get unresolved windows and session-close order expiry.
/// </summary>
internal sealed record DecisionHourExecution(IReadOnlyDictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule> Schedules,
    IReadOnlySet<string> Symbols);

internal sealed record ActiveBacktestExecution(
    string Ticker,
    string StrategyName,
    string StrategyId,
    DateTimeOffset EntryTimestamp,
    int RequestedQuantity,
    decimal ReservedNotional)
{
    /// <summary>The candidate's planned exit bar start (an open fill for next-open exits), or null while it is still open.</summary>
    public DateTimeOffset? PlannedExitTimestamp { get; init; }

    /// <summary>The candidate's planned exit reason, used to tell an open-fill sale from a stop or target hit inside a bar.</summary>
    public string? PlannedExitReason { get; init; }

    /// <summary>Close of the latest bar of this ticker the coordinator has processed (known before the admission time).</summary>
    public decimal? LastClose { get; init; }
}
