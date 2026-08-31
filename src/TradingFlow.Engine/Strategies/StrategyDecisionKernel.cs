using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
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

    public StrategyDecisionResult Evaluate(StrategyDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var rules = new List<StrategyDecisionRule>();
        var transitions = new List<StrategyCandidateTransition>();
        var asOfUtc = request.AsOfUtc.ToUniversalTime();
        var currentState = request.Candidate.State;

        StrategyDecisionResult Finish(
            StrategyCandidateState state,
            string? direction = null,
            TradeSignal? signal = null,
            StrategyOrderPlan? orderPlan = null,
            string? noEntryReason = null)
        {
            var semantic = new
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
            var audit = new
            {
                schemaVersion = 1,
                semantic,
                discovery = request.Candidate.Discovery,
                marketState = request.Candidate.MarketState,
                universe = request.Candidate.Universe,
                regime = request.Candidate.Regime,
                rules
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
                Encoding.UTF8.GetString(canonical));
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
        var setupAvailableAt = latest.Timestamp.Add(TimeframeParser.Parse(request.Strategy.Timeframe));
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
            setupSnapshots.Length - 1);
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

        var relativeVolume = StrategyDecisionBrain.ResolveEntryRelativeVolume(request.Strategy, latest);
        var longRejection = AllowsLong(request.Strategy)
            ? entryEvaluator.GetLongEntryRejection(request.Strategy, signal, latest, relativeVolume)
            : "direction_not_long";
        var shortRejection = AllowsShort(request.Strategy)
            ? entryEvaluator.GetShortEntryRejection(request.Strategy, signal, latest, relativeVolume)
            : "direction_not_short";
        var direction = longRejection is null ? "long" : shortRejection is null ? "short" : null;
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

        var orderPlan = new StrategyOrderPlan(
            request.Strategy.StrategyId,
            request.Strategy.StrategyName,
            direction,
            setupAvailableAt,
            triggerSnapshot.Timestamp.Add(TimeframeParser.Parse(request.Strategy.Execution.Timeframe)),
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
            request.Candidate.Discovery.ExpiresAtUtc);
        AddRule(rules, "trigger", "execution_trigger_satisfied", true, "execution_confirmation", new
        {
            trigger.ConfirmationIndex,
            trigger.StopContextIndex,
            orderPlan
        });
        Move(ref currentState, StrategyCandidateState.Triggered, "execution_trigger_satisfied", asOfUtc, transitions);
        return Finish(StrategyCandidateState.Triggered, direction, signal, orderPlan);
    }

    private static void ValidateRequest(StrategyDecisionRequest request)
    {
        RequireUtc(request.AsOfUtc, nameof(request.AsOfUtc));
        if (request.Candidate.CandidateId == Guid.Empty ||
            String.IsNullOrWhiteSpace(request.Candidate.Identity.Symbol) ||
            String.IsNullOrWhiteSpace(request.Candidate.Identity.SetupKey))
        {
            throw new InvalidOperationException("Candidate identity, symbol, and setup key are required.");
        }

        if (!request.Strategy.StrategyId.Equals(request.StrategyIdentity.StrategyId, StringComparison.Ordinal) ||
            !request.Candidate.Identity.StrategyContentSha256.Equals(request.StrategyIdentity.ContentSha256, StringComparison.Ordinal))
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

        if (!request.AdmissionProfile.ExecutionTimingPolicy.Equals(
                "completed-bar-next-executable-bar-v1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported execution timing policy '{request.AdmissionProfile.ExecutionTimingPolicy}'.");
        }

        ValidateDiscovery(request.Candidate);
        ValidateMarketEvidence(request.Candidate.MarketState);
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
                request.Candidate.MarketState);
        }

        foreach (var catalyst in request.Catalysts)
        {
            ValidateCatalyst(catalyst, request.Candidate.Identity.Symbol);
        }
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

    private static void ValidateMarketEvidence(StrategyMarketStateEvidence evidence)
    {
        RequireUtc(evidence.VerifiedAtUtc, nameof(evidence.VerifiedAtUtc));
        if (String.IsNullOrWhiteSpace(evidence.StateFingerprint) ||
            !evidence.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase) ||
            !evidence.Feed.Equals("sip", StringComparison.OrdinalIgnoreCase) ||
            !evidence.AdjustmentPolicy.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            !evidence.Reliability.Equals("verified_same_feed", StringComparison.OrdinalIgnoreCase) ||
            evidence.CompletedBarsByTimeframe.IsEmpty ||
            evidence.RequiredWarmupBarsByTimeframe.IsEmpty ||
            evidence.RequiredWarmupBarsByTimeframe.Any(pair =>
                pair.Value <= 0 ||
                !evidence.CompletedBarsByTimeframe.TryGetValue(pair.Key, out var completed) ||
                (evidence.IsWarm && completed < pair.Value)))
        {
            throw new InvalidOperationException("Authoritative warm market evidence requires Alpaca SIP/all and sufficient verified bars.");
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
        StrategyMarketStateEvidence marketEvidence)
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
            if (!bar.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                !snapshot.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) ||
                !bar.Timeframe.Equals(timeframe, StringComparison.OrdinalIgnoreCase) ||
                !snapshot.Timeframe.Equals(timeframe, StringComparison.OrdinalIgnoreCase) ||
                bar.Timestamp != snapshot.Timestamp ||
                previous is { } prior && bar.Timestamp <= prior ||
                bar.Timestamp.Add(duration) > asOfUtc ||
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
