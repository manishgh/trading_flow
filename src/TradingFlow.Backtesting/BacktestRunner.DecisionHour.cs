using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Backtesting;

// Candidate generation for strategies bound to the decision-hour timing (ExecutionRules.DecisionHourAsTradedTimingPolicy):
// each session is decided one hour before its close on a provisional daily bar (DecisionHourDailyBars) through the shared
// decision kernel; entries and technical exits fill on that session's last-hour bar, and stops and targets are checked on the
// hourly bars after the entry fill. Skipped stock-days (a missing decision or last-hour bar) are counted, never fatal.
public sealed partial class BacktestRunner
{
    // The pipeline's indicator engine (same evidence profile and calendar), used for provisional snapshots.
    private IndicatorEngine? decisionHourIndicators;

    // Each stock-session's decision evidence, computed once per run and shared by every decision-hour strategy.
    private DecisionHourEvidenceCache decisionHourEvidence = new();

    // Hourly bars outside their session's [regular open, scheduled close), dropped before any use (published evidence).
    private int decisionHourDroppedHourlyBars;

    /// <summary>
    /// One session of one stock: its decision instant, the decision evidence or the named skip reason, and the unresolved
    /// window (the part of the session no hourly bar shows) where resting stops and targets are checked on the daily bar.
    /// </summary>
    private sealed record DecisionHourSession(DateOnly Session, int DailyIndex, DateTimeOffset DecisionTimeUtc,
        DecisionHourDailyBars.SessionDecision? Decision, IndicatorSnapshot? Snapshot, string? SkipReason,
        DecisionHourDailyBars.UnresolvedWindow? Window);

    /// <summary>A planned exit: the bar it fills on, the price estimate the candidate records, the reason and its stamp.</summary>
    private sealed record DecisionHourExit(OhlcvBar Bar, decimal Price, string Reason, DateTimeOffset At);

    /// <summary>True when the strategy's immutable admission profile binds it to the decision-hour timing.</summary>
    private bool IsDecisionHour(StrategyDefinition strategy) =>
        ResolveRuntimeStrategy(strategy).AdmissionProfile.ExecutionTimingPolicy == ExecutionRules.DecisionHourAsTradedTimingPolicy;

