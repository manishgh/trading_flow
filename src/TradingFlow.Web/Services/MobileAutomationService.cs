using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Handles Android/mobile-triggered paper entries while keeping exit ownership
/// on the shared TradingFlow strategy engine. The mobile side decides "enter
/// now"; this service decides "stay in" or "get out".
/// </summary>
public sealed class MobileAutomationService
{
    private readonly ConcurrentDictionary<Guid, MutableAutomationSession> sessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellationTokens = new();
    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;
    private readonly MobileAutomationSessionStore sessionStore;
    private readonly IOrderStateRepository? orderRepo;
    private readonly IDecisionAuditRepository? auditRepo;
    private readonly ILogger<MobileAutomationService> logger;
    private readonly ICandleStore candleStore;
    private readonly SignalGenerator signalGenerator = new();
    private readonly TechnicalExecutionEngine technicalExecutionEngine = new();
    private readonly RiskEngine riskEngine = new();

    public MobileAutomationService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        MobileAutomationSessionStore sessionStore,
        ILogger<MobileAutomationService>? logger = null,
        ICandleStore? candleStore = null,
        IOrderStateRepository? orderRepo = null,
        IDecisionAuditRepository? auditRepo = null)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
        this.sessionStore = sessionStore;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileAutomationService>.Instance;
        this.candleStore = candleStore ?? NullCandleStore.Instance;
        this.orderRepo = orderRepo;
        this.auditRepo = auditRepo;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var restored = await sessionStore.LoadAsync(cancellationToken);
        foreach (var snapshot in restored)
        {
            var session = MutableAutomationSession.FromSnapshot(snapshot);
            if (session.Status is "queued" or "starting" or "running")
            {
                session.Status = "interrupted";
                session.FinishedAt = DateTimeOffset.UtcNow;
                session.Report("interrupted", "Backend restarted before the automation session completed.");
            }

            sessions[session.SessionId] = session;
        }

        await PersistAsync(cancellationToken);
    }

    public IReadOnlyList<MobileAutomationSessionSnapshot> List()
    {
        return sessions.Values
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ToSnapshot())
            .ToArray();
    }

    public MobileAutomationSessionSnapshot? Get(Guid sessionId)
    {
        return sessions.TryGetValue(sessionId, out var session)
            ? session.ToSnapshot()
            : null;
    }

    public async Task<MobileAutomationSessionSnapshot> StartAsync(MobileAutomationStartRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedTicker = request.Ticker.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedTicker))
        {
            throw new InvalidOperationException("Ticker is required.");
        }

        var runConfig = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(request.ConfigPath));
        var strategy = yamlReader.ReadStrategy(paths.ResolveRepositoryPath(request.StrategyPath));
        if (!strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Mobile automation currently supports long intraday paper entries only.");
        }

        var session = new MutableAutomationSession(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.RunName) ? $"mobile_auto_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}" : request.RunName.Trim(),
            request.ConfigPath,
            request.StrategyPath,
            normalizedTicker,
            request.Source,
            request.SourcePackage,
            request.SourceTitle,
            request.SourceMessage,
            DateTimeOffset.UtcNow);

        if (sessions.Values.Any(x =>
                x.Ticker.Equals(normalizedTicker, StringComparison.OrdinalIgnoreCase) &&
                x.Status is "queued" or "starting" or "running"))
        {
            throw new InvalidOperationException($"An active mobile automation session already exists for {normalizedTicker}.");
        }

        sessions[session.SessionId] = session;
        session.Report("queued", $"Queued {request.Source} automation for {normalizedTicker}.");
        await PersistAsync(cancellationToken);

        _ = Task.Run(() => RunAsync(session, runConfig, strategy, cancellationToken), cancellationToken);
        return session.ToSnapshot();
    }

    public void Cancel(Guid sessionId)
    {
        if (!cancellationTokens.TryGetValue(sessionId, out var cts) || !sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        cts.Cancel();
        session.Status = "cancelled";
        session.Report("cancelled", "User cancelled the automation session.");
        _ = PersistAsync();
    }

    private async Task RunAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        CancellationToken outerCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
        cancellationTokens[session.SessionId] = linkedCts;
        var cancellationToken = linkedCts.Token;

        session.Status = "starting";
        session.StartedAt = DateTimeOffset.UtcNow;
        session.Report("starting", $"Preparing market state for {session.Ticker}.");
        await PersistAsync(cancellationToken);

        try
        {
            var marketDataProvider = runtimeFactory.CreateProvider(runConfig);
            var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
            using var providerDisposable = marketDataProvider as IDisposable;
            using var brokerDisposable = brokerClient as IDisposable;

            if (brokerClient is null)
            {
                throw new InvalidOperationException("No broker client available for automation.");
            }

            var state = await LoadTickerStateAsync(runConfig, strategy, session.Ticker, marketDataProvider, cancellationToken);
            var execution = PrepareEntryExecution(runConfig, strategy, session.Ticker, state);

            var order = riskEngine.CreateLongBracketOrder(
                strategy,
                execution.ExecutionSignal,
                runConfig.Portfolio.StartingCapital,
                runConfig.Portfolio.RiskPerTradePct);
            if (order is null)
            {
                throw new InvalidOperationException($"Unable to size order for {session.Ticker}; ATR or risk budget is invalid.");
            }

            order = order with
            {
                ClientOrderId = ClientOrderIdFactory.Create(session.RunName, session.Ticker)
            };

            session.Report("submitting_entry", $"Submitting entry for {session.Ticker} from {session.Source}.");
            var orderId = await brokerClient.SubmitOrderAsync(order, cancellationToken);
            session.Status = "running";
            session.EntryOrderId = orderId;
            session.EntryPrice = order.LimitPrice;
            session.StopLossPrice = order.StopLossPrice;
            session.TakeProfitPrice = order.TakeProfitPrice;
            session.ShareQuantity = order.ShareQuantity;
            session.EntrySubmittedAt = DateTimeOffset.UtcNow;
            session.Report("running", $"Entry submitted: {order.ShareQuantity} shares of {session.Ticker} at {order.LimitPrice:F2}.");
            await PersistAsync(cancellationToken);

            if (orderRepo is not null)
            {
                await orderRepo.SaveOrderAsync(new PersistedOrder
                {
                    OrderId = orderId,
                    Ticker = session.Ticker,
                    RunName = session.RunName,
                    ClientOrderId = order.ClientOrderId,
                    StrategyName = strategy.StrategyName,
                    Broker = runConfig.Execution.Broker,
                    Status = runConfig.Execution.ExtendedHours ? "pending_exit_setup" : "new",
                    EntryPrice = order.LimitPrice,
                    StopLossPrice = order.StopLossPrice,
                    TakeProfitPrice = order.TakeProfitPrice,
                    ShareQuantity = order.ShareQuantity,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken);
            }

            await MonitorExitAsync(session, runConfig, strategy, marketDataProvider, brokerClient, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.Status = "cancelled";
            session.FinishedAt = DateTimeOffset.UtcNow;
            session.Report("cancelled", $"Automation cancelled for {session.Ticker}.");
            await PersistAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mobile automation session {SessionId} failed for {Ticker}.", session.SessionId, session.Ticker);
            session.Status = "failed";
            session.ErrorMessage = exception.Message;
            session.FinishedAt = DateTimeOffset.UtcNow;
            session.Report("failed", exception.Message);
            await PersistAsync();
        }
        finally
        {
            cancellationTokens.TryRemove(session.SessionId, out _);
            await PersistAsync();
        }
    }

    private async Task MonitorExitAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        IMarketDataProvider provider,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var pollingInterval = TimeSpan.FromMinutes(1);
        session.Report("monitoring", $"Monitoring {session.Ticker} for strategy-managed exits.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var openPositions = await brokerClient.GetOpenPositionsAsync(cancellationToken);
            var position = openPositions.FirstOrDefault(x =>
                x.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                x.Qty > 0 &&
                !x.Side.Equals("short", StringComparison.OrdinalIgnoreCase));

            var openOrders = await brokerClient.GetOpenOrdersAsync(cancellationToken);
            if (position is null)
            {
                if (session.EntryOrderId is not null && openOrders.Any(x => x.OrderId == session.EntryOrderId))
                {
                    session.Report("waiting_fill", $"Waiting for {session.Ticker} entry order to fill.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                session.Status = "completed";
                session.FinishedAt = DateTimeOffset.UtcNow;
                session.Report("completed", $"No open position remains for {session.Ticker}. Session finished.");
                await PersistAsync(cancellationToken);
                return;
            }

            await EnsureSafetyExitOrdersAsync(session, runConfig, brokerClient, position, cancellationToken);

            var state = await LoadTickerStateAsync(runConfig, strategy, session.Ticker, provider, cancellationToken);
            if (!state.BarsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                !state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
                executionBars.Count == 0 ||
                executionSnapshots.Count == 0)
            {
                session.Report("monitoring", $"Execution timeframe state missing for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var entryTimestamp = session.EntrySubmittedAt ?? session.StartedAt ?? DateTimeOffset.UtcNow;
            var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
            if (entryIndex >= executionBars.Count)
            {
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var entryPrice = position.EntryPrice > 0m ? position.EntryPrice : session.EntryPrice ?? 0m;
            if (entryPrice <= 0m)
            {
                session.Report("monitoring", $"Entry price unavailable for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var stopLoss = session.StopLossPrice ?? (entryPrice - ((executionSnapshots[^1].Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple));
            var stopDistance = entryPrice - stopLoss;
            if (stopDistance <= 0m)
            {
                session.Report("monitoring", $"Stop distance invalid for {session.Ticker}; retrying.");
                await PersistAsync(cancellationToken);
                await Task.Delay(pollingInterval, cancellationToken);
                continue;
            }

            var takeProfit = session.TakeProfitPrice ?? (entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple));
            var currentStopLossPrice = stopLoss;
            var highestHighSinceEntry = entryPrice;

            string? exitReason = null;
            decimal? exitPrice = null;
            DateTimeOffset exitSignalTimestamp = DateTimeOffset.UtcNow;

            for (var index = entryIndex; index < executionBars.Count; index++)
            {
                var exitPriceLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                    executionBars,
                    index,
                    strategy.ExitRules.ExitLogPriceLookbackBars,
                    x => x.Close,
                    addOne: false);
                var exitVolumeLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                    executionBars,
                    index,
                    strategy.ExitRules.ExitLogVolumeLookbackBars,
                    x => x.Volume,
                    addOne: true);

                if (technicalExecutionEngine.ShouldExitLongOnConfirmedVwapFailure(
                        strategy,
                        executionSnapshots,
                        index,
                        entryIndex,
                        entryPrice,
                        stopDistance,
                        Math.Max(highestHighSinceEntry, executionBars[index].High),
                        index - entryIndex))
                {
                    exitReason = "confirmed_vwap_failure";
                    exitPrice = executionBars[index].Close;
                    exitSignalTimestamp = executionBars[index].Timestamp;
                    break;
                }

                var (candidateExitPrice, candidateExitReason) = technicalExecutionEngine.EvaluateBarForExit(
                    strategy,
                    executionBars[index],
                    executionSnapshots[index],
                    entryPrice,
                    stopLoss,
                    takeProfit,
                    entryTimestamp,
                    stopDistance,
                    index - entryIndex,
                    ref currentStopLossPrice,
                    ref highestHighSinceEntry,
                    exitPriceLogTrend?.Slope,
                    exitVolumeLogTrend?.Slope,
                    index > 0 ? executionSnapshots[index - 1] : null);

                if (candidateExitReason is not null)
                {
                    exitReason = candidateExitReason;
                    exitPrice = candidateExitPrice;
                    exitSignalTimestamp = executionBars[index].Timestamp;
                    break;
                }
            }

            if (exitReason is not null)
            {
                session.Report("submitting_exit", $"Exit triggered for {session.Ticker}: {exitReason}.");
                foreach (var order in openOrders.Where(order =>
                             order.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                             order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        await brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Failed to cancel sell order {OrderId} before closing {Ticker}.", order.OrderId, session.Ticker);
                    }
                }

                var closed = await brokerClient.ClosePositionAsync(session.Ticker, cancellationToken);
                if (closed)
                {
                    session.Status = "completed";
                    session.FinishedAt = DateTimeOffset.UtcNow;
                    session.ExitReason = exitReason;
                    session.ExitSubmittedAt = DateTimeOffset.UtcNow;
                    session.Report("completed", $"Closed {session.Ticker} on {exitReason} at ~{exitPrice?.ToString("F2") ?? "market"}.");
                    await PersistAsync(cancellationToken);

                    if (auditRepo is not null)
                    {
                        await auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = session.RunName,
                            Ticker = session.Ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = DateTimeOffset.UtcNow,
                            Decision = "ExitSubmitted",
                            RejectionReason = exitReason,
                            SignalJson = JsonSerializer.Serialize(new
                            {
                                ticker = session.Ticker,
                                session.RunName,
                                exitReason,
                                exitSignalTimestamp,
                                estimatedExitPrice = exitPrice,
                                entryPrice,
                                currentPrice = position.CurrentPrice,
                                unrealizedPl = position.UnrealizedPl
                            })
                        }, cancellationToken);
                    }

                    return;
                }
            }

            session.LastObservedPrice = position.CurrentPrice;
            session.UnrealizedPl = position.UnrealizedPl;
            session.Report("monitoring", $"Holding {session.Ticker}. Last {position.CurrentPrice:F2}, unrealized {position.UnrealizedPl:F2}.");
            await PersistAsync(cancellationToken);
            await Task.Delay(pollingInterval, cancellationToken);
        }
    }

    private async Task EnsureSafetyExitOrdersAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        IBrokerClient brokerClient,
        BrokerPosition position,
        CancellationToken cancellationToken)
    {
        if (!runConfig.Execution.ExtendedHours || session.ExitSafetyOrdersSubmitted)
        {
            return;
        }

        var stopLoss = session.StopLossPrice;
        var takeProfit = session.TakeProfitPrice;
        if (stopLoss is null || takeProfit is null)
        {
            return;
        }

        await brokerClient.SubmitExitOrdersAsync(session.Ticker, (int)position.Qty, stopLoss.Value, takeProfit.Value, cancellationToken);
        session.ExitSafetyOrdersSubmitted = true;
        session.Report("monitoring", $"Submitted safety OCO exits for {session.Ticker}.");
        await PersistAsync(cancellationToken);
    }

    private Task PersistAsync(CancellationToken cancellationToken = default)
    {
        return sessionStore.SaveAsync(sessions.Values.Select(x => x.ToSnapshot()).ToArray(), cancellationToken);
    }

    private async Task<TickerMarketState> LoadTickerStateAsync(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string ticker,
        IMarketDataProvider provider,
        CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var lookbackDays = runConfig.TimeWindow.WarmupLookbackDays > 0
            ? runConfig.TimeWindow.WarmupLookbackDays
            : Math.Max(runConfig.TimeWindow.LookbackDays, 10);
        var start = end.AddDays(-lookbackDays);
        var requiredTimeframes = ResolveRequiredTimeframes(strategy);
        var downloadTimeframes = ResolveDownloadTimeframesForAutomation(runConfig, strategy, requiredTimeframes);
        var deriveFromTimeframe = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, downloadTimeframes);

        var pipeline = new CandlePipelineEngine(candleStore);
        var state = await pipeline.RunAsync(
            new CandlePipelineRequest(
                [ticker],
                downloadTimeframes,
                requiredTimeframes,
                deriveFromTimeframe,
                start,
                end,
                runConfig.Engine.BoundedCapacity,
                runConfig.Engine.WorkerCount,
                runConfig.Execution.ExtendedHours,
                strategy.Session.ExchangeTimezone),
            provider,
            cancellationToken);

        if (!state.TickerStates.TryGetValue(ticker, out var tickerState))
        {
            var reason = state.Failures.TryGetValue(ticker, out var failure)
                ? failure
                : "missing_ticker_market_state";
            throw new InvalidOperationException(reason);
        }

        return tickerState;
    }

    private static string[] ResolveDownloadTimeframesForAutomation(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        IReadOnlyCollection<string> requiredTimeframes)
    {
        var source = ResolveDeriveFromTimeframe(runConfig, requiredTimeframes, runConfig.Intervals);
        var sourceDuration = TimeframeParser.Parse(source);
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source
        };

        foreach (var timeframe in runConfig.Intervals.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var duration = TimeframeParser.Parse(timeframe);
            if (duration <= sourceDuration || TimeframeParser.IsDailyOrHigher(timeframe))
            {
                timeframes.Add(timeframe);
            }
        }

        if (RequiresDailyContext(strategy))
        {
            timeframes.Add("1d");
        }

        return timeframes
            .OrderBy(TimeframeParser.Parse)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDeriveFromTimeframe(
        BacktestRunConfig runConfig,
        IReadOnlyCollection<string> requiredTimeframes,
        IReadOnlyCollection<string> candidateDownloadTimeframes)
    {
        var intradayRequired = requiredTimeframes
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .ToArray();

        if (intradayRequired.Length == 0)
        {
            return runConfig.DerivedTimeframes.Source;
        }

        var finestRequired = intradayRequired[0];
        var finestRequiredDuration = TimeframeParser.Parse(finestRequired);
        var currentSource = string.IsNullOrWhiteSpace(runConfig.DerivedTimeframes.Source)
            ? finestRequired
            : runConfig.DerivedTimeframes.Source;

        if (TimeframeParser.Parse(currentSource) <= finestRequiredDuration)
        {
            return currentSource;
        }

        var providerCandidate = candidateDownloadTimeframes
            .Where(timeframe => !string.IsNullOrWhiteSpace(timeframe))
            .Where(timeframe => !TimeframeParser.IsDailyOrHigher(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(TimeframeParser.Parse)
            .FirstOrDefault(timeframe => TimeframeParser.Parse(timeframe) <= finestRequiredDuration);

        return providerCandidate ?? finestRequired;
    }

    private static bool RequiresDailyContext(StrategyDefinition strategy)
    {
        return TimeframeParser.IsDailyOrHigher(strategy.Timeframe) ||
            TimeframeParser.IsDailyOrHigher(strategy.Execution.Timeframe) ||
            strategy.EntryRules.RequirePriceAboveSma50Daily ||
            strategy.EntryRules.RequirePriceAboveSma200Daily;
    }

    private PreparedEntryExecution PrepareEntryExecution(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string ticker,
        TickerMarketState state)
    {
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var strategySnapshots) ||
            strategySnapshots.Count == 0 ||
            !state.BarsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
            strategyBars.Count == 0)
        {
            throw new InvalidOperationException($"Strategy timeframe {strategy.Timeframe} is unavailable for {ticker}.");
        }

        var latest = strategySnapshots[^1];
        var readiness = GetSignalReadinessRejection(latest);
        if (readiness is not null)
        {
            throw new InvalidOperationException(readiness);
        }

        var signal = signalGenerator.CreateTradeSignal(strategy, strategyBars, strategySnapshots, strategySnapshots.Count - 1)
            ?? throw new InvalidOperationException($"No trade signal could be prepared for {ticker}.");

        var signalAvailableAt = latest.Timestamp.Add(TimeframeParser.Parse(strategy.Timeframe));
        var confluenceRejection = signalGenerator.GetConfluenceRejection(strategy, signalAvailableAt, state.SnapshotsByTimeframe);
        if (confluenceRejection is not null)
        {
            throw new InvalidOperationException(confluenceRejection);
        }

        var executionSignal = ResolveExecutionOrderSignal(strategy, signal, state.SnapshotsByTimeframe)
            ?? throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");

        return new PreparedEntryExecution(signal, executionSignal);
    }

    private static string? GetSignalReadinessRejection(IndicatorSnapshot snapshot)
    {
        var missing = new List<string>();
        if (snapshot.Rsi is null) missing.Add("rsi");
        if (snapshot.Atr is null) missing.Add("atr");
        if (snapshot.RelativeVolume is null) missing.Add("relative_volume");
        if (snapshot.Vwap is null) missing.Add("vwap");
        if (snapshot.BollingerMiddle is null) missing.Add("bollinger_middle");
        if (snapshot.MacdHistogram is null) missing.Add("macd_histogram");
        return missing.Count == 0 ? null : $"signal_missing_indicators ({string.Join(", ", missing)})";
    }

    private static string[] ResolveRequiredTimeframes(StrategyDefinition strategy)
    {
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            strategy.Timeframe,
            strategy.Execution.Timeframe
        };
        if (strategy.Confluence.Enabled)
        {
            timeframes.Add(strategy.Confluence.Timeframe);
        }

        return timeframes.ToArray();
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

        var orderedSnapshots = executionSnapshots.OrderBy(snapshot => snapshot.Timestamp).ToArray();
        var signalCloseTimestamp = signal.Timestamp.Add(TimeframeParser.Parse(strategy.Timeframe));
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

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }

    private sealed record PreparedEntryExecution(TradeSignal Signal, TradeSignal ExecutionSignal);

    private sealed class MutableAutomationSession
    {
        private readonly List<string> events = new();

        public MutableAutomationSession(
            Guid sessionId,
            string runName,
            string configPath,
            string strategyPath,
            string ticker,
            string source,
            string? sourcePackage,
            string? sourceTitle,
            string? sourceMessage,
            DateTimeOffset createdAt)
        {
            SessionId = sessionId;
            RunName = runName;
            ConfigPath = configPath;
            StrategyPath = strategyPath;
            Ticker = ticker;
            Source = source;
            SourcePackage = sourcePackage;
            SourceTitle = sourceTitle;
            SourceMessage = sourceMessage;
            CreatedAt = createdAt;
        }

        public Guid SessionId { get; }
        public string RunName { get; }
        public string ConfigPath { get; }
        public string StrategyPath { get; }
        public string Ticker { get; }
        public string Source { get; }
        public string? SourcePackage { get; }
        public string? SourceTitle { get; }
        public string? SourceMessage { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public DateTimeOffset? EntrySubmittedAt { get; set; }
        public DateTimeOffset? ExitSubmittedAt { get; set; }
        public string Status { get; set; } = "queued";
        public string CurrentStage { get; private set; } = "queued";
        public string? ErrorMessage { get; set; }
        public string? EntryOrderId { get; set; }
        public decimal? EntryPrice { get; set; }
        public decimal? StopLossPrice { get; set; }
        public decimal? TakeProfitPrice { get; set; }
        public int? ShareQuantity { get; set; }
        public decimal? LastObservedPrice { get; set; }
        public decimal? UnrealizedPl { get; set; }
        public string? ExitReason { get; set; }
        public bool ExitSafetyOrdersSubmitted { get; set; }

        public void Report(string stage, string message)
        {
            CurrentStage = stage;
            events.Add($"{DateTimeOffset.Now:HH:mm:ss} {stage}: {message}");
            if (events.Count > 80)
            {
                events.RemoveAt(0);
            }
        }

        public MobileAutomationSessionSnapshot ToSnapshot()
        {
            return new MobileAutomationSessionSnapshot(
                SessionId,
                RunName,
                ConfigPath,
                StrategyPath,
                Ticker,
                Source,
                SourcePackage,
                Status,
                CreatedAt,
                StartedAt,
                FinishedAt,
                EntrySubmittedAt,
                ExitSubmittedAt,
                ErrorMessage,
                CurrentStage,
                EntryOrderId,
                EntryPrice,
                StopLossPrice,
                TakeProfitPrice,
                ShareQuantity,
                LastObservedPrice,
                UnrealizedPl,
                ExitReason,
                events.ToArray(),
                SourceTitle,
                SourceMessage);
        }

        public static MutableAutomationSession FromSnapshot(MobileAutomationSessionSnapshot snapshot)
        {
            var session = new MutableAutomationSession(
                snapshot.SessionId,
                snapshot.RunName,
                snapshot.ConfigPath,
                snapshot.StrategyPath,
                snapshot.Ticker,
                snapshot.Source,
                snapshot.SourcePackage,
                snapshot.SourceTitle,
                snapshot.SourceMessage,
                snapshot.CreatedAt)
            {
                StartedAt = snapshot.StartedAt,
                FinishedAt = snapshot.FinishedAt,
                EntrySubmittedAt = snapshot.EntrySubmittedAt,
                ExitSubmittedAt = snapshot.ExitSubmittedAt,
                Status = snapshot.Status,
                CurrentStage = snapshot.CurrentStage,
                ErrorMessage = snapshot.ErrorMessage,
                EntryOrderId = snapshot.EntryOrderId,
                EntryPrice = snapshot.EntryPrice,
                StopLossPrice = snapshot.StopLossPrice,
                TakeProfitPrice = snapshot.TakeProfitPrice,
                ShareQuantity = snapshot.ShareQuantity,
                LastObservedPrice = snapshot.LastObservedPrice,
                UnrealizedPl = snapshot.UnrealizedPl,
                ExitReason = snapshot.ExitReason
            };

            foreach (var item in snapshot.Events)
            {
                session.events.Add(item);
            }

            return session;
        }
    }
}
