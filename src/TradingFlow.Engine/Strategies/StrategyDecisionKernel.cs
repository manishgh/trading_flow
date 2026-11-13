using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Deterministic strategy admission boundary. It consumes completed, point-in-time
/// evidence and returns qualification, arming, trigger, invalidation, and expiry.
/// Persistence, portfolio reservations, and broker routing happen after this pure step.
/// </summary>
public sealed class StrategyDecisionKernel : IStrategyDecisionKernel
{
    private readonly SignalGenerator signalGenerator = new();
    private readonly StrategyDecisionBrain entryEvaluator = new();
    private readonly StrategySessionClock sessionClock = new();
    private readonly CompletedBarExecutionPlanner executionPlanner = new();
    private readonly StrategyInitialStopResolver stopResolver = new();

    // Evaluate shared completed entry evidence; only the declared day lifecycle may carry continuation protection.
    public StrategyDecisionResult Evaluate(StrategyDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Strategy.EntryRules.OpeningRangeBreakout is { } openingRules) openingRules.Validate(request.Strategy);
        else TechnicalExecutionEngine.RequireSupportedExitPolicy(request.Strategy);
        var validatedFactor = ValidateRequest(request);
        var drawdown = request.Strategy.Timeframe == "1d"
            ? DrawdownQualificationAnalyzer.Analyze(request.BarsByTimeframe[request.Strategy.Timeframe],
                request.SnapshotsByTimeframe[request.Strategy.Timeframe],
                request.BarsByTimeframe[request.Strategy.Timeframe].Length - 1, request.AsOfUtc,
                request.Strategy.EntryRules.DrawdownQualification, context: request.DrawdownContext)
            : null;

        var rules = new List<StrategyDecisionRule>();
        var transitions = new List<StrategyCandidateTransition>();
        var asOfUtc = request.AsOfUtc.ToUniversalTime();
        var currentState = request.Candidate.State;

