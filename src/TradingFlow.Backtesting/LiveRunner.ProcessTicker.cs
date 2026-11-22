using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Catalysts;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

// Per-ticker live processing (LiveRunner partial split).
public sealed partial class LiveRunner
{
    // Process chart and exit decisions before checking this run’s entry health and current membership.
    private async Task ProcessTickerLiveAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        string ticker,
        TickerMarketState marketState,
        CatalystStreamer catalystStreamer,
        EntryMembershipObservation membership,
        EntrySkipAudit entrySkipAudit,
        IReadOnlyCollection<string> activeTickers,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        bool brokerStateConfirmedForOrderDecisions,
        bool entriesStopped,
        Lazy<Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>?>> decisionCalendar,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var discoveryAggregate = membership.Eligible;
        var podId = Environment.MachineName;
        var lockAcquired = false;

        // 0. Acquire Distributed Lock
        if (_lockService != null)
        {
            var acquired = await _lockService.TryAcquireLockAsync(ticker, podId, TimeSpan.FromMinutes(3), cancellationToken);
            if (!acquired)
            {
                logger.LogDebug("Ticker {Ticker} is locked by another pod. Skipping...", ticker);
                return;
            }

            lockAcquired = true;
        }

        try
        {
        var barsByTimeframe = marketState.BarsByTimeframe
            .ToDictionary(
                x => x.Key,
                x => x.Value.OrderBy(bar => bar.Timestamp).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var snapshotsByTimeframe = marketState.SnapshotsByTimeframe
            .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CatalystEvent> catalysts = [];
        if (run.News.Enabled)
        {
            var allBars = barsByTimeframe.Values.SelectMany(x => x).ToArray();
            if (allBars.Length > 0)
            {
                catalysts = await catalystStreamer.LoadTickerCatalystsAsync(
                    ticker,
                    allBars.Min(x => x.Timestamp),
                    allBars.Max(x => x.Timestamp),
                    cancellationToken);
                foreach (var timeframe in snapshotsByTimeframe.Keys)
                {
                    CatalystSnapshotAttacher.AttachToSnapshots(snapshotsByTimeframe[timeframe], catalysts, cancellationToken);
                }
            }
        }

        var now = _timeProvider.GetUtcNow();
        var completedBarsByTimeframe = barsByTimeframe.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Where(bar => bar.Timestamp.Add(TimeframeParser.Parse(pair.Key)) <= now)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        var completedSnapshotsByTimeframe = snapshotsByTimeframe.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IndicatorSnapshot>)pair.Value
                .Where(snapshot => snapshot.Timestamp.Add(TimeframeParser.Parse(pair.Key)) <= now)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName, ticker);
        Directory.CreateDirectory(resultsDir);

        // 3. Process Strategies & Write Chart Data
        foreach (var strategy in strategies)
        {
            if (!completedSnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var snapshots) || snapshots.Count == 0)
                continue;

            if (!completedBarsByTimeframe.TryGetValue(strategy.Timeframe, out var barsList) || barsList.Count == 0)
                continue;

