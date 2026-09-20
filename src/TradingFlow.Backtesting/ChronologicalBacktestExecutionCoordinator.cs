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
    private readonly List<ExecutionTracker> _active = [];
    private readonly List<BacktestTrade> _completed = [];
    private readonly List<BacktestExecutionFailure> _failures = [];
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
            tracker.Plan.RequestedQuantity * tracker.Plan.Candidate.EntryPrice))
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

        if (!HasExecutionBar(plan.Candidate.Ticker, plan.Strategy.Execution.Timeframe, entryTimestamp))
        {
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
            !tracker.Plan.Strategy.Execution.Timeframe.Equals(
                plan.Strategy.Execution.Timeframe,
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

        _active.Add(new ExecutionTracker(
            identity,
            plan,
            $"entry-{identity}",
            $"exit-{identity}"));
        rejectionReason = null;
        return true;
    }

    public void Complete()
    {
        if (_active.Count == 0)
        {
            return;
        }

        while (_nextTimelineIndex < _timeline.Length)
        {
            AdvanceBar(_timeline[_nextTimelineIndex++]);
        }

        foreach (var tracker in _active.Where(item => !item.IsTerminal).ToArray())
        {
            Fail(tracker, tracker.EntryFilledQuantity == 0
                ? "entry_not_filled_before_end_of_data"
                : "exit_not_filled_before_end_of_data");
        }

        _active.RemoveAll(item => item.IsTerminal);
    }

    private void AdvanceBar(TimelineBar timelineBar)
    {
        var symbol = NormalizeSymbol(timelineBar.Bar.Ticker);
        var trackers = _active
            .Where(tracker => !tracker.IsTerminal)
            .Where(tracker => NormalizeSymbol(tracker.Plan.Candidate.Ticker).Equals(
                symbol,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (trackers.Length == 0 || !IsExecutionTimeframe(timelineBar, trackers))
        {
            return;
        }

        var bar = timelineBar.Bar;
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
        _active.RemoveAll(item => item.IsTerminal);
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
                    SimulatedTimeInForce.Day,
                    FixedFeeGroupId: $"entry-fee-{tracker.Identity}"));
            }

            var priceTriggeredExitIsDue = IsPriceTriggeredExit(tracker.Plan.Candidate.ExitReason) &&
                tracker.Plan.Candidate.ExitTimestamp.ToUniversalTime() <= timestamp;
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

            AddProtectiveOrderCommands(timestamp, tracker, commands);

            if (tracker.ExitSubmitted ||
                IsPriceTriggeredExit(tracker.Plan.Candidate.ExitReason) ||
                tracker.Plan.Candidate.ExitTimestamp.ToUniversalTime() > timestamp)
            {
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
                    tracker.Plan.Candidate.ExitTimestamp.ToUniversalTime(),
                    _nextCommandSequence++,
                    IsRiskReducing: false,
                    TargetClientOrderId: tracker.EntryClientOrderId));
            }

            tracker.ExitSubmitted = true;
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
                tracker.EntryFilledQuantity,
                tracker.Plan.Candidate.ExitTimestamp.ToUniversalTime(),
                _nextCommandSequence++,
                IsRiskReducing: true,
                SimulatedTimeInForce.Day,
                FixedFeeGroupId: $"exit-fee-{tracker.Identity}"));
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
            FixedFeeGroupId: $"exit-fee-{tracker.Identity}"));
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
            candidate.ExitTimestamp.ToUniversalTime() <= timestamp
                ? candidate.ExitPrice
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
                tracker.LastExitFillUtc ?? candidate.ExitTimestamp.ToUniversalTime(),
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
            .Select(tracker => tracker.Plan.Strategy.Execution.Timeframe.Trim().ToLowerInvariant())
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
    int RequestedQuantity);

internal sealed record ActiveBacktestExecution(
    string Ticker,
    string StrategyName,
    string StrategyId,
    DateTimeOffset EntryTimestamp,
    int RequestedQuantity,
    decimal ReservedNotional);