    /// <summary>
    /// Decides every evaluated session of one stock at its decision hour through the shared kernel and builds a candidate for
    /// each triggered decision. Inputs: the stock's completed daily bars and snapshots plus its hourly bars. A session whose
    /// decision or last-hour bar is missing is counted under its named reason and skipped.
    /// </summary>
    private async Task<StrategyCandidateEvaluation> CreateDecisionHourCandidatesAsync(BacktestRunConfig run,
        AuthorizedRuntimeStrategy runtimeStrategy, IReadOnlyList<OhlcvBar> dailyBars, IReadOnlyList<IndicatorSnapshot> dailySnapshots,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe, IReadOnlyCollection<CatalystEvent> catalysts,
        DateTimeOffset evaluationStart, DateTimeOffset evaluationEnd, UniverseMembership? universeMembership, RegimeCalendar? regimeCalendar,
        BacktestCandidateJournal candidateJournal, ExecutionAuditor executionAuditor, RetainedCandidateBudget retainedCandidateBudget,
        CancellationToken cancellationToken)
    {
        var strategy = runtimeStrategy.Definition;
        RequireDecisionHourSupport(run, strategy);
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        var candidates = new List<BacktestCandidateTrade>();
        var hourlyBars = HourlyBarsThrough(barsByTimeframe, evaluationEnd);
        var startIndex = Math.Max(Math.Max(run.Engine.IndicatorWarmupBars, strategy.EntryRules.TechnicalFactor?.RequiredCompletedBars ?? 0) - 1, 1);
        var sessions = BuildDecisionHourSessions(strategy, dailyBars, hourlyBars, startIndex, evaluationStart, evaluationEnd, cancellationToken);
        var candidateDecisions = new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), candidateJournal);
        var simulationRun = BacktestCandidateJournal.CreateRun(
            new { Run = CandidateIdentityConfiguration(run), Strategy = runtimeStrategy.Identity, Ticker = dailySnapshots[0].Ticker },
            $"{run.RunName}:{runtimeStrategy.Identity.StrategyId}:{dailySnapshots[0].Ticker}", evaluationStart);
        var exitContext = new DecisionHourExitContext(strategy, sessions, hourlyBars, dailySnapshots);
        for (var index = 0; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.EvaluatedBarCount++;
            if (sessions[index] is not { Decision: { } decision, Snapshot: { } snapshot })
            {
                diagnostics.IncrementRejection(sessions[index].SkipReason!);
                continue;
            }
            var request = await CreateDecisionHourRequestAsync(run, runtimeStrategy, barsByTimeframe, snapshotsByTimeframe, catalysts,
                decision, snapshot, universeMembership, regimeCalendar, simulationRun.RunId, cancellationToken);
            var persisted = await candidateDecisions.EvaluateAsync(simulationRun, request, cancellationToken);
            var evaluated = persisted.Decision
                ?? throw new InvalidOperationException("Backtest candidate became terminal before its current decision was evaluated.");
            if (!evaluated.IsTriggered || evaluated.OrderPlan is not { } orderPlan || evaluated.Signal is not { } signal)
            {
                diagnostics.IncrementRejection(evaluated.NoEntryReason ?? "execution_trigger_not_ready");
                continue;
            }
            var candidate = CreateDecisionHourCandidate(persisted.Candidate.CandidateId.ToString("D"), run, exitContext, index, signal,
                orderPlan, executionAuditor);
            if (candidate is null)
            {
                diagnostics.IncrementRejection("no_next_bar_or_invalid_stop");
                continue;
            }
            retainedCandidateBudget.Reserve();
            var decisionStrategyId = persisted.Candidate.SelectedStrategy;
            if (String.IsNullOrWhiteSpace(decisionStrategyId))
                throw new InvalidDataException("Triggered candidate is missing its persisted decision strategy identity.");
            candidates.Add(candidate with { DecisionStrategyId = decisionStrategyId });
        }

        diagnostics.CandidateTradeCount = candidates.Count;
        return new StrategyCandidateEvaluation(candidates,
            candidateJournal.DurableJournalPath is null ? candidateJournal.Snapshot() : [], diagnostics);
    }

    /// <summary>
    /// Refuses the decision-hour timing outside the implemented path: no fine or terminal replay, and no trailing stops, VWAP
    /// confirmations or log-trend exits, which read evidence the provisional session does not supply.
    /// </summary>
    private static void RequireDecisionHourSupport(BacktestRunConfig run, StrategyDefinition strategy)
    {
        if (run.Engine.ExecutionReplayPolicyId is not null || run.Engine.PrecommittedLiquidationSourceUtc is not null)
            throw new NotSupportedException("The decision-hour timing runs on the native daily book without an execution replay policy or terminal exit.");
        var exits = strategy.ExitRules;
        if (exits.EnableAtrTrailingStop || exits.EnableConfirmedVwapExit || exits.ExitOnLogPriceFade)
            throw new NotSupportedException($"{strategy.StrategyName}: the decision-hour timing does not support trailing stops, " +
                "confirmed VWAP exits or log-trend exits.");
    }

    /// <summary>The stock's hourly bars that complete by the evaluation end, in time order (empty when it has none).</summary>
    private static OhlcvBar[] HourlyBarsThrough(IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe, DateTimeOffset end) =>
        barsByTimeframe.TryGetValue(ExecutionRules.DecisionHourFillTimeframe, out var hourly)
            ? hourly.Where(bar => bar.Timestamp.AddHours(1) <= end).OrderBy(bar => bar.Timestamp).ToArray()
            : [];

    /// <summary>
    /// Builds the decision evidence of every trading session from the warm-up index on whose last hour completes inside the
    /// evaluation window: the provisional bar and snapshot (DecisionHourDailyBars, the pipeline's indicator engine on the prior
    /// completed daily bars plus the provisional bar), or the named skip reason, from the run's shared cache. Decisions read
    /// nothing after their decision time; the unresolved window (completed daily bar) serves resting orders only.
    /// </summary>
    private List<DecisionHourSession> BuildDecisionHourSessions(StrategyDefinition strategy, IReadOnlyList<OhlcvBar> dailyBars,
        IReadOnlyList<OhlcvBar> hourlyBars, int startIndex, DateTimeOffset evaluationStart, DateTimeOffset evaluationEnd,
        CancellationToken cancellationToken)
    {
        var engine = decisionHourIndicators ?? throw new InvalidOperationException("Decision-hour snapshots need the pipeline's indicator engine.");
        var schedules = candidateSessionSchedules ?? throw new InvalidDataException("The decision-hour timing requires an authoritative exchange calendar.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var hourlyBySession = hourlyBars.GroupBy(bar => ExchangeDate(bar.Timestamp, zone))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<OhlcvBar>)group.ToArray());
        var sessions = new List<DecisionHourSession>();
        for (var index = startIndex; index < dailyBars.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = ExchangeDate(dailyBars[index].Timestamp, zone);
            if (!schedules.TryGetValue(date, out var schedule))
                throw new InvalidDataException($"The exchange calendar does not cover {date:yyyy-MM-dd}.");
            if (!schedule.IsTradingDay) continue;
            var decisionTime = DecisionHourDailyBars.DecisionTimeUtc(schedule, zone);
            if (decisionTime < evaluationStart) continue;
            if (decisionTime.AddHours(1) > evaluationEnd) break;
            var at = index;
            var evidence = decisionHourEvidence.Get(dailyBars[index].Ticker, date, () =>
                DecisionHourEvidenceCache.Compute(engine, dailyBars, at, hourlyBySession.GetValueOrDefault(date, []), schedule, zone));
            sessions.Add(new(date, index, decisionTime, evidence.Decision, evidence.Snapshot, evidence.SkipReason, evidence.Window));
        }
        return sessions;
    }

    /// <summary>
    /// Keeps only hourly bars that start inside their session's [regular open, scheduled close) (dropping pre-market,
    /// after-hours and post-early-close bars, with their snapshots) in every prepared stock context, before any decision, stop
    /// or fill reads them; returns the contexts and the number of bars dropped. Other timeframes are unchanged.
    /// </summary>
    private (IReadOnlyDictionary<string, PreparedTickerEvaluationContext> Contexts, int Dropped) KeepRegularSessionHourlyBars(
        IReadOnlyDictionary<string, PreparedTickerEvaluationContext> contexts)
    {
        var schedules = candidateSessionSchedules ?? throw new InvalidDataException("The decision-hour timing requires an authoritative exchange calendar.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        bool Regular(OhlcvBar bar) => !bar.Timeframe.Equals(ExecutionRules.DecisionHourFillTimeframe, StringComparison.OrdinalIgnoreCase) ||
            schedules.TryGetValue(ExchangeDate(bar.Timestamp, zone), out var schedule) && DecisionHourDailyBars.IsRegularSessionHour(bar, schedule, zone);
        var dropped = 0;
        var kept = contexts.ToDictionary(pair => pair.Key, pair =>
        {
            var context = pair.Value;
            if (context.ErrorMessage is not null ||
                !context.BarsByTimeframe.TryGetValue(ExecutionRules.DecisionHourFillTimeframe, out var hourly)) return context;
            var keep = hourly.Select(Regular).ToArray();
            dropped += keep.Count(value => !value);
            var bars = new Dictionary<string, IReadOnlyList<OhlcvBar>>(context.BarsByTimeframe, StringComparer.OrdinalIgnoreCase)
                { [ExecutionRules.DecisionHourFillTimeframe] = hourly.Where((_, i) => keep[i]).ToArray() };
            var snapshots = new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(context.SnapshotsByTimeframe, StringComparer.OrdinalIgnoreCase);
            if (snapshots.TryGetValue(ExecutionRules.DecisionHourFillTimeframe, out var hourlySnapshots) && hourlySnapshots.Count == keep.Length)
                snapshots[ExecutionRules.DecisionHourFillTimeframe] = hourlySnapshots.Where((_, i) => keep[i]).ToArray();
            return context with { BarsByTimeframe = bars, SnapshotsByTimeframe = snapshots, ProcessedBars = context.ProcessedBars.Where(Regular).ToArray() };
        }, StringComparer.OrdinalIgnoreCase);
        return (kept, dropped);
    }

    /// <summary>The exchange-calendar date of an instant.</summary>
    private static DateOnly ExchangeDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The shared kernel request for one session's decision: completed daily evidence before the session plus the provisional
    /// bar and snapshot, decided at the decision time and valid until the session's close (the end of the last hour).
    /// </summary>
    private async Task<StrategyDecisionRequest> CreateDecisionHourRequestAsync(BacktestRunConfig run, AuthorizedRuntimeStrategy runtimeStrategy,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe, IReadOnlyCollection<CatalystEvent> catalysts,
        DecisionHourDailyBars.SessionDecision decision, IndicatorSnapshot snapshot, UniverseMembership? universeMembership,
        RegimeCalendar? regimeCalendar, Guid runId, CancellationToken cancellationToken)
    {
        var strategy = runtimeStrategy.Definition;
        var ticker = snapshot.Ticker;
        var decisionTime = decision.DecisionTimeUtc;
        var expiry = decision.LastHourBar.Timestamp.AddHours(1);
        var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(strategy.StrategyId, strategy.Timeframe, snapshot.Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(ticker, setupKey, decisionTime, expiry);
        var (universe, regime) = EligibilityEvidence(ticker, strategy, decisionTime, universeMembership, regimeCalendar);
        return StrategyDecisionRequestAssembler.Create(runtimeStrategy, ticker, barsByTimeframe, snapshotsByTimeframe, catalysts,
            decisionTime, discovery, universe, regime, StrategyCandidateState.DataWarming, 0, setupKey, decisionTime, expiry,
            run.Engine.IndicatorWarmupBars, runId,
            drawdownContext: await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(_decisionContextProvider,
                ticker, decisionTime, barsByTimeframe, "backtest", cancellationToken),
            historicalNewsPresence: strategy.EntryRules.HistoricalNewsDiagnostic is not null && _historicalNewsProvider is not null
                ? await _historicalNewsProvider.ResolveAsync(ticker, decisionTime, cancellationToken) : null,
            provisionalSetup: new ProvisionalSetupObservation(decision.ProvisionalDailyBar, snapshot));
    }

    /// <summary>
    /// The universe and regime evidence of one decision: the stock's membership on the session the decision trades in (see
    /// EligibilitySession) and the strategy's regime calendar at the decision time (static universe / no regime gate when absent).
    /// </summary>
    private (StrategyEligibilityEvidence Universe, StrategyEligibilityEvidence Regime) EligibilityEvidence(string ticker,
        StrategyDefinition strategy, DateTimeOffset decisionTime, UniverseMembership? universeMembership, RegimeCalendar? regimeCalendar)
    {
        var eligibilityDay = EligibilitySession(decisionTime);
        var universeEligible = universeMembership is null || universeMembership.IsMember(ticker, eligibilityDay);
        var regimeEligible = regimeCalendar is null || regimeCalendar.IsOn(decisionTime);
        var universe = new StrategyEligibilityEvidence(universeEligible,
            universeMembership is null ? "static_run_universe" : "historical_universe_membership",
            $"{ticker}:{eligibilityDay:yyyy-MM-dd}", decisionTime,
            System.Text.Json.JsonSerializer.Serialize(new { Ticker = ticker, eligibilityDay, universeEligible }));
        var regime = new StrategyEligibilityEvidence(regimeEligible,
            regimeCalendar is null ? "strategy_has_no_regime_gate" : "historical_regime_calendar",
            $"{strategy.StrategyId}:{eligibilityDay:yyyy-MM-dd}", decisionTime,
            System.Text.Json.JsonSerializer.Serialize(new { strategy.StrategyId, eligibilityDay, regimeEligible }));
        return (universe, regime);
    }
}
