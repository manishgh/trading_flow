using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    // Advance one symbol's completed RTH stream once; only shared kernel intents reach ordinary execution candidates.
    private async Task<IReadOnlyList<StrategyTickerBacktestBatch>> BuildZoneCandidatesAsync(
        BacktestRunConfig run, StrategyDefinition strategy, PreparedBacktestMarket preparedMarket, CancellationToken token)
    {
        var schedules = candidateSessionSchedules ?? throw new InvalidDataException("Zone candidates require authoritative exchange sessions.");
        var runtime = ResolveRuntimeStrategy(strategy);
        var priorSessionCount = strategy.EntryRules.IntradayZone!.PriorSessionCount;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var profile = new MarketEvidenceProfile(MarketEvidenceProfile.ProductionVersion, 20, 20,
            strategy.Session.ExchangeTimezone, schedules,
            MarketEvidenceOperationalRules.ForExecutionTimingPolicy(runtime.AdmissionProfile.ExecutionTimingPolicy), false);
        var budget = new RetainedCandidateBudget(run.Engine.MaxRetainedCandidates);
        var batches = new List<StrategyTickerBacktestBatch>();
        foreach (var ticker in run.Tickers)
        {
            token.ThrowIfCancellationRequested();
            if (!preparedMarket.MarketState.TickerStates.TryGetValue(ticker, out var state) ||
                !state.BarsByTimeframe.TryGetValue("5m", out var source))
            {
                batches.Add(StrategyTickerBacktestBatch.Failed(ticker, strategy, "zone_five_minute_source_unavailable")
                    with { IntradayUnavailableFromUtc = preparedMarket.WindowStart });
                continue;
            }
            // Session VWAP must be calculated from RTH inputs; pre/postmarket cannot contaminate the entry filter.
            var bars = source.Where(bar => IsIntradayRegularBar(bar, schedules, zone)).OrderBy(bar => bar.Timestamp).ToArray();
            var indicators = new IndicatorEngine(profile);
            var snapshots = indicators.Compute(bars);
            var fifteen = new BarResampler(profile).ResampleComplete(bars, "15m").ToArray();
            var fifteenSnapshots = indicators.Compute(fifteen).ToDictionary(item => item.Timestamp);
            var evaluator = new IntradayZoneEvaluator(strategy.EntryRules.IntradayZone);
            var candidates = new List<BacktestCandidateTrade>();
            var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
            var sessionBars = bars.GroupBy(bar => IntradaySessionDate(bar.Timestamp, zone))
                .ToDictionary(group => group.Key, group => group.ToArray());
            DateTimeOffset? unavailable = FindIntradaySourceGap(sessionBars, schedules, IntradayPreviousSessionDates(schedules), zone,
                preparedMarket.WindowStart, preparedMarket.WindowEnd);
            var calendar = schedules.Values.Where(item => item.IsTradingDay).OrderBy(item => item.TradeDate).ToArray();
            var contexts = calendar.Select((session, index) => (session, index)).Where(item => item.index >= priorSessionCount)
                .ToDictionary(item => item.session.TradeDate, item => new IntradayZoneSessionContext(item.session.TradeDate,
                    IntradayCalendarAtUtc(item.session.TradeDate, item.session.RegularOpen, zone),
                    IntradayCalendarAtUtc(item.session.TradeDate, item.session.RegularClose, zone),
                    calendar.Take(item.index).TakeLast(priorSessionCount).Reverse().Select(prior => prior.TradeDate).ToArray(),
                    IntradayCalendarAtUtc(calendar[item.index - 1].TradeDate, calendar[item.index - 1].RegularClose, zone)));
            using var journal = new BacktestCandidateJournal(CreateCandidateAuditJournalPath(strategy, ticker));
            using var evaluationJournal = strategy.EntryRules.IntradayZone.IsPythonVolumeSpreadComparison
                ? OpenZoneEvaluationJournal() : null;
            var orchestrator = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), journal);
            var simulation = BacktestCandidateJournal.CreateRun(new { Run = CandidateIdentityConfiguration(run), Strategy = runtime.Identity, Ticker = ticker },
                $"{run.RunName}:{runtime.Identity.StrategyId}:{ticker}", preparedMarket.EvaluationStart);
            var fifteenIndex = 0;
            for (var index = 0; index < bars.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var bar = bars[index];
                var asOf = bar.Timestamp.AddMinutes(5);
                if (!contexts.TryGetValue(IntradaySessionDate(bar.Timestamp, zone), out var session)) continue;
                // A 15m update at this close is retained separately; evaluator selects only versions known before the 5m candle began.
                while (fifteenIndex < fifteen.Length && fifteen[fifteenIndex].Timestamp.AddMinutes(15) <= asOf)
                {
                    var completed = fifteen[fifteenIndex++];
                    if (contexts.TryGetValue(IntradaySessionDate(completed.Timestamp, zone), out var zoneSession))
                        evaluator.ProcessCompleted15m(completed, fifteenSnapshots[completed.Timestamp], zoneSession, completed.Timestamp.AddMinutes(15));
                }
                var increment = UsEquityOrderIncrementResolver.Resolve(session.SessionDate, bar.Low, bar.High);
                var context = evaluator.ProcessCompleted5m(bar, snapshots[index], session, asOf, increment?.TickSize ?? 0);
                if (asOf < preparedMarket.EvaluationStart || asOf > preparedMarket.WindowEnd) continue;
                diagnostics.EvaluatedBarCount++;
                if (increment is null || context.RejectionReason is "intraday_zone_5m_or_15m_coverage_unknown" or
                    "intraday_zone_vwap_or_slot_volume_unavailable" or "python_zone_atr_or_vwap_unavailable" or "python_zone_short_volume_unavailable" or
                    "python_zone_atr_unavailable" or "python_zone_volume_unavailable")
                    unavailable = EarlierIntradayBoundary(unavailable, asOf);
                if (context.Signal is null)
                {
                    AppendZoneEvaluation(evaluationJournal, strategy.StrategyId, ticker, context);
                    diagnostics.IncrementRejection(context.RejectionReason ?? "intraday_zone_no_pattern");
                    continue;
                }
                var request = CreateZoneDecisionRequest(runtime, ticker, bars, snapshots, fifteen, fifteenSnapshots,
                    index, fifteenIndex, context, simulation.RunId, session.SessionDate);
                var persisted = await orchestrator.EvaluateAsync(simulation, request, token);
                AppendZoneEvaluation(evaluationJournal, strategy.StrategyId, ticker, context,
                    persisted.Candidate.CandidateId.ToString("D"), persisted.Decision?.NoEntryReason);
                if (persisted.Decision?.OrderPlan?.ZoneEntry is not { } pending)
                {
                    diagnostics.IncrementRejection(persisted.Decision?.NoEntryReason ?? "intraday_zone_order_not_triggered");
                    continue;
                }
                budget.Reserve();
                candidates.Add(CreateZonePendingCandidate(strategy, ticker, persisted.Candidate.CandidateId.ToString("D"), pending,
                    session.CloseUtc, persisted.Decision.OrderPlan.ZoneAlternatives));
                diagnostics.CandidateTradeCount++;
            }
            if (!bars.Any(bar => bar.Timestamp >= preparedMarket.WindowStart && bar.Timestamp.AddMinutes(5) <= preparedMarket.WindowEnd))
                unavailable = preparedMarket.WindowStart;
            batches.Add(new StrategyTickerBacktestBatch(ticker, strategy.StrategyId, strategy.StrategyName, unavailable is null,
                unavailable is null ? null : $"Zone entry evidence is incomplete from {unavailable:O}.", candidates, [], journal.DurableJournalPath, diagnostics)
                { IntradayUnavailableFromUtc = unavailable });
        }
        return batches;
    }

    // Assemble only completed prefixes after the shared evaluator emits an intent; future execution bars remain inaccessible.
    private static StrategyDecisionRequest CreateZoneDecisionRequest(AuthorizedRuntimeStrategy runtime, string ticker,
        IReadOnlyList<OhlcvBar> five, IReadOnlyList<IndicatorSnapshot> snapshots, IReadOnlyList<OhlcvBar> fifteen,
        IReadOnlyDictionary<DateTimeOffset, IndicatorSnapshot> fifteenSnapshots, int fiveIndex, int fifteenCount,
        IntradayZoneDecisionContext context, Guid runId, DateOnly sessionDate)
    {
        var signal = context.Signal!;
        var completedFifteen = fifteen.Take(fifteenCount).ToArray();
        var completedFive = five.Take(fiveIndex + 1).ToArray();
        var asOf = signal.ActiveAtUtc;
        var key = StrategyDecisionRequestAssembler.CreateSetupKey(runtime.Definition.StrategyId, "5m", signal.ConfirmationBar.Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(ticker, key, asOf, signal.SessionDeadlineUtc);
        var eligibility = new StrategyEligibilityEvidence(true, "static_run_universe", $"{ticker}:{sessionDate:yyyy-MM-dd}",
            asOf, JsonSerializer.Serialize(new { ticker, sessionDate }));
        return StrategyDecisionRequestAssembler.Create(runtime, ticker,
            new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["5m"] = completedFive, ["15m"] = completedFifteen },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>> { ["5m"] = snapshots.Take(fiveIndex + 1).ToArray(),
                ["15m"] = completedFifteen.Select(item => fifteenSnapshots[item.Timestamp]).ToArray() },
            Array.Empty<CatalystEvent>(), asOf, discovery, eligibility, eligibility with { Source = "strategy_has_no_regime_gate" },
            StrategyCandidateState.DataWarming, 0, key, asOf, signal.SessionDeadlineUtc, 2, runId, zoneContext: context);
    }

    // Preserve fixed comparison targets and genuine zone evidence while retaining the original SIP candidate contract.
    private static BacktestCandidateTrade CreateZonePendingCandidate(StrategyDefinition strategy, string ticker,
        string candidateId, IntradayZoneSignal signal, DateTimeOffset sessionClose,
        IReadOnlyList<IntradayZoneSignal>? alternatives)
    {
        var risk = Math.Abs(signal.EntryReferencePrice - signal.InitialStopPrice);
        var comparison = strategy.EntryRules.IntradayZone!.IsPythonFixedTargetComparison;
        var target = comparison ? signal.FixedTargetPrice ?? (signal.Evidence.TargetPlan is not null ? 0m
                : throw new InvalidDataException("Python zone intent requires a fixed target."))
            : signal.EntryReferencePrice + (signal.Direction == "long" ? 1 : -1) * strategy.ExitRules.TargetRMultiple * risk;
        return new(ticker, strategy.StrategyName, signal.Direction, signal.ActiveAtUtc, signal.EntryReferencePrice,
            signal.InitialStopPrice, target, signal.SessionDeadlineUtc, null, "session_deadline_pending", risk,
            signal.ConfirmationBar.Volume, StrategyId: strategy.StrategyId, CandidateId: candidateId)
        {
            DecisionStrategyId = strategy.StrategyId, ZoneExecution = signal, ZoneAlternatives = alternatives,
            IntradayExecution = comparison ? new(signal.EntryReferencePrice, signal.TickSize, signal.ActiveAtUtc,
                signal.EntryCutoffUtc ?? throw new InvalidDataException("Python zone intent requires an entry cutoff."),
                signal.SessionDeadlineUtc, null, null, signal.ConfirmationBar.Timestamp,
                signal.Evidence.ConfirmationCompletedAtUtc, signal.Pattern)
            {
                EntryOrderMode = IntradayEntryOrderMode.NextBarMarket, EntryCutoffUtc = signal.EntryCutoffUtc,
                MaxFilledParentTradesPerSession = signal.MaxFilledParentTradesPerSession
            } : null,
            ExecutionReplay = new(strategy.EntryRules.IntradayZone.IsPythonVolumeSpreadComparison ? ExecutionReplayPolicy.PythonZoneVolumeSpreadV1
                : comparison ? ExecutionReplayPolicy.PythonZoneBounceBreakdownV1
                : ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1, signal.ActiveAtUtc,
                signal.SessionDeadlineUtc, signal.SessionDeadlineUtc, []) { EventExecutionEndUtc = comparison ? null : sessionClose }
        };
    }
}