        // Bind consumed evidence to the semantic hash while preserving adapter details separately in the audit.
        StrategyDecisionResult Finish(
            StrategyCandidateState state,
            string? direction = null,
            TradeSignal? signal = null,
            StrategyOrderPlan? orderPlan = null,
            string? noEntryReason = null)
        {
            object semantic = new
            {
                schemaVersion = 1,
                strategy = request.StrategyIdentity,
                admissionProfile = request.AdmissionProfile,
                candidate = request.Candidate.Identity,
                discovery = new
                {
                    request.Candidate.Discovery.IsPersisted,
                    request.Candidate.Discovery.IsActive,
                    request.Candidate.Discovery.ExpiresAtUtc
                },
                marketState = new
                {
                    request.Candidate.MarketState.IsWarm,
                    request.Candidate.MarketState.StateFingerprint,
                    request.Candidate.MarketState.Provider,
                    request.Candidate.MarketState.Feed,
                    request.Candidate.MarketState.AdjustmentPolicy,
                    request.Candidate.MarketState.Reliability,
                    request.Candidate.MarketState.CompletedBarsByTimeframe,
                    request.Candidate.MarketState.RequiredWarmupBarsByTimeframe
                },
                universeEligible = request.Candidate.Universe.IsEligible,
                regimeEligible = request.Candidate.Regime.IsEligible,
                asOfUtc,
                state,
                direction,
                orderPlan,
                noEntryReason,
                rules = rules.Select(rule => new { rule.Stage, rule.Code, rule.Passed }).ToArray(),
                catalysts = AvailableCatalysts(request.Catalysts, asOfUtc)
                    .Select(ToSemanticCatalyst)
                    .ToArray()
            };
            if (request.Strategy.EntryRules.DrawdownQualification is { Mode: "gate" } activeDrawdown &&
                (activeDrawdown.UsesSectorContext || activeDrawdown.UsesEventContext || activeDrawdown.MinimumMaturedEpisodes > 0))
            {
                // Bind consumed context only for gates that use it. Legacy/default hashes remain exact.
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(semantic))!;
                var history = activeDrawdown.MinimumMaturedEpisodes > 0 ? request.DrawdownContext?.History : null;
                var source = request.BarsByTimeframe[request.Strategy.Timeframe];
                fields["drawdownContext"] = JsonSerializer.SerializeToElement(new {
                    context = drawdown?.Context,
                    history = history is null ? null : new { history.SecurityId, history.AdjustmentVersion,
                        history.Identity, history.CalendarKnownAtUtc, history.CalendarSha256,
                        expectedSessionTimestamps = history.ExpectedSessionTimestamps.Where(time =>
                            time >= source[0].Timestamp && time <= source[^1].Timestamp).ToArray() } });
                semantic = fields;
            }
            if (request.Strategy.EntryRules.HistoricalNewsDiagnostic is not null)
            {
                // Exact assumed clocks and consumed revisions are part of this Research decision identity.
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    EvidenceCanonicalJson.SerializeToUtf8Bytes(semantic))!;
                fields["historicalNewsPresence"] = request.HistoricalNewsPresence is { } presence
                    ? JsonSerializer.Deserialize<JsonElement>(EvidenceCanonicalJson.SerializeToUtf8Bytes(presence))
                    : JsonSerializer.SerializeToElement<object?>(null);
                semantic = fields;
            }
            var audit = new
            {
                schemaVersion = 1,
                semantic,
                discovery = request.Candidate.Discovery,
                marketState = request.Candidate.MarketState,
                market = CreateMarketAudit(request),
                attachedCatalyst = signal?.Catalyst is null ? null : AvailableCatalysts(request.Catalysts, asOfUtc)
                    .LastOrDefault(item => item.Event == signal.Catalyst),
                universe = request.Candidate.Universe,
                regime = request.Candidate.Regime,
                rules,
                drawdownQualification = drawdown
            };
            var canonical = EvidenceCanonicalJson.SerializeToUtf8Bytes(audit);
            return new StrategyDecisionResult(
                state,
                direction,
                signal,
                rules.ToImmutableArray(),
                transitions.ToImmutableArray(),
                orderPlan,
                noEntryReason,
                EvidenceCanonicalJson.ComputeSha256(semantic),
                Encoding.UTF8.GetString(canonical),
                drawdown);
        }

        if (!request.Candidate.Discovery.IsPersisted)
        {
            AddRule(rules, "qualification", "candidate_not_persisted", false, "discovery", request.Candidate.Discovery);
            var failedState = FailAdmission(ref currentState, "candidate_not_persisted", asOfUtc, transitions);
            return Finish(failedState, noEntryReason: "candidate_not_persisted");
        }

        AddRule(rules, "qualification", "candidate_persisted", true, "discovery", request.Candidate.Discovery);
        if (!request.Candidate.Discovery.IsActive || request.Candidate.Discovery.ExpiresAtUtc <= asOfUtc)
        {
            AddRule(rules, "qualification", "candidate_expired", false, "discovery", new
            {
                request.Candidate.Discovery.IsActive,
                request.Candidate.Discovery.ExpiresAtUtc,
                asOfUtc
            });
            Move(ref currentState, StrategyCandidateState.Expired, "candidate_expired", asOfUtc, transitions);
            return Finish(StrategyCandidateState.Expired, noEntryReason: "candidate_expired");
        }

        AddRule(rules, "qualification", "candidate_fresh", true, "discovery", new
        {
            request.Candidate.Discovery.ObservedAtUtc,
            request.Candidate.Discovery.ExpiresAtUtc,
            request.Candidate.Discovery.AggregateVersion
        });

        if (!request.Candidate.MarketState.IsWarm)
        {
            AddRule(rules, "qualification", "market_state_not_warm", false, "market_state", request.Candidate.MarketState);
            var warmingState = currentState == StrategyCandidateState.Armed
                ? StrategyCandidateState.DataError
                : StrategyCandidateState.DataWarming;
            Move(ref currentState, warmingState, "market_state_not_warm", asOfUtc, transitions);
            return Finish(warmingState, noEntryReason: "market_state_not_warm");
        }

        AddRule(rules, "qualification", "market_state_warm", true, "market_state", request.Candidate.MarketState);
        if (currentState == StrategyCandidateState.Discovered)
        {
            Move(
                ref currentState,
                StrategyCandidateState.DataWarming,
                "market_state_evaluation_started",
                asOfUtc,
                transitions);
        }

        if (!request.Candidate.Universe.IsEligible)
        {
            AddRule(rules, "qualification", "universe_not_eligible", false, "universe", request.Candidate.Universe);
            var failedState = FailAdmission(ref currentState, "universe_not_eligible", asOfUtc, transitions);
            return Finish(failedState, noEntryReason: "universe_not_eligible");
        }

        AddRule(rules, "qualification", "universe_eligible", true, "universe", request.Candidate.Universe);
        if (!request.Candidate.Regime.IsEligible)
        {
            AddRule(rules, "qualification", "market_regime_off", false, "regime", request.Candidate.Regime);
            var failedState = FailAdmission(ref currentState, "market_regime_off", asOfUtc, transitions);
            return Finish(failedState, noEntryReason: "market_regime_off");
        }

        AddRule(rules, "qualification", "market_regime_on", true, "regime", request.Candidate.Regime);
        var setupBars = request.BarsByTimeframe[request.Strategy.Timeframe];
        var setupSnapshots = request.SnapshotsByTimeframe[request.Strategy.Timeframe];
        var latest = setupSnapshots[^1];
        var setupAvailableAt = IsDecisionHour(request) ? asOfUtc : latest.Timestamp.Add(TimeframeParser.Parse(request.Strategy.Timeframe));
        if (request.Strategy.EntryRules.OpeningRangeBreakout is not null)
        {
            var evaluation = request.PreviousSessionContext is { } context
                ? new OpeningRangeBreakoutEvaluator().Evaluate(request.Strategy, setupBars, setupSnapshots, context, asOfUtc)
                : new IntradayBreakoutEvaluation(null, "opening_range_context_unavailable");
            AddRule(rules, "qualification", evaluation.RejectionReason ?? "opening_range_qualified",
                evaluation.Signal is not null, "opening_range_breakout", new { evaluation, context = request.PreviousSessionContext });
            if (evaluation.Signal is not { } pending)
            {
                var failedState = FailAdmission(ref currentState, evaluation.RejectionReason!, asOfUtc, transitions);
                return Finish(failedState, noEntryReason: evaluation.RejectionReason);
            }
            Move(ref currentState, StrategyCandidateState.Qualified, "opening_range_qualified", asOfUtc, transitions);
            Move(ref currentState, StrategyCandidateState.Armed, pending.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket ? "next_open_market_prepared" : "pending_stop_market_prepared", asOfUtc, transitions);
            var plan = new StrategyOrderPlan(request.Strategy.StrategyId, request.Strategy.StrategyName,
                pending.Direction, pending.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket ? asOfUtc : pending.OpeningRangeCompletedAtUtc!.Value, pending.ActiveAtUtc, "5m",
                pending.BreakoutIndex, pending.LimitPrice, pending.InitialStopPrice,
                request.Strategy.ExitRules.TargetRMultiple, request.Strategy.ExitRules.ProfitTargetMode, null,
                request.Strategy.Execution.SlippageBps, pending.ExpiresAtUtc, pending.ConfirmationTimestampUtc, pending);
            Move(ref currentState, StrategyCandidateState.Triggered, pending.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket ? "next_open_market_committed" : "pending_stop_market_activated", asOfUtc, transitions);
            return Finish(StrategyCandidateState.Triggered, pending.Direction, orderPlan: plan);
        }
        if (!sessionClock.ValidateExecutionWindow(latest.Timestamp, request.Strategy.Timeframe, request.Strategy.Session))
        {
            AddRule(rules, "qualification", "outside_session_window", false, "exchange_calendar", new
            {
                latest.Timestamp,
                setupAvailableAt,
                request.Strategy.Session
            });
            var failedState = FailAdmission(ref currentState, "outside_session_window", asOfUtc, transitions);
            return Finish(failedState, noEntryReason: "outside_session_window");
        }

        if (request.Strategy.EntryRules.DecisionSchedule is { } schedule &&
            ScheduleRejection(schedule, latest.Timestamp, request.NextExchangeSessionDate, rules) is { } scheduleReason)
        {
            var failedState = FailAdmission(ref currentState, scheduleReason, asOfUtc, transitions);
            return Finish(failedState, noEntryReason: scheduleReason);
        }

        AddRule(rules, "qualification", "inside_session_window", true, "exchange_calendar", new
        {
            latest.Timestamp,
            setupAvailableAt,
            request.Strategy.Session.UseExtendedHours
        });

        var pointInTimeSnapshots = AttachPointInTimeCatalysts(
            request.SnapshotsByTimeframe,
            request.Catalysts,
            asOfUtc);
        var signal = signalGenerator.CreateTradeSignal(
            request.Strategy,
            setupBars,
            pointInTimeSnapshots[request.Strategy.Timeframe],
            setupSnapshots.Length - 1, request.ReferenceBars?.ToArray(), request.AsOfUtc, request.ReferenceSessionDates?.ToArray(), validatedFactor);
        if (signal is null)
        {
            AddRule(rules, "qualification", "setup_signal_not_available", false, "signal_generator", new
            {
                request.Strategy.EntryRules.SetupType,
                latest.Timestamp,
                request.Candidate.MarketState.CompletedBarsByTimeframe,
                request.Candidate.MarketState.RequiredWarmupBarsByTimeframe
            });
            var failedState = FailAdmission(ref currentState, "setup_signal_not_available", asOfUtc, transitions);
            return Finish(failedState, noEntryReason: "setup_signal_not_available");
        }

        AddRule(rules, "qualification", "setup_signal_available", true, "signal_generator", new
        {
            signal.Timestamp,
            signal.Timeframe,
            signal.CurrentPrice,
            signal.CurrentVolume
        });
        var confluenceRejection = signalGenerator.GetConfluenceRejection(
            request.Strategy,
            setupAvailableAt,
            pointInTimeSnapshots.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IndicatorSnapshot>)pair.Value,
                StringComparer.OrdinalIgnoreCase));
        if (confluenceRejection is not null)
        {
            AddRule(rules, "qualification", confluenceRejection, false, "confluence", new
            {
                request.Strategy.Confluence,
                setupAvailableAt
            });
            var failedState = FailAdmission(ref currentState, confluenceRejection, asOfUtc, transitions);
            return Finish(failedState, signal: signal, noEntryReason: confluenceRejection);
        }

        AddRule(rules, "qualification", "confluence_satisfied", true, "confluence", new
        {
            request.Strategy.Confluence,
            setupAvailableAt
        });

        var (direction, longRejection, shortRejection, relativeVolume) = ResolveEntryDirection(request.Strategy, signal, latest, entryEvaluator);
        if (direction is null)
        {
            var reason = ChoosePrimaryRejection(longRejection!, shortRejection!);
            AddRule(rules, "qualification", reason, false, "strategy_entry_rules", new
            {
                longRejection,
                shortRejection,
                relativeVolume,
                strategyEntryRules = request.Strategy.EntryRules,
                signal
            });
            var failedState = FailAdmission(ref currentState, reason, asOfUtc, transitions);
            return Finish(failedState, signal: signal, noEntryReason: reason);
        }

        AddRule(rules, "qualification", "strategy_admission_satisfied", true, "strategy_entry_rules", new
        {
            direction,
            relativeVolume,
            strategyEntryRules = request.Strategy.EntryRules,
            signal
        });
        if (currentState == StrategyCandidateState.Disarmed)
        {
            Move(ref currentState, StrategyCandidateState.DataWarming, "candidate_requalification_started", asOfUtc, transitions);
        }

        if (currentState == StrategyCandidateState.DataWarming)
        {
            Move(ref currentState, StrategyCandidateState.Qualified, "strategy_admission_satisfied", asOfUtc, transitions);
        }

        if (currentState == StrategyCandidateState.Qualified)
        {
            Move(ref currentState, StrategyCandidateState.Armed, "candidate_armed", asOfUtc, transitions);
        }

        if (currentState != StrategyCandidateState.Armed)
        {
            throw new InvalidOperationException(
                $"Candidate state {currentState} cannot enter trigger evaluation.");
        }

        var executionBars = request.BarsByTimeframe[request.Strategy.Execution.Timeframe];
        var executionSnapshots = pointInTimeSnapshots[request.Strategy.Execution.Timeframe];
        var side = direction == "long" ? PlannedOrderSide.Long : PlannedOrderSide.Short;
        var trigger = executionPlanner.Plan(new CompletedBarExecutionRequest(
            request.Strategy,
            signal,
            side,
            executionBars,
            executionSnapshots,
            timestamp => sessionClock.ValidateExecutionWindow(
                timestamp,
                request.Strategy.Execution.Timeframe,
                request.Strategy.Session),
            RequireKnownFillBar: false));
        if (!trigger.IsReady ||
            trigger.StopContextIndex is not { } stopContextIndex ||
            trigger.ConfirmationIndex is not { } confirmationIndex)
        {
            var reason = trigger.RejectionReason ?? "execution_trigger_not_ready";
            AddRule(rules, "trigger", reason, false, "execution_confirmation", new
            {
                request.Strategy.Execution,
                setupAvailableAt
            });
            return Finish(StrategyCandidateState.Armed, direction, signal, noEntryReason: reason);
        }

        var triggerSnapshot = executionSnapshots[confirmationIndex];
        var initialStop = stopResolver.Resolve(new StrategyInitialStopRequest(
            request.Strategy,
            signal,
            side,
            triggerSnapshot.CurrentPrice,
            stopContextIndex,
            executionBars,
            executionSnapshots));
        if (!initialStop.IsResolved || initialStop.StopPrice is not { } invalidationPrice)
        {
            var reason = initialStop.RejectionReason ?? "initial_invalidation_not_resolved";
            AddRule(rules, "trigger", reason, false, "initial_stop", new
            {
                request.Strategy.ExitRules.InitialStopMode,
                stopContextIndex,
                triggerSnapshot.CurrentPrice
            });
            return Finish(StrategyCandidateState.Armed, direction, signal, noEntryReason: reason);
        }

        // Historical stop context is not the time an order became actionable.
        // In particular, a prior-session intraday close cannot backdate a daily
        // setup, a delayed receipt, or a later admission decision.
        // A decision-hour trigger is the session's provisional bar, known at the decision time rather than at its completion.
        var triggeredAtUtc = IsDecisionHour(request) && confirmationIndex == executionSnapshots.Length - 1
            ? asOfUtc
            : triggerSnapshot.Timestamp.Add(TimeframeParser.Parse(request.Strategy.Execution.Timeframe));
        if (triggeredAtUtc < setupAvailableAt) triggeredAtUtc = setupAvailableAt;
        if (triggeredAtUtc < asOfUtc) triggeredAtUtc = asOfUtc;
        var orderPlan = new StrategyOrderPlan(
            request.Strategy.StrategyId,
            request.Strategy.StrategyName,
            direction,
            setupAvailableAt,
            triggeredAtUtc,
            request.Strategy.Execution.Timeframe,
            stopContextIndex,
            triggerSnapshot.CurrentPrice,
            invalidationPrice,
            request.Strategy.ExitRules.TargetRMultiple,
            request.Strategy.ExitRules.ProfitTargetMode,
            request.Strategy.ExitRules.ProfitTargetMode.Equals("vwap", StringComparison.OrdinalIgnoreCase)
                ? triggerSnapshot.Vwap
                : null,
            request.Strategy.Execution.SlippageBps,
            request.Candidate.Discovery.ExpiresAtUtc,
            executionSnapshots[stopContextIndex].Timestamp.ToUniversalTime());
        AddRule(rules, "trigger", "execution_trigger_satisfied", true, "execution_confirmation", new
        {
            trigger.ConfirmationIndex,
            trigger.StopContextIndex,
            orderPlan
        });
        if (request.Strategy.EntryRules.DrawdownQualification is { Mode: "gate" } && drawdown is not null)
        {
            var passed = drawdown.Verdict == "qualifies";
            var reason = passed ? drawdown.Reason : $"drawdown_{drawdown.Verdict}:{drawdown.Reason}";
            AddRule(rules, "qualifier", reason, passed, DrawdownQualificationRules.ContractVersion, drawdown);
            if (!passed)
            {
                // This opportunity is decided at its original trigger; it must not wait
                // for another bar to make the optional qualifier pass.
                Move(ref currentState, StrategyCandidateState.Rejected, reason, asOfUtc, transitions);
                return Finish(StrategyCandidateState.Rejected, direction, signal, noEntryReason: reason);
            }
        }
        if (request.Strategy.EntryRules.HistoricalNewsDiagnostic is not null)
        {
            var diagnostic = HistoricalNewsPresenceEvaluation.Evaluate(request);
            rules.AddRange(diagnostic.Rules);
            if (diagnostic.RejectionReason is { } reason)
            {
                // Reject this original opportunity permanently; a later query/revision cannot postpone its trigger.
                Move(ref currentState, StrategyCandidateState.Rejected, reason, asOfUtc, transitions);
                return Finish(StrategyCandidateState.Rejected, direction, signal, noEntryReason: reason);
            }
        }
        Move(ref currentState, StrategyCandidateState.Triggered, "execution_trigger_satisfied", asOfUtc, transitions);
        return Finish(StrategyCandidateState.Triggered, direction, signal, orderPlan);
    }

    /// <summary>
    /// Records whether the setup bar's session is on the entry decision schedule and returns the rejection reason when it is
    /// not. The next trading session comes from the exchange calendar; without it the schedule cannot be judged, so the
    /// decision fails with a data error rather than trading on an unknown day.
    /// </summary>
    private static string? ScheduleRejection(string schedule, DateTimeOffset setupBarStart, DateOnly? nextSession,
        List<StrategyDecisionRule> rules)
    {
        var session = ExchangeSessionCalendar.SessionDate(setupBarStart);
        if (nextSession is not { } next)
            throw new InvalidDataException(
                $"Entry decision schedule {schedule} needs the exchange calendar's next session after {session:yyyy-MM-dd}; it was not supplied.");
        var onSchedule = DecisionSchedules.IsDecisionSession(schedule, session, next);
        AddRule(rules, "qualification", onSchedule ? "on_decision_schedule" : "not_on_decision_schedule", onSchedule,
            "exchange_calendar", new { schedule, session, nextSession = next });
        return onSchedule ? null : "not_on_decision_schedule";
    }

    // Verify immutable identity, policy/mode binding and aligned completed inputs before computing any strategy fields.
    private static TechnicalFactorEvidence? ValidateRequest(StrategyDecisionRequest request)
    {
        RequireUtc(request.AsOfUtc, nameof(request.AsOfUtc));
        HistoricalNewsPresenceEvaluation.ValidateBinding(request);
        if (request.Candidate.CandidateId == Guid.Empty ||
            String.IsNullOrWhiteSpace(request.Candidate.Identity.Symbol) ||
            String.IsNullOrWhiteSpace(request.Candidate.Identity.SetupKey))
        {
            throw new InvalidOperationException("Candidate identity, symbol, and setup key are required.");
        }

        // The canonical document binds the source definition (including a research alias)
        // to the catalog identity. String equality of those IDs is not an identity proof.
        if (!request.Candidate.Identity.StrategyContentSha256.Equals(request.StrategyIdentity.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Candidate, strategy definition, and immutable identity do not match.");
        }

        var canonicalIdentity = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
            request.StrategyIdentity.StrategyId,
            request.StrategyIdentity.SemanticVersion,
            request.Strategy,
            request.AdmissionProfile));
        if (!canonicalIdentity.Equals(request.StrategyIdentity.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Strategy definition, admission profile, and immutable content hash do not match.");
        }

        if (request.AdmissionProfile.ExecutionTimingPolicy is not
            ("completed-bar-next-executable-bar-v1" or "completed-bar-asof-regime-scheduled-expiry-v2" or
             "completed-bar-next-slice-market-exits-v3" or ExecutionRules.KeptGapTimingPolicy or ExecutionRules.KeptGapAsTradedTimingPolicy or
             ExecutionRules.DecisionHourAsTradedTimingPolicy or
             "completed-bar-precommitted-terminal-open-v4" or "completed-bar-precommitted-terminal-owned-v5" or
             OpeningRangeBreakoutRules.ExecutionTimingPolicy or OpeningRangeBreakoutRules.NextBarMarketTimingPolicy))
        {
            throw new InvalidOperationException(
                $"Unsupported execution timing policy '{request.AdmissionProfile.ExecutionTimingPolicy}'.");
        }

        ValidateDiscovery(request.Candidate);
        ValidateMarketEvidence(request.Candidate.MarketState,
            MarketEvidenceOperationalRules.ForExecutionTimingPolicy(request.AdmissionProfile.ExecutionTimingPolicy));
        if (IsDecisionHour(request)) RequireDecisionHourRequest(request);
        var requiredTimeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            request.Strategy.Timeframe,
            request.Strategy.Execution.Timeframe
        };
        if (request.Strategy.Confluence.Enabled)
        {
            requiredTimeframes.Add(request.Strategy.Confluence.Timeframe);
        }

        foreach (var timeframe in requiredTimeframes)
        {
            if (!request.BarsByTimeframe.TryGetValue(timeframe, out var bars) ||
                !request.SnapshotsByTimeframe.TryGetValue(timeframe, out var snapshots))
            {
                throw new InvalidOperationException($"Required timeframe {timeframe} is unavailable.");
            }

            ValidateAlignedCompletedSeries(
                request.Candidate.Identity.Symbol,
                timeframe,
                bars,
                snapshots,
                request.AsOfUtc,
                request.Candidate.MarketState,
                provisionalLast: IsDecisionHour(request));
        }

        if (request.Strategy.EntryRules.DrawdownQualification is { } drawdownRules)
        {
            drawdownRules.Validate();
            if (request.Strategy.Timeframe != "1d")
                throw new InvalidOperationException("Drawdown qualification requires completed native daily evidence.");
        }
        if (request.Strategy.EntryRules.TechnicalFactor is { } factor)
        {
            factor.Validate();
            if (request.AdmissionProfile.Indicators.DerivedIndicatorProfileVersion != "2.2.0" ||
                request.Strategy.Timeframe != "1d" || request.Strategy.Session.IsContinuousMarket ||
                request.Strategy.Session.ExchangeTimezone != "America/New_York" ||
                !request.Strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Technical factors require the versioned 2.2.0 long native-daily profile.");
        }
        if (request.ReferenceBars is { } reference)
        {
            var referenceFactor = request.Strategy.EntryRules.TechnicalFactor;
            if (referenceFactor?.Metric != TechnicalFactorRules.RelativeRoc)
                throw new InvalidOperationException("Undeclared benchmark reference evidence.");
            DateTimeOffset? previous = null;
            foreach (var bar in reference)
            {
                if (bar.Ticker != referenceFactor.BenchmarkSymbol || bar.Timeframe != "1d" ||
                    previous is { } prior && bar.Timestamp <= prior || bar.Timestamp.AddDays(1) > request.AsOfUtc ||
                    bar.KnownAtUtc is { } known && known > request.AsOfUtc ||
                    bar.DataFeed != request.Candidate.MarketState.Feed || bar.AdjustmentPolicy != request.Candidate.MarketState.AdjustmentPolicy)
                    throw new InvalidOperationException("Benchmark evidence is duplicated, future-known or has mismatched provenance.");
                previous = bar.Timestamp;
            }
        }
        foreach (var catalyst in request.Catalysts)
        {
            ValidateCatalyst(catalyst, request.Candidate.Identity.Symbol);
        }
        if (request.Strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } requiredFactor &&
            request.Candidate.MarketState.IsWarm && request.Candidate.Universe.IsEligible)
        {
            var setup = request.BarsByTimeframe[request.Strategy.Timeframe];
            return TechnicalFactorCalculator.Analyze(requiredFactor, setup,
                request.SnapshotsByTimeframe[request.Strategy.Timeframe], setup.Length - 1,
                request.ReferenceBars?.ToArray(), request.AsOfUtc, request.ReferenceSessionDates?.ToArray());
        }
        return null;
    }

    /// <summary>
    /// The entry-direction stage of the shared decision path: the strategy's long and short entry rules applied to a setup
    /// signal and its completed snapshot. Returns the admitted direction (long first, null when neither passes), each side's
    /// rejection and the relative volume the rules read. Research signal studies call it too, so they never re-implement it.
    /// </summary>
    public static (string? Direction, string? LongRejection, string? ShortRejection, decimal? RelativeVolume) ResolveEntryDirection(
        StrategyDefinition strategy, TradeSignal signal, IndicatorSnapshot latest, StrategyDecisionBrain? evaluator = null)
    {
        evaluator ??= new StrategyDecisionBrain();
        var relativeVolume = StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, latest);
        var longRejection = AllowsLong(strategy) ? evaluator.GetLongEntryRejection(strategy, signal, latest, relativeVolume) : "direction_not_long";
        var shortRejection = AllowsShort(strategy) ? evaluator.GetShortEntryRejection(strategy, signal, latest, relativeVolume) : "direction_not_short";
        return (longRejection is null ? "long" : shortRejection is null ? "short" : null, longRejection, shortRejection, relativeVolume);
    }

    /// <summary>True when the request is bound to the decision-hour timing (daily rules decided one hour before the close).</summary>
    private static bool IsDecisionHour(StrategyDecisionRequest request) =>
        request.AdmissionProfile.ExecutionTimingPolicy == ExecutionRules.DecisionHourAsTradedTimingPolicy;

    /// <summary>
    /// A session's provisional daily bar (DecisionHourDailyBars): it opened before the decision, is known exactly at it and
    /// would complete only after it. Any other last bar would mean the decision read a completed or a future session.
    /// </summary>
    private static bool IsProvisionalAt(OhlcvBar bar, TimeSpan duration, DateTimeOffset asOfUtc) =>
        bar.KnownAtUtc == asOfUtc && bar.Timestamp < asOfUtc && bar.Timestamp.Add(duration) > asOfUtc;

    /// <summary>
    /// Refuses decision-hour requests outside the implemented path: native daily setup and execution on one series with no
    /// confirmation bar, confluence, drawdown gate or technical factor, which would read evidence this timing does not supply.
    /// </summary>
    private static void RequireDecisionHourRequest(StrategyDecisionRequest request)
    {
        var strategy = request.Strategy;
        if (strategy.Timeframe != "1d" || strategy.Execution.Timeframe != "1d" || strategy.Execution.EffectiveConfirmation.Enabled ||
            strategy.Confluence.Enabled || strategy.EntryRules.DrawdownQualification is not null ||
            strategy.EntryRules.TechnicalFactor is not null || strategy.EntryRules.OpeningRangeBreakout is not null)
            throw new InvalidOperationException("The decision-hour timing supports daily rules on one daily series only, without " +
                "confirmation bars, confluence, drawdown gates, technical factors or opening ranges.");
    }

    private static void ValidateDiscovery(StrategyCandidateSnapshot candidate)
    {
        RequireUtc(candidate.Identity.DiscoveryWindowStartUtc, nameof(candidate.Identity.DiscoveryWindowStartUtc));
        RequireUtc(candidate.Identity.DiscoveryWindowEndUtc, nameof(candidate.Identity.DiscoveryWindowEndUtc));
        RequireUtc(candidate.Discovery.ObservedAtUtc, nameof(candidate.Discovery.ObservedAtUtc));
        RequireUtc(candidate.Discovery.ExpiresAtUtc, nameof(candidate.Discovery.ExpiresAtUtc));
        if (candidate.Identity.DiscoveryWindowEndUtc <= candidate.Identity.DiscoveryWindowStartUtc ||
            candidate.Discovery.AggregateId == Guid.Empty ||
            candidate.Discovery.AggregateVersion <= 0 ||
            candidate.Discovery.Sources.IsDefaultOrEmpty ||
            !IsSha256(candidate.Discovery.EvidenceSha256))
        {
            throw new InvalidOperationException("Discovery identity and sourced persistence evidence are incomplete.");
        }
    }

    /// <summary>Requires actual SIP provenance, warm bars and the price basis bound to the already hash-verified admission profile.</summary>
    private static void ValidateMarketEvidence(StrategyMarketStateEvidence evidence, MarketEvidenceOperationalRules rules)
    {
        RequireUtc(evidence.VerifiedAtUtc, nameof(evidence.VerifiedAtUtc));
        if (String.IsNullOrWhiteSpace(evidence.StateFingerprint) ||
            !evidence.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ||
            !evidence.Feed.Equals(rules.RequiredDataFeed, StringComparison.OrdinalIgnoreCase) ||
            !evidence.AdjustmentPolicy.Equals(rules.RequiredAdjustmentPolicy, StringComparison.OrdinalIgnoreCase) ||
            !evidence.Reliability.Equals("verified_same_feed", StringComparison.OrdinalIgnoreCase) ||
            evidence.CompletedBarsByTimeframe.IsEmpty ||
            evidence.RequiredWarmupBarsByTimeframe.IsEmpty ||
            evidence.RequiredWarmupBarsByTimeframe.Any(pair =>
                pair.Value <= 0 ||
                !evidence.CompletedBarsByTimeframe.TryGetValue(pair.Key, out var completed) ||
                (evidence.IsWarm && completed < pair.Value)))
        {
            throw new InvalidOperationException($"Authoritative warm market evidence requires Alpaca {rules.RequiredDataFeed}/{rules.RequiredAdjustmentPolicy} and sufficient verified bars.");
        }
    }

    private static void ValidateCatalyst(StrategyCatalystSnapshot catalyst, string symbol)
    {
        RequireUtc(catalyst.ProviderPublishedAtUtc, nameof(catalyst.ProviderPublishedAtUtc));
        RequireUtc(catalyst.DecisionKnownAtUtc, nameof(catalyst.DecisionKnownAtUtc));
        if (catalyst.ProviderUpdatedAtUtc is { } updatedAt)
        {
            RequireUtc(updatedAt, nameof(catalyst.ProviderUpdatedAtUtc));
        }

        if (catalyst.FirstReceivedAtUtc is not { } receivedAt)
        {
            throw new InvalidOperationException(
                "Catalyst decisions require observed provider receipt time.");
        }

        RequireUtc(receivedAt, nameof(catalyst.FirstReceivedAtUtc));

        if (String.IsNullOrWhiteSpace(catalyst.Provider) ||
            String.IsNullOrWhiteSpace(catalyst.ProviderArticleId) ||
            String.IsNullOrWhiteSpace(catalyst.RevisionId) ||
            String.IsNullOrWhiteSpace(catalyst.ClassificationVersion) ||
            String.IsNullOrWhiteSpace(catalyst.AvailabilityEvidence) ||
            catalyst.ClassificationVersion.Equals("unclassified-catalyst-v1", StringComparison.OrdinalIgnoreCase) ||
            catalyst.AvailabilityEvidence.Equals("provider_timestamp_only", StringComparison.OrdinalIgnoreCase) ||
            catalyst.DecisionKnownAtUtc < receivedAt ||
            !IsSha256(catalyst.DeduplicationKey) ||
            !catalyst.Event.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalyst revision identity, provenance, and symbol mapping are incomplete.");
        }
    }

    private static void ValidateAlignedCompletedSeries(
        string symbol,
        string timeframe,
        ImmutableArray<OhlcvBar> bars,
        ImmutableArray<IndicatorSnapshot> snapshots,
        DateTimeOffset asOfUtc,
        StrategyMarketStateEvidence marketEvidence,
        bool provisionalLast = false)
    {
        if (bars.Length < 2 || bars.Length != snapshots.Length)
        {
            throw new InvalidOperationException($"{timeframe} bars and snapshots must be aligned and contain at least two completed observations.");
        }

        var duration = TimeframeParser.Parse(timeframe);
        DateTimeOffset? previous = null;
        for (var index = 0; index < bars.Length; index++)
        {
            var bar = bars[index];
            var snapshot = snapshots[index];
            // Under the decision-hour timing the last bar is the session's provisional bar, known exactly at the decision.
            var provisional = provisionalLast && index == bars.Length - 1;
            if (!bar.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                !snapshot.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                !bar.Timeframe.Equals(timeframe, StringComparison.OrdinalIgnoreCase) ||
                !snapshot.Timeframe.Equals(timeframe, StringComparison.OrdinalIgnoreCase) ||
                bar.Timestamp != snapshot.Timestamp ||
                previous is { } prior && bar.Timestamp <= prior ||
                (provisional ? !IsProvisionalAt(bar, duration, asOfUtc) : bar.Timestamp.Add(duration) > asOfUtc) ||
                bar.KnownAtUtc is { } knownAt && knownAt > asOfUtc ||
                !bar.DataFeed.Equals(marketEvidence.Feed, StringComparison.OrdinalIgnoreCase) ||
                !bar.AdjustmentPolicy.Equals(marketEvidence.AdjustmentPolicy, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(snapshot.DataFeed, marketEvidence.Feed, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(snapshot.AdjustmentPolicy, marketEvidence.AdjustmentPolicy, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(snapshot.MarketEvidenceReliability, marketEvidence.Reliability, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{symbol} {timeframe} market evidence is misaligned, incomplete, future-known, or has mixed provenance at index {index}.");
            }

            previous = bar.Timestamp;
        }
    }

    private static ImmutableArray<StrategyCatalystSnapshot> AvailableCatalysts(
        ImmutableArray<StrategyCatalystSnapshot> catalysts,
        DateTimeOffset asOfUtc) => catalysts
        .Where(catalyst => catalyst.DecisionKnownAtUtc <= asOfUtc)
        .OrderBy(catalyst => catalyst.DecisionKnownAtUtc)
        .ThenBy(catalyst => catalyst.Provider, StringComparer.Ordinal)
        .ThenBy(catalyst => catalyst.ProviderArticleId, StringComparer.Ordinal)
        .ThenBy(catalyst => catalyst.RevisionId, StringComparer.Ordinal)
        .ToImmutableArray();

    private static object ToSemanticCatalyst(StrategyCatalystSnapshot catalyst) => new
    {
        catalyst.Provider,
        catalyst.ProviderArticleId,
        catalyst.RevisionId,
        providerPublishedAtUtc = catalyst.ProviderPublishedAtUtc.ToUniversalTime(),
        providerUpdatedAtUtc = catalyst.ProviderUpdatedAtUtc?.ToUniversalTime(),
        firstReceivedAtUtc = catalyst.FirstReceivedAtUtc?.ToUniversalTime(),
        decisionKnownAtUtc = catalyst.DecisionKnownAtUtc.ToUniversalTime(),
        catalyst.ClassificationVersion,
        catalyst.AvailabilityEvidence,
        catalyst.DeduplicationKey,
        catalyst.Event.Type,
        catalyst.Event.SentimentScore
    };

    private static ImmutableDictionary<string, ImmutableArray<IndicatorSnapshot>> AttachPointInTimeCatalysts(
        ImmutableDictionary<string, ImmutableArray<IndicatorSnapshot>> source,
        ImmutableArray<StrategyCatalystSnapshot> catalysts,
        DateTimeOffset asOfUtc)
    {
        var available = AvailableCatalysts(catalysts, asOfUtc);
        return source.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(snapshot =>
            {
                var barKnownAt = snapshot.Timestamp.Add(TimeframeParser.Parse(pair.Key));
                var catalyst = available.LastOrDefault(item => item.DecisionKnownAtUtc <= barKnownAt);
                return snapshot with { Catalyst = catalyst?.Event };
            }).ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? CreateMarketAudit(StrategyDecisionRequest request)
    {
        var timeframe = request.Strategy.Execution.Timeframe;
        if (!request.SnapshotsByTimeframe.TryGetValue(timeframe, out var snapshots) || snapshots.IsDefaultOrEmpty) return null;
        var snapshot = snapshots[^1];
        return new {
            asOfUtc = request.AsOfUtc,
            latestCompletedCandleAtUtc = snapshot.Timestamp.Add(TimeframeParser.Parse(timeframe)),
            timeframe,
            session = snapshot.RelativeVolumeCohort ?? "unknown",
            lastPrice = snapshot.CurrentPrice,
            sameTimeRelativeVolume = snapshot.RelativeVolume,
            relativeVolumeSampleCount = snapshot.RelativeVolumeSampleCount
        };
    }
    private static bool AllowsLong(StrategyDefinition strategy) =>
        strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ||
        strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase);

    private static bool AllowsShort(StrategyDefinition strategy) =>
        strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
        strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase);

    private static string ChoosePrimaryRejection(string longRejection, string shortRejection) =>
        !longRejection.Equals("direction_not_long", StringComparison.Ordinal)
            ? longRejection
            : shortRejection;

    private static void Move(
        ref StrategyCandidateState current,
        StrategyCandidateState next,
        string reason,
        DateTimeOffset now,
        ICollection<StrategyCandidateTransition> transitions)
    {
        if (current == next)
        {
            return;
        }

        if (current == StrategyCandidateState.Discovered && next != StrategyCandidateState.DataWarming)
        {
            Move(ref current, StrategyCandidateState.DataWarming, "market_state_evaluation_started", now, transitions);
        }

        StrategyCandidateStateMachine.RequireTransition(current, next);
        transitions.Add(new StrategyCandidateTransition(current, next, now, reason));
        current = next;
    }

    private static StrategyCandidateState FailAdmission(
        ref StrategyCandidateState current,
        string reason,
        DateTimeOffset now,
        ICollection<StrategyCandidateTransition> transitions)
    {
        if (current is StrategyCandidateState.Disarmed or StrategyCandidateState.DataError)
        {
            Move(ref current, StrategyCandidateState.DataWarming, "candidate_requalification_started", now, transitions);
        }

        var failedState = current == StrategyCandidateState.Armed
            ? StrategyCandidateState.Disarmed
            : StrategyCandidateState.Rejected;
        Move(ref current, failedState, reason, now, transitions);
        return failedState;
    }

    private static void AddRule(
        ICollection<StrategyDecisionRule> rules,
        string stage,
        string code,
        bool passed,
        string source,
        object evidence) => rules.Add(new StrategyDecisionRule(
            stage,
            code,
            passed,
            source,
            JsonSerializer.Serialize(evidence)));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{name} must be UTC.");
        }
    }
}