            var lastSnapshot = snapshots[^1];
            var effectiveRelativeVolume = StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, lastSnapshot);
            var relativeVolumeSource = strategy.EntryRules.MinVolumeSpikeSource.ToConfigValue();

            var chartData = new
            {
                Timestamp = lastSnapshot.Timestamp,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timeframe = lastSnapshot.Timeframe,
                Close = lastSnapshot.CurrentPrice,
                Atr = lastSnapshot.Atr ?? 0m,
                RelativeVolume = effectiveRelativeVolume,
                SlotRelativeVolume = lastSnapshot.SlotRelativeVolume,
                CalculatedRelativeVolume = lastSnapshot.RelativeVolume,
                RelativeVolumeSource = relativeVolumeSource,
                SlotMedianVolume = lastSnapshot.SlotMedianVolume,
                CumulativeSameTimeMedianVolume = lastSnapshot.CumulativeSameTimeMedianVolume,
                RelativeVolumeSampleCount = lastSnapshot.RelativeVolumeSampleCount,
                SlotRelativeVolumeSampleCount = lastSnapshot.SlotRelativeVolumeSampleCount,
                MarketEvidenceProfileVersion = lastSnapshot.MarketEvidenceProfileVersion,
                RelativeVolumeCohort = lastSnapshot.RelativeVolumeCohort,
                DataFeed = lastSnapshot.DataFeed,
                AdjustmentPolicy = lastSnapshot.AdjustmentPolicy,
                MarketEvidenceReliability = lastSnapshot.MarketEvidenceReliability,
                Volume = lastSnapshot.CurrentVolume,
                Rsi = lastSnapshot.Rsi,
                Vwap = lastSnapshot.Vwap
            };
            var json = System.Text.Json.JsonSerializer.Serialize(chartData);
            await _artifactWriter.WriteTextAsync(Path.Combine(resultsDir, $"{ticker}_chart.json"), json, cancellationToken);

            if (!brokerStateConfirmedForOrderDecisions)
            {
                const string reason = "broker_state_unavailable_for_order_decisions";
                logger.LogWarning(
                    "Skipping order decisions for {Ticker} and {Strategy} because the broker order/position snapshot is incomplete.",
                    ticker,
                    strategy.StrategyName);
                if (_auditRepo is not null)
                {
                    await _auditRepo.SaveAuditAsync(
                        new DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = DateTimeOffset.UtcNow,
                            Decision = "Skipped",
                            RejectionReason = reason,
                            SignalJson = JsonSerializer.Serialize(new
                            {
                                ticker,
                                strategy = strategy.StrategyName,
                                brokerStateConfirmed = false
                            })
                        },
                        cancellationToken);
                }

                continue;
            }

            var technicalExitHandled = await TrySubmitTechnicalExitAsync(
                run,
                strategy,
                ticker,
                completedBarsByTimeframe,
                completedSnapshotsByTimeframe,
                openOrders,
                openPositions,
                cancellationToken,
                progress);
            if (technicalExitHandled || entriesStopped)
            {
                continue;
            }

            if (!HasEntryMembership(run, ticker, discoveryAggregate, now))
            {
                await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                    new EntryCheck("before_entry_decision", now), cancellationToken);
                continue;
            }

            // Check for existing position/order limits
            var activeCount = activeTickers.Count(t => t.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            if (run.Portfolio.MaxOpenTradesPerTicker > 0 && activeCount >= run.Portfolio.MaxOpenTradesPerTicker)
            {
                var reason = $"max_open_trades_per_ticker_reached (Actual: {activeCount}, Allowed: {run.Portfolio.MaxOpenTradesPerTicker})";
                logger.LogDebug("Skipping {Ticker} because {Count} open position(s) or order(s) already exist, hitting the limit of {Max}.", ticker, activeCount, run.Portfolio.MaxOpenTradesPerTicker);
                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = DateTimeOffset.UtcNow,
                        Decision = "Skipped",
                        RejectionReason = reason,
                        SignalJson = JsonSerializer.Serialize(new
                        {
                            ticker,
                            strategy = strategy.StrategyName,
                            activeCount,
                            maxOpenTradesPerTicker = run.Portfolio.MaxOpenTradesPerTicker
                        })
                    }, cancellationToken);
                }

                continue;
            }

            // Per stock: the benchmark, under the Nasdaq home-market rule the listing (from the broker's asset record) and QQQ,
            // and with VIX sizing the previous session's VIX close (refusal, or the risk scale applied when the order is sized).
            var regimeDecision = strategy.Regime is not { IsActive: true } regimeRule ? RegimeEntryDecision.Allowed :
                await _regimeGate.EntryDecisionAsync(regimeRule, provider, run.Intervals, now, ticker, _brokerClient, cancellationToken);
            var regimeRejection = regimeDecision.Rejection;
            var regimeOn = regimeRejection is null;

            if (_candidateDecisions is null || _executionRunContext is null)
            {
                throw new InvalidOperationException(
                    "Live strategy admission requires durable candidate persistence and execution provenance.");
            }

            if (!runtimeStrategies.TryGetValue(strategy, out var runtimeStrategy))
            {
                throw new UnauthorizedAccessException(
                    $"Strategy '{strategy.StrategyId}' has no exact runtime authorization identity.");
            }

            var decisionTimestamp = now;
            var setupAvailableAtUtc = lastSnapshot.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null;
            if (!strategy.Session.IsContinuousMarket)
            {
                try
                {
                    schedules = await decisionCalendar.Value;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The poll records the shared calendar failure after all technical exits have run.
                    continue;
                }
            }
            var entryWindow = NextRegularOpenEntryWindow.Resolve(strategy, lastSnapshot.Timestamp, schedules);
            if (entryWindow is not null)
            {
                // Late provider receipt cannot move the opportunity to another session.
                var received = barsList.LastOrDefault(bar => bar.Timestamp == lastSnapshot.Timestamp)?.KnownAtUtc;
                if (received > setupAvailableAtUtc) setupAvailableAtUtc = received.Value;
                if (now >= entryWindow.EndUtc || setupAvailableAtUtc >= entryWindow.EndUtc)
                {
                    await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                        new EntryCheck("entry_window", now, entryWindow.EndUtc, SetupDeadlineElapsed: true,
                            EntryWindow: entryWindow, SetupAvailableAtUtc: setupAvailableAtUtc), cancellationToken);
                    continue;
                }
                if (now < entryWindow.OpenUtc || now < setupAvailableAtUtc) continue;
            }
            var candidateExpiresAtUtc = entryWindow?.EndUtc ??
                PaperDecisionCalendar.ResolveExpiry(setupAvailableAtUtc, strategy, schedules);
            if (candidateExpiresAtUtc <= setupAvailableAtUtc)
            {
                continue;
            }

            var sourceDiscovery = discoveryAggregate is null
                ? StrategyDecisionRequestAssembler.CreateConfiguredLiveDiscovery(
                    _executionRunContext.RunId,
                    ticker,
                    _executionRunContext.StartedAtUtc,
                    candidateExpiresAtUtc)
                : StrategyDecisionRequestAssembler.CreateLiveDiscovery(discoveryAggregate);
            var discovery = sourceDiscovery with
            {
                IsPersisted = false,
                ExpiresAtUtc = candidateExpiresAtUtc
            };
            var universeEvidence = new StrategyEligibilityEvidence(
                discovery.IsActive,
                discoveryAggregate is null ? "configured_run_universe" : "durable_discovery_universe",
                discovery.AggregateId.ToString("N"),
                discovery.ObservedAtUtc,
                JsonSerializer.Serialize(new
                {
                    eligible = discovery.IsActive,
                    discovery.AggregateId,
                    discovery.AggregateVersion,
                    discovery.Sources
                }));
            var regimeEvidence = new StrategyEligibilityEvidence(
                regimeOn,
                "shared_regime_gate",
                strategy.Regime is { IsActive: true }
                    ? $"{strategy.Regime.EvidenceRule}:{now:O}"
                    : $"not-required:{now:O}",
                now,
                // The VIX reading is recorded only for strategies with VIX sizing, so other strategies' evidence is unchanged.
                regimeDecision.Vix is not { } vixReading
                    ? JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime, regimeRejection })
                    : JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime, regimeRejection, vix = vixReading, regimeDecision.RiskScale }));
            var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(
                strategy.StrategyId,
                strategy.Timeframe,
                lastSnapshot.Timestamp);
            IReadOnlyList<OhlcvBar>? factorReference = null;
            IReadOnlyList<DateOnly>? factorSessions = null;
            if (strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } factor)
            {
                factorReference = await _regimeGate.LoadBenchmarkDailyBarsAsync(provider, factor.BenchmarkSymbol,
                    ["1d"], now.AddDays(-Math.Max(400, factor.LookbackBars * 3)), now, cancellationToken);
                if (provider is not IMarketSessionScheduleProvider calendarProvider)
                    throw new InvalidDataException("Relative strength requires an authoritative calendar.");
                var calendar = await calendarProvider.LoadMarketSessionSchedulesAsync(
                    DateOnly.FromDateTime(now.AddDays(-400).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), cancellationToken);
                factorSessions = TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, now, factor.LookbackBars + 1);
            }
            var decisionRequest = StrategyDecisionRequestAssembler.Create(
                runtimeStrategy,
                ticker,
                completedBarsByTimeframe.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<OhlcvBar>)pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                completedSnapshotsByTimeframe,
                catalysts,
                now,
                discovery,
                universeEvidence,
                regimeEvidence,
                StrategyCandidateState.Discovered,
                0,
                setupKey,
                setupAvailableAtUtc,
                candidateExpiresAtUtc,
                run.Engine.IndicatorWarmupBars,
                _executionRunContext.RunId, factorReference, factorSessions,
                await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(_decisionContextProvider,
                    ticker, now, completedBarsByTimeframe.ToDictionary(pair => pair.Key,
                        pair => (IReadOnlyList<OhlcvBar>)pair.Value, StringComparer.OrdinalIgnoreCase), run.Mode,
                    cancellationToken));
            // Source membership may expire while loading regime or decision context.
            var evaluationTime = _timeProvider.GetUtcNow();
            if (entryWindow is not null && evaluationTime < entryWindow.OpenUtc) continue;
            if (entryWindow is not null && !entryWindow.Contains(evaluationTime))
            {
                await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                    new EntryCheck("entry_window", evaluationTime, entryWindow.EndUtc, SetupDeadlineElapsed: true,
                        EntryWindow: entryWindow, SetupAvailableAtUtc: setupAvailableAtUtc), cancellationToken);
                continue;
            }
            if (!HasEntryMembership(run, ticker, discoveryAggregate, evaluationTime))
            {
                await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                    new EntryCheck("before_candidate_evaluation", evaluationTime, candidateExpiresAtUtc),
                    cancellationToken);
                continue;
            }

            var persistedDecision = await _candidateDecisions.EvaluateAsync(
                CreateProductionRun(_executionRunContext),
                decisionRequest,
                cancellationToken);
            if (persistedDecision.WasAlreadyTerminal || persistedDecision.Decision is null)
            {
                continue;
            }

            var decision = persistedDecision.Decision;
            var signalJson = decision.CanonicalDecisionJson;
            if (!persistedDecision.IsNewTrigger ||
                decision.Signal is not { } signal ||
                decision.OrderPlan is not { } canonicalOrderPlan)
            {
                var reason = decision.NoEntryReason ?? decision.State.ToString();
                progress?.Report($"Evaluated {strategy.StrategyName} for {ticker}: {reason}.");
                if (_auditRepo is not null)
                {
                    await _auditRepo.SaveAuditAsync(new DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = decisionTimestamp,
                        Decision = decision.State == StrategyCandidateState.Armed ? "Armed" : "Rejected",
                        RejectionReason = reason,
                        SignalJson = signalJson
                    }, cancellationToken);
                }

                continue;
            }

            if (!canonicalOrderPlan.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
            {
                var reason = "live_short_execution_not_authorized";
                if (_brokerClient is not null)
                {
                    AssetTradingEligibility? eligibility = null;
                    try
                    {
                        eligibility = await _brokerClient.GetEligibilityAsync(ticker, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning(
                            ex,
                            "Broker short-borrow eligibility lookup failed for {Ticker}; failing closed as {Reason}.",
                            ticker,
                            ShortBorrowEligibility.NotEasyToBorrowReason);
                        eligibility = null;
                    }

                    if (!ShortBorrowEligibility.IsShortableAndEasyToBorrow(eligibility))
                    {
                        reason = ShortBorrowEligibility.NotEasyToBorrowReason;
                    }
                }

                if (_auditRepo is not null)
                {
                    await _auditRepo.SaveAuditAsync(new DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = decisionTimestamp,
                        Decision = "Rejected",
                        RejectionReason = reason,
                        SignalJson = signalJson
                    }, cancellationToken);
                }

                continue;
            }

            var signalDirection = canonicalOrderPlan.Direction;

            var orderSignal = StrategyDecisionRequestAssembler.CreateExecutionSignal(
                signal,
                canonicalOrderPlan,
                completedSnapshotsByTimeframe[strategy.Execution.Timeframe]);

                var msg = $"LIVE SIGNAL: {strategy.StrategyName} {ticker} {signalDirection} at {signal.CurrentPrice} (execution {strategy.Execution.Timeframe}: {orderSignal.CurrentPrice})";
                logger.LogInformation(
                    "Live signal generated for {Ticker} {StrategyName} {Direction} at {SignalPrice}; execution price {ExecutionPrice} from {ExecutionTimeframe}.",
                    ticker,
                    strategy.StrategyName,
                    signalDirection,
                    signal.CurrentPrice,
                    orderSignal.CurrentPrice,
                    strategy.Execution.Timeframe);
                progress?.Report(msg);
                _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.SignalGenerated, msg);

                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = decisionTimestamp,
                        Decision = "Accepted",
                        RejectionReason = null,
                        SignalJson = signalJson
                    }, cancellationToken);
                }

                // Submit Order if broker is configured
                if (_brokerClient != null && !run.Execution.DryRun && run.Execution.AllowLiveOrders)
                {
                    progress?.Report($"Calculating position size for {ticker}...");
                    if (_brokerClient is not IBrokerAccountProvider accountProvider)
                    {
                        throw new InvalidOperationException(
                            "Broker account equity is unavailable; live sizing fails closed.");
                    }

                    if (!completedBarsByTimeframe.TryGetValue(
                            strategy.Execution.Timeframe,
                            out var executionBars) ||
                        !completedSnapshotsByTimeframe.TryGetValue(
                            strategy.Execution.Timeframe,
                            out var executionSnapshots) ||
                        executionBars.Count == 0 ||
                        executionSnapshots.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Execution market state {strategy.Execution.Timeframe} is unavailable for risk planning.");
                    }

                    var account = await accountProvider.GetAccountSnapshotAsync(cancellationToken);
                    var finalizedOrderPlan = new StrategyOrderPlanner().Plan(
                        new StrategyOrderPlanningRequest(
                            canonicalOrderPlan,
                            orderSignal,
                            account.Equity,
                            new OrderPlanningRiskLimits(
                                run.Portfolio.AccountRiskBudgetPct,
                                run.Portfolio.MaxPositionNotionalPct,
                                regimeDecision.RiskScale),
                            run.Portfolio.FixedBuyFee,
                            run.Portfolio.FixedSellFee));
                    var order = finalizedOrderPlan.Order;

                    if (order != null)
                    {
                        var shares = order.ShareQuantity;

                        try
                        {
                            if (_orderSubmissionService is null || _executionRunContext is null)
                            {
                                throw new InvalidOperationException(
                                    "Order submission is not armed because the durable submission service or execution provenance is unavailable.");
                            }

                            var submissionTime = _timeProvider.GetUtcNow();
                            if (entryWindow is not null && submissionTime < entryWindow.OpenUtc) continue;
                            var setupDeadlineElapsed = submissionTime >= candidateExpiresAtUtc;
                            if (setupDeadlineElapsed || !HasEntryMembership(run, ticker, discoveryAggregate, submissionTime))
                            {
                                await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                                    new EntryCheck(entryWindow is not null && setupDeadlineElapsed ? "entry_window" : "before_order_submission",
                                        submissionTime, candidateExpiresAtUtc, persistedDecision.Candidate.CandidateId, setupDeadlineElapsed,
                                        entryWindow, setupAvailableAtUtc),
                                    cancellationToken);
                                continue;
                            }

                            progress?.Report($"Submitting Bracket Order: {shares} shares of {ticker}...");
                            var intentId = OrderIntentIdFactory.Create(
                                _executionRunContext.RunId,
                                strategy.StrategyId,
                                "buy",
                                ticker,
                                orderSignal.Timestamp);
                            var submission = await _orderSubmissionService.SubmitEntryOrderAsync(
                                new EntryOrderSubmission(
                                    intentId,
                                     new ValidatedEntryCandidate(
                                         persistedDecision.Candidate.CandidateId,
                                         DiscoverySource: String.Join(",", discovery.Sources),
                                         Horizon: "swing",
                                         DiscoveredAtUtc: persistedDecision.Candidate.DiscoveredAtUtc,
                                         RevalidatedAtUtc: persistedDecision.Candidate.RevalidatedAtUtc,
                                         SetupEvidenceJson: signalJson,
                                         CandidateVersion: persistedDecision.Candidate.Version,
                                         SemanticDecisionSha256: persistedDecision.Candidate.SemanticDecisionSha256),
                                    _executionRunContext,
                                    strategy.StrategyId,
                                    Side: "buy",
                                    OrderType: ResolveEntryOrderType(run),
                                    TimeInForce: ResolveEntryTimeInForce(run),
                                    ExecutionRunContextFactory.ResolveSessionDate(
                                        decisionTimestamp,
                                        strategy.Session.ExchangeTimezone),
                                    decisionTimestamp,
                                    order,
                                    AllowExtendedHoursTrading: run.Execution.AllowExtendedHoursTrading,
                                    StrategyIdentity: runtimeStrategy.Identity,
                                    StrategySelectionMode: runtimeStrategy.SelectionMode)
                                {
                                    DecisionReference = new(canonicalOrderPlan.TriggerReferencePrice, canonicalOrderPlan.TriggeredAtUtc)
                                    {
                                        EntryWindow = entryWindow is not null && strategy.Execution.EntryWindow is { } windowPolicy
                                            ? new(windowPolicy.Policy, windowPolicy.Version, windowPolicy.GraceSeconds,
                                                lastSnapshot.Timestamp.ToUniversalTime(), setupAvailableAtUtc.ToUniversalTime(),
                                                strategy.Execution.EffectiveConfirmation.Enabled ? canonicalOrderPlan.TriggeredAtUtc : null,
                                                entryWindow.OpenUtc, entryWindow.EndUtc) : null
                                    }
                                },
                                _brokerClient,
                                cancellationToken);
                            var orderId = submission.BrokerOrderId;
                            var successMsg = $"Order submitted successfully: ID {orderId}";
                            logger.LogInformation(
                                "Order submitted for {Ticker} {StrategyName}. OrderId={OrderId} Shares={ShareQuantity}",
                                ticker,
                                strategy.StrategyName,
                                orderId,
                                shares);
                            progress?.Report(successMsg);
                            _auditor.LogEvent(ticker, strategy.StrategyName, DateTimeOffset.UtcNow, ExecutionState.BracketOrderSubmitted, successMsg);

                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception missed) when (entryWindow is not null &&
                            (missed is OrderDispatchExpiredException or ExpiredCandidateOrderIntentException))
                        {
                            await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                                new EntryCheck("entry_window", _timeProvider.GetUtcNow(), entryWindow.EndUtc,
                                    persistedDecision.Candidate.CandidateId, true, entryWindow, setupAvailableAtUtc),
                                cancellationToken);
                        }
                        catch (PaperEntryDispatchBlockedException refused)
                        {
                            // Use the actual durable fence outcome when membership or ownership changed after evaluation.
                            await entrySkipAudit.SaveAsync(ticker, strategy, lastSnapshot.Timestamp, membership,
                                new EntryCheck("durable_entry_fence", _timeProvider.GetUtcNow(), candidateExpiresAtUtc,
                                    persistedDecision.Candidate.CandidateId, EntryWindow: entryWindow,
                                    SetupAvailableAtUtc: setupAvailableAtUtc, RefusalReason: refused.ReasonCode), cancellationToken);
                            progress?.Report(refused.Message);
                        }
                        catch (Exception ex)
                        {
                            var errMsg = $"Order submission failed: {ex.Message}";
                            logger.LogError(
                                ex,
                                "Order submission failed for {Ticker} {StrategyName}.",
                                ticker,
                                strategy.StrategyName);
                            progress?.Report(errMsg);
                        }
                    }
                    else
                    {
                        var reason = finalizedOrderPlan.RiskRejection?.Reason ??
                            finalizedOrderPlan.StopRejectionReason ??
                            "order_risk_plan_rejected";
                        logger.LogWarning(
                            "Order risk plan rejected for {Ticker} {StrategyName}: {Reason}",
                            ticker,
                            strategy.StrategyName,
                            reason);
                        progress?.Report($"Skipped {ticker}: {reason}");
                        if (_auditRepo != null)
                        {
                            await _auditRepo.SaveAuditAsync(
                                new TradingFlow.Domain.Audit.DecisionAuditRecord
                                {
                                    RunName = run.RunName,
                                    Ticker = ticker,
                                    StrategyName = strategy.StrategyName,
                                    Timestamp = decisionTimestamp,
                                    Decision = "Rejected",
                                    RejectionReason = reason,
                                    SignalJson = signalJson
                                },
                                cancellationToken);
                        }
                    }
                }
        }
        }
        finally
        {
            if (lockAcquired && _lockService != null)
            {
                try
                {
                    await _lockService.ReleaseLockAsync(ticker, podId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release ticker lock for {Ticker}. It will expire by TTL.", ticker);
                }
            }
        }
    }

    // The exact same row is checked at startup and when candidate evidence is journaled.
    internal static ProductionRun CreateProductionRun(ExecutionRunContext context) => new()
    {
        RunId = context.RunId,
        SchemaVersion = 1,
        ConfigHash = context.ConfigHash,
        CodeVersion = context.CodeVersion,
        Profile = context.Profile,
        Status = "running",
        StartedAtUtc = context.StartedAtUtc,
        UniverseSnapshotId = context.UniverseSnapshotId,
        DecisionRunId = context.DecisionRunId
    };

    // A symbol retained for broker exposure has no automatic entry membership.
    private bool HasEntryMembership(
        BacktestRunConfig run, string ticker, ActiveDiscoveryAggregate? aggregate, DateTimeOffset asOfUtc) =>
        _discoverySession is null
            ? run.Tickers.Contains(ticker, StringComparer.OrdinalIgnoreCase)
            : aggregate is not null && aggregate.Sources.Any(source =>
                !source.IsDiagnostic && source.ObservedAtUtc <= asOfUtc && source.ExpiresAtUtc > asOfUtc);

    private readonly record struct EntryMembershipObservation(
        ActiveDiscoveryAggregate? Current,
        ActiveDiscoveryAggregate? BeforeWarmupRefresh,
        ActiveDiscoveryAggregate? Eligible,
        DateTimeOffset? RefreshedAtUtc,
        bool RefreshFailed,
        IReadOnlyList<DiscoverySourceRefreshFailure> FailedSources)
    {
        // Keep raw membership for audit. Candidate provenance uses only sources
        // that are healthy, non-diagnostic and currently in force.
        public static EntryMembershipObservation Create(
            ActiveDiscoveryAggregate? current,
            ActiveDiscoveryAggregate? beforeWarmupRefresh,
            DateTimeOffset? refreshedAtUtc,
            bool refreshFailed,
            IReadOnlyDictionary<(string Kind, string Key), DiscoverySourceRefreshFailure> failures,
            DateTimeOffset asOfUtc)
        {
            var sources = current?.Sources.Where(source =>
                !source.IsDiagnostic && source.ObservedAtUtc <= asOfUtc && source.ExpiresAtUtc > asOfUtc).ToArray() ?? [];
            var eligible = sources.Where(source => !failures.ContainsKey((source.SourceKind, source.SourceKey))).ToArray();
            var failed = sources.Select(source => failures.GetValueOrDefault((source.SourceKind, source.SourceKey)))
                .OfType<DiscoverySourceRefreshFailure>().ToArray();
            var projection = current is not null && eligible.Length > 0
                ? current with
                {
                    Sources = eligible,
                    LastObservedAtUtc = eligible.Max(source => source.ObservedAtUtc),
                    ExpiresAtUtc = eligible.Max(source => source.ExpiresAtUtc)
                }
                : null;
            return new EntryMembershipObservation(current, beforeWarmupRefresh, projection, refreshedAtUtc, refreshFailed, failed);
        }
    }

    private readonly record struct EntryCheck(
        string Stage,
        DateTimeOffset CheckedAtUtc,
        DateTimeOffset? CandidateExpiresAtUtc = null,
        Guid? CandidateId = null,
        bool SetupDeadlineElapsed = false,
        NextRegularOpenEntryWindow? EntryWindow = null,
        DateTimeOffset? SetupAvailableAtUtc = null,
        string? RefusalReason = null);

    // A single run records the latest skip per symbol, strategy identity and gate.
    private sealed class EntrySkipAudit(
        IDecisionAuditRepository? repository,
        ILogger<LiveRunner> auditLogger,
        string runName,
        Guid? runId,
        IReadOnlyDictionary<StrategyDefinition, AuthorizedRuntimeStrategy> strategies,
        bool usesDiscovery)
    {
        private readonly ConcurrentDictionary<
            (string Ticker, string StrategyId, StrategyArtifactIdentity? Identity, string Stage),
            (DateTimeOffset SetupBarTime, string Reason)> lastSaved = new();

        // Record the gate's actual check time and source evidence after the entry has been skipped.
        public async Task SaveAsync(
            string ticker,
            StrategyDefinition strategy,
            DateTimeOffset setupBarTime,
            EntryMembershipObservation membership,
            EntryCheck check,
            CancellationToken cancellationToken)
        {
            if (repository is null || (!usesDiscovery && !check.SetupDeadlineElapsed && check.RefusalReason is null))
            {
                return;
            }

            var reason = check.RefusalReason ?? (check.SetupDeadlineElapsed && check.EntryWindow is not null ? "entry_window_missed" : check.SetupDeadlineElapsed
                ? "paper_setup_deadline_elapsed_before_order_submission"
                : ResolveMembershipReason(membership, check.CheckedAtUtc));
            var identity = strategies.GetValueOrDefault(strategy)?.Identity;
            var key = (ticker.ToUpperInvariant(), strategy.StrategyId, identity, check.Stage);
            var skip = (setupBarTime, reason);
            // The runner processes a ticker and its strategies sequentially in each poll.
            if (lastSaved.TryGetValue(key, out var previous) && previous == skip)
            {
                return;
            }

            var evidence = membership.Current ?? membership.BeforeWarmupRefresh;
            try
            {
                await repository.SaveAuditAsync(new DecisionAuditRecord
                {
                    RunName = runName,
                    Ticker = ticker,
                    StrategyName = strategy.StrategyName,
                    Timestamp = check.CheckedAtUtc.ToUniversalTime(),
                    Decision = "Skipped",
                    RejectionReason = reason,
                    DeduplicationKey = reason == "entry_window_missed"
                        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                            {
                                runId, runName, ticker = ticker.ToUpperInvariant(), strategy.StrategyId,
                                identity, setup = setupBarTime.ToUniversalTime(), reason
                            })))) : null,
                    SignalJson = JsonSerializer.Serialize(new
                    {
                        runId,
                        ticker,
                        strategyId = strategy.StrategyId,
                        strategyIdentity = identity,
                        stage = check.Stage,
                        checkedAtUtc = check.CheckedAtUtc.ToUniversalTime(),
                        setupBarTimeUtc = setupBarTime.ToUniversalTime(),
                        candidateExpiresAtUtc = check.CandidateExpiresAtUtc,
                        entryWindow = check.EntryWindow,
                        entryWindowPolicy = strategy.Execution.EntryWindow,
                        setupAvailableAtUtc = check.SetupAvailableAtUtc,
                        candidateId = check.CandidateId,
                        membershipRefreshedAtUtc = membership.RefreshedAtUtc,
                        membershipRefreshFailed = membership.RefreshFailed,
                        failedDiscoverySources = membership.FailedSources,
                        membershipPresentAfterRefresh = membership.Current is not null,
                        discoveryAggregateId = evidence?.AggregateId,
                        discoveryAggregateVersion = evidence?.Version,
                        sourceEvidence = evidence?.Sources.Select(source => new
                        {
                            source.SourceKind,
                            source.SourceKey,
                            source.LatestSnapshotId,
                            source.ObservedAtUtc,
                            source.ExpiresAtUtc,
                            source.IsDiagnostic
                        })
                    })
                }, cancellationToken);
                lastSaved[key] = skip;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                auditLogger.LogWarning(exception,
                    "Could not save entry skip for {RunName} {Ticker} {StrategyId} at {EntryStage}: {Reason}.",
                    runName, ticker, strategy.StrategyId, check.Stage, reason);
            }
        }

        // A source dropped from the active projection may have expired before the refresh read.
        private static string ResolveMembershipReason(EntryMembershipObservation membership, DateTimeOffset checkedAtUtc)
        {
            if (membership.RefreshFailed)
            {
                return "paper_discovery_refresh_failed_after_warmup";
            }

            if (membership.FailedSources.Count > 0)
            {
                return "paper_discovery_source_refresh_failed";
            }

            var evidence = membership.Current ?? membership.BeforeWarmupRefresh;
            var asOfUtc = membership.Current is null && membership.RefreshedAtUtc is { } refreshedAtUtc
                ? refreshedAtUtc
                : checkedAtUtc;
            if (evidence is not null)
            {
                if (membership.Current is null && evidence.Sources.Any(source =>
                    !source.IsDiagnostic && source.ObservedAtUtc <= asOfUtc && source.ExpiresAtUtc > asOfUtc))
                {
                    return "paper_discovery_membership_removed_after_warmup";
                }
                if (evidence.Sources.Any(source =>
                    !source.IsDiagnostic && source.ObservedAtUtc <= asOfUtc && source.ExpiresAtUtc <= asOfUtc))
                {
                    return "paper_discovery_membership_expired";
                }
                if (evidence.Sources.Count > 0 && evidence.Sources.All(source => source.IsDiagnostic))
                {
                    return "paper_discovery_membership_diagnostic_only";
                }
            }

            return "paper_discovery_membership_unavailable";
        }
    }

    private static string ResolveEntryOrderType(BacktestRunConfig run) =>
        String.IsNullOrWhiteSpace(run.Execution.EntryOrderType)
            ? run.Execution.OrderType.Trim().ToLowerInvariant()
            : run.Execution.EntryOrderType.Trim().ToLowerInvariant();

    private static string ResolveEntryTimeInForce(BacktestRunConfig run) =>
        run.Execution.OrderExpiration.Equals("day", StringComparison.OrdinalIgnoreCase)
            ? "day"
            : "gtc";
}
