using System.Text.Json;
using TradingFlow.Domain.Backtesting;
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
        IReadOnlyCollection<string> activeTickers,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        IReadOnlyDictionary<string, decimal> screenerRelativeVolumeByTicker,
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
        // 0.5 Recover state from DB if necessary
        if (_orderRepo != null)
        {
            var dbOrders = await _orderRepo.GetActiveOrdersByTickerAsync(ticker, cancellationToken);
            if (dbOrders.Count > 0)
            {
                logger.LogInformation("Recovered {Count} active orders for {Ticker} from database state.", dbOrders.Count, ticker);
                progress?.Report($"Recovered {dbOrders.Count} active orders for {ticker} from database state.");
                // Here the pod could theoretically reconstruct technical exits based on the recovered StrategyName and EntryPrice
            }
        }

        var barsByTimeframe = marketState.BarsByTimeframe
            .ToDictionary(
                x => x.Key,
                x => x.Value.OrderBy(bar => bar.Timestamp).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var snapshotsByTimeframe = marketState.SnapshotsByTimeframe
            .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);

        if (run.News.Enabled)
        {
            var allBars = barsByTimeframe.Values.SelectMany(x => x).ToArray();
            if (allBars.Length > 0)
            {
                var catalysts = await catalystStreamer.LoadTickerCatalystsAsync(
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

        var readonlySnapshotsByTimeframe = snapshotsByTimeframe.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<IndicatorSnapshot>)x.Value,
            StringComparer.OrdinalIgnoreCase);

        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName, ticker);
        Directory.CreateDirectory(resultsDir);

        // 3. Process Strategies & Write Chart Data
        foreach (var strategy in strategies)
        {
            if (!readonlySnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var snapshots) || snapshots.Count == 0)
                continue;

            if (!barsByTimeframe.TryGetValue(strategy.Timeframe, out var barsList))
                continue;

            var lastSnapshot = snapshots[^1];
            var lastBar = barsList[^1];
            var isFinvizRelativeVolume = screenerRelativeVolumeByTicker.TryGetValue(ticker, out var screenerRelativeVolume);
            var configuredRelativeVolume = StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, lastSnapshot);
            var relativeVolumeSource = isFinvizRelativeVolume ? "finviz_screener" : strategy.EntryRules.MinVolumeSpikeSource;
            var effectiveRelativeVolume = isFinvizRelativeVolume
                ? screenerRelativeVolume
                : configuredRelativeVolume ?? 0m;

            var chartData = new
            {
                Timestamp = lastSnapshot.Timestamp,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timeframe = lastSnapshot.Timeframe,
                Close = lastSnapshot.CurrentPrice,
                Atr = lastSnapshot.Atr ?? 0m,
                RelativeVolume = effectiveRelativeVolume,
                SlotRelativeVolume = lastSnapshot.SlotRelativeVolume ?? 0m,
                SessionRelativeVolume = lastSnapshot.SessionRelativeVolume ?? 0m,
                CalculatedRelativeVolume = lastSnapshot.RelativeVolume ?? 0m,
                RelativeVolumeSource = relativeVolumeSource,
                SlotAverageVolume = lastSnapshot.SlotAverageVolume ?? 0m,
                CumulativeAverageVolume = lastSnapshot.CumulativeAverageVolume ?? 0m,
                AverageSessionVolume = lastSnapshot.AverageSessionVolume ?? 0m,
                RelativeVolumeSampleCount = lastSnapshot.RelativeVolumeSampleCount,
                Volume = lastSnapshot.CurrentVolume,
                Rsi = lastSnapshot.Rsi,
                Vwap = lastSnapshot.Vwap
            };
            var json = System.Text.Json.JsonSerializer.Serialize(chartData);
            await _artifactWriter.WriteTextAsync(Path.Combine(resultsDir, $"{ticker}_chart.json"), json, cancellationToken);

            var technicalExitHandled = await TrySubmitTechnicalExitAsync(
                run,
                strategy,
                ticker,
                barsByTimeframe,
                readonlySnapshotsByTimeframe,
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

            // L2 regime gate (doctrine §2): block NEW entries when the strategy's regime is off today
            // (e.g. SPY below its 50-day SMA). Exits above are unaffected, so open positions are still
            // managed in any regime. Uses the same no-lookahead gate the backtest applies.
            if (strategy.Regime is { IsActive: true } regimeRule)
            {
                var regimeOn = await _regimeGate.IsRegimeOnAsync(
                    regimeRule,
                    provider,
                    run.Intervals,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (!regimeOn)
                {
                    var regimeReason = $"regime_off ({regimeRule.BenchmarkSymbol} not above {regimeRule.SmaPeriod}d SMA)";
                    logger.LogDebug("Skipping {Ticker} entry for {Strategy}: {Reason}.", ticker, strategy.StrategyName, regimeReason);
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = DateTimeOffset.UtcNow,
                            Decision = "Rejected",
                            RejectionReason = regimeReason,
                            SignalJson = JsonSerializer.Serialize(new
                            {
                                ticker,
                                strategy = strategy.StrategyName,
                                benchmark = regimeRule.BenchmarkSymbol,
                                smaPeriod = regimeRule.SmaPeriod
                            })
                        }, cancellationToken);
                    }

                    continue;
                }
            }

            // Evaluate signal
            var signal = _signalGenerator.CreateTradeSignal(strategy, barsList, snapshots, snapshots.Count - 1);
            if (signal != null)
            {
                var signalJson = BuildSignalAuditJson(
                    signal,
                    effectiveRelativeVolume,
                    relativeVolumeSource,
                    lastSnapshot.RelativeVolume,
                    lastSnapshot.SlotRelativeVolume,
                    lastSnapshot.SessionRelativeVolume,
                    lastSnapshot.SlotAverageVolume,
                    lastSnapshot.CumulativeAverageVolume,
                    lastSnapshot.AverageSessionVolume,
                    lastSnapshot.RelativeVolumeSampleCount);
                var decisionTimestamp = DateTimeOffset.UtcNow;

                var signalAvailableTimestamp = lastSnapshot.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
                var confluenceRejection = _signalGenerator.GetConfluenceRejection(strategy, signalAvailableTimestamp, readonlySnapshotsByTimeframe);
                if (confluenceRejection is not null)
                {
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = decisionTimestamp,
                            Decision = "Rejected",
                            RejectionReason = confluenceRejection,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }
                    continue;
                }

                var relativeVolume = effectiveRelativeVolume;
                var rejectionReason = _decisionBrain.GetLongEntryRejection(strategy, signal, lastSnapshot, relativeVolume, relativeVolumeSource);

                if (rejectionReason != null)
                {
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = decisionTimestamp,
                            Decision = "Rejected",
                            RejectionReason = rejectionReason,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }
                    continue;
                }

                const string signalDirection = "long";

                // Check News Veto
                if (run.News.Enabled)
                {
                    progress?.Report($"Checking recent news sentiment for {ticker}...");
                    var recentNews = await catalystStreamer.LoadTickerCatalystsAsync(ticker, DateTimeOffset.UtcNow.AddMinutes(-run.News.VetoTtlMinutes), DateTimeOffset.UtcNow, cancellationToken);
                    var badNews = recentNews.FirstOrDefault(c => c.SentimentScore <= run.News.VetoNegativeThreshold);
                    if (badNews != null)
                    {
                        var vetoMsg = $"VETOED: Negative news detected ({badNews.SentimentScore:F2}): {badNews.Headline}";
                        logger.LogWarning(
                            "News veto for {Ticker} {StrategyName}: {VetoMessage}",
                            ticker,
                            strategy.StrategyName,
                            vetoMsg);
                        progress?.Report(vetoMsg);
                        _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.OrderRejected, vetoMsg);

                        if (_auditRepo != null)
                        {
                            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                            {
                                RunName = run.RunName,
                                Ticker = ticker,
                                StrategyName = strategy.StrategyName,
                                Timestamp = decisionTimestamp,
                                Decision = "Rejected",
                                RejectionReason = "news_veto",
                                SignalJson = signalJson
                            }, cancellationToken);
                        }
                        continue;
                    }
                }

                var orderSignal = ResolveExecutionOrderSignal(strategy, signal, readonlySnapshotsByTimeframe);
                if (orderSignal is null)
                {
                    var reason = $"execution_timeframe_missing (ExecutionTimeframe: {strategy.Execution.Timeframe})";
                    logger.LogWarning(
                        "Skipping {Ticker} {StrategyName}; execution timeframe {ExecutionTimeframe} has no usable snapshot.",
                        ticker,
                        strategy.StrategyName,
                        strategy.Execution.Timeframe);
                    progress?.Report($"Skipped {ticker}: {reason}");

                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
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
                    var riskEngine = new TradingFlow.Engine.Risk.RiskEngine();
                    var order = riskEngine.CreateLongBracketOrderWithPositionRisk(
                        strategy,
                        orderSignal,
                        run.Portfolio.StartingCapital,
                        run.Portfolio.RiskPerTradePct,
                        run.Portfolio.MaxPositionValuePct);

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
                            var submission = await _orderSubmissionService.SubmitBracketOrderAsync(
                                new BracketOrderSubmission(
                                    intentId,
                                    new ValidatedEntryCandidate(
                                        intentId,
                                        DiscoverySource: "strategy_signal",
                                        Horizon: strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)
                                            ? "swing"
                                            : "intraday",
                                        DiscoveredAtUtc: decisionTimestamp,
                                        RevalidatedAtUtc: decisionTimestamp,
                                        SetupEvidenceJson: signalJson),
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
                                    AllowExtendedHoursTrading: run.Execution.AllowExtendedHoursTrading),
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

                            if (_orderRepo != null)
                            {
                                var persistedOrder = new TradingFlow.Domain.Orders.PersistedOrder
                                {
                                    OrderId = orderId,
                                    Ticker = ticker,
                                    RunName = run.RunName,
                                    ClientOrderId = submission.ClientOrderId,
                                    StrategyName = strategy.StrategyName,
                                    Broker = run.Execution.Broker,
                                    Status = submission.SubmittedOutsideRegularHours ? "pending_exit_setup" : "new",
                                    EntryPrice = order.LimitPrice,
                                    StopLossPrice = order.StopLossPrice,
                                    TakeProfitPrice = order.TakeProfitPrice,
                                    ShareQuantity = order.ShareQuantity,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                    UpdatedAt = DateTimeOffset.UtcNow
                                };
                                await _orderRepo.SaveOrderAsync(persistedOrder, cancellationToken);
                            }
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
                        progress?.Report($"Skipped order for {ticker}: Calculated shares is 0.");
                    }
                }
            }
            else
            {
                progress?.Report($"Evaluated {strategy.StrategyName} for {ticker}: No signal generated.");
                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = DateTimeOffset.UtcNow,
                        Decision = "NoSignal",
                        RejectionReason = "no_signal_generated",
                        SignalJson = JsonSerializer.Serialize(new
                        {
                            ticker,
                            strategy = strategy.StrategyName,
                            timestamp = lastSnapshot.Timestamp,
                            timeframe = lastSnapshot.Timeframe,
                            close = lastSnapshot.CurrentPrice,
                            volume = lastSnapshot.CurrentVolume,
                            relativeVolume = effectiveRelativeVolume,
                            relativeVolumeSource,
                            calculatedRelativeVolume = lastSnapshot.RelativeVolume,
                            slotRelativeVolume = lastSnapshot.SlotRelativeVolume,
                            sessionRelativeVolume = lastSnapshot.SessionRelativeVolume
                        })
                    }, cancellationToken);
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

    private static TradeSignal? ResolveExecutionOrderSignal(
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        if (strategy.Execution.Timeframe.Equals(strategy.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return signal;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            return null;
        }

        var orderedSnapshots = executionSnapshots
            .OrderBy(snapshot => snapshot.Timestamp)
            .ToArray();
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var executionSnapshot = orderedSnapshots.FirstOrDefault(snapshot => snapshot.Timestamp >= signalCloseTimestamp)
            ?? orderedSnapshots.LastOrDefault(snapshot => snapshot.Timestamp >= signal.Timestamp);

        if (executionSnapshot is null)
        {
            return null;
        }

        return signal with
        {
            Timestamp = executionSnapshot.Timestamp,
            Timeframe = executionSnapshot.Timeframe,
            CurrentPrice = executionSnapshot.CurrentPrice,
            CurrentVolume = executionSnapshot.CurrentVolume,
            CurrentRsi = executionSnapshot.Rsi ?? signal.CurrentRsi,
            CurrentAtr = signal.CurrentAtr > 0m
                ? signal.CurrentAtr
                : executionSnapshot.Atr ?? signal.CurrentAtr
        };
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
