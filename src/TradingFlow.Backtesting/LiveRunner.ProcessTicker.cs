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
using TradingFlow.Engine.Risk;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Backtesting;

// Per-ticker live processing (LiveRunner partial split).
public sealed partial class LiveRunner
{
    private async Task ProcessTickerLiveAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        string ticker,
        TickerMarketState marketState,
        CatalystStreamer catalystStreamer,
        ActiveDiscoveryAggregate? discoveryAggregate,
        IReadOnlyCollection<string> activeTickers,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        bool brokerStateConfirmedForOrderDecisions,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
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
            if (technicalExitHandled)
            {
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

            var regimeOn = strategy.Regime is not { IsActive: true } regimeRule ||
                await _regimeGate.IsRegimeOnAsync(
                    regimeRule,
                    provider,
                    run.Intervals,
                    now,
                    cancellationToken);

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
            var candidateExpiresAtUtc = setupAvailableAtUtc.Add(ParseTimeframe(strategy.Timeframe));
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
                ExpiresAtUtc = sourceDiscovery.ExpiresAtUtc < candidateExpiresAtUtc
                    ? sourceDiscovery.ExpiresAtUtc
                    : candidateExpiresAtUtc
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
                    ? $"{strategy.Regime.BenchmarkSymbol}:{strategy.Regime.SmaPeriod}:{now:O}"
                    : $"not-required:{now:O}",
                now,
                JsonSerializer.Serialize(new { eligible = regimeOn, strategy.Regime }));
            var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(
                strategy.StrategyId,
                strategy.Timeframe,
                lastSnapshot.Timestamp);
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
                _executionRunContext.RunId);
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
                const string reason = "live_short_execution_not_authorized";
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
                                run.Portfolio.MaxPositionNotionalPct),
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
                                         DiscoverySource: persistedDecision.Candidate.DiscoverySource,
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
                                    StrategySelectionMode: runtimeStrategy.SelectionMode),
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

    private static ProductionRun CreateProductionRun(ExecutionRunContext context) => new()
    {
        RunId = context.RunId,
        SchemaVersion = 1,
        ConfigHash = context.ConfigHash,
        CodeVersion = context.CodeVersion,
        Profile = context.Profile,
        Status = "running",
        StartedAtUtc = context.StartedAtUtc
    };

    private static string ResolveEntryOrderType(BacktestRunConfig run) =>
        String.IsNullOrWhiteSpace(run.Execution.EntryOrderType)
            ? run.Execution.OrderType.Trim().ToLowerInvariant()
            : run.Execution.EntryOrderType.Trim().ToLowerInvariant();

    private static string ResolveEntryTimeInForce(BacktestRunConfig run) =>
        run.Execution.OrderExpiration.Equals("day", StringComparison.OrdinalIgnoreCase)
            ? "day"
            : "gtc";
}
