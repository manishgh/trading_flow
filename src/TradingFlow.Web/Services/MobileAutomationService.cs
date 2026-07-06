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
    private readonly BasicStrategyEvaluator strategyEvaluator = new();
    private readonly PositionGuardianEngine positionGuardianEngine = new();
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
        PruneExpiredSessions();
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
        var entryMode = NormalizeEntryMode(request.EntryMode);
        session.Report("queued", $"Queued {request.Source} automation for {normalizedTicker}. EntryMode={entryMode}.");
        await PersistAsync(cancellationToken);

        _ = Task.Run(() => RunAsync(session, runConfig, strategy, entryMode, cancellationToken), cancellationToken);
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

    /// <summary>
    /// Manually closes (sells) the broker position tracked by a session and ends its
    /// monitoring loop. Used by the Running Trades "Sell" action.
    /// </summary>
    public async Task<bool> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        var runConfig = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(session.ConfigPath));
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient is null)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var quantity = session.ShareQuantity ?? 0;
            if (quantity <= 0)
            {
                session.Report("close_rejected", $"Cannot close {session.Ticker}; session quantity is unavailable.");
                await PersistAsync(cancellationToken);
                return false;
            }

            await CancelOpenSellOrdersForTickerAsync(brokerClient, session.Ticker, cts.Token);

            var closed = await brokerClient.ClosePositionAsync(session.Ticker, quantity, cts.Token);
            if (closed)
            {
                if (cancellationTokens.TryGetValue(sessionId, out var monitorCts))
                {
                    monitorCts.Cancel();
                }

                session.Status = "completed";
                session.FinishedAt = DateTimeOffset.UtcNow;
                session.ExitSubmittedAt = DateTimeOffset.UtcNow;
                session.ExitReason = "manual_sell";
                session.Report("completed", $"Manually sold {session.Ticker} from Running Trades.");
                await PersistAsync(cancellationToken);
            }

            return closed;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to manually close automation session {SessionId} for {Ticker}.", sessionId, session.Ticker);
            return false;
        }
        finally
        {
            if (brokerClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task RunAsync(
        MutableAutomationSession session,
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        string entryMode,
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
            var execution = PrepareAutomationEntryExecution(runConfig, strategy, session, state, entryMode);

            var order = riskEngine.CreateLongBracketOrderWithPositionRisk(
                strategy,
                execution.ExecutionSignal,
                runConfig.Portfolio.StartingCapital,
                runConfig.Portfolio.RiskPerTradePct,
                runConfig.Portfolio.MaxPositionValuePct);
            if (order is null)
            {
                throw new InvalidOperationException($"Unable to size order for {session.Ticker}; ATR or risk budget is invalid.");
            }

            order = order with
            {
                ClientOrderId = ClientOrderIdFactory.Create(session.RunName, session.Ticker)
            };

            session.Report("submitting_entry", $"Submitting entry for {session.Ticker} from {session.Source}. EntryMode={entryMode}.");
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
            var guardianDecision = positionGuardianEngine.EvaluateLong(
                strategy,
                executionBars,
                executionSnapshots,
                entryTimestamp,
                entryPrice,
                stopLoss,
                takeProfit);

            if (guardianDecision.ShouldExit)
            {
                session.Report("submitting_exit", $"Exit triggered for {session.Ticker}: {guardianDecision.Reason}.");
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

                var closeQuantity = session.ShareQuantity ?? (int)Math.Floor(position.Qty);
                if (closeQuantity <= 0)
                {
                    session.Report("exit_rejected", $"Exit triggered for {session.Ticker}, but close quantity was unavailable.");
                    await PersistAsync(cancellationToken);
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                var closed = await brokerClient.ClosePositionAsync(session.Ticker, closeQuantity, cancellationToken);
                if (closed)
                {
                    session.Status = "completed";
                    session.FinishedAt = DateTimeOffset.UtcNow;
                    session.ExitReason = guardianDecision.Reason;
                    session.ExitSubmittedAt = DateTimeOffset.UtcNow;
                    session.Report("completed", $"Closed {session.Ticker} on {guardianDecision.Reason} at ~{guardianDecision.ExitPrice?.ToString("F2") ?? "market"}.");
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
                            RejectionReason = guardianDecision.Reason,
                            SignalJson = JsonSerializer.Serialize(new
                            {
                                ticker = session.Ticker,
                                session.RunName,
                                exitReason = guardianDecision.Reason,
                                exitSignalTimestamp = guardianDecision.ExitSignalTimestamp,
                                estimatedExitPrice = guardianDecision.ExitPrice,
                                entryPrice,
                                currentPrice = position.CurrentPrice,
                                unrealizedPl = position.UnrealizedPl
                            })
                        }, cancellationToken);
                    }

                    return;
                }
            }
            else
            {
                await TryRaiseBrokerTrailingStopAsync(
                    session,
                    strategy,
                    brokerClient,
                    openOrders,
                    stopLoss,
                    guardianDecision.CurrentStopLossPrice,
                    cancellationToken);
            }

            session.LastObservedPrice = position.CurrentPrice;
            session.UnrealizedPl = position.UnrealizedPl;
            session.Report("monitoring", $"Holding {session.Ticker}. Last {position.CurrentPrice:F2}, unrealized {position.UnrealizedPl:F2}. Guardian={guardianDecision.Reason}.");
            await PersistAsync(cancellationToken);
            await Task.Delay(pollingInterval, cancellationToken);
        }
    }

    private async Task CancelOpenSellOrdersForTickerAsync(
        IBrokerClient brokerClient,
        string ticker,
        CancellationToken cancellationToken)
    {
        var openOrders = await brokerClient.GetOpenOrdersAsync(cancellationToken);
        foreach (var order in openOrders.Where(order =>
                     order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                     order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to cancel sell order {OrderId} before closing automation position for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
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

    private async Task TryRaiseBrokerTrailingStopAsync(
        MutableAutomationSession session,
        StrategyDefinition strategy,
        IBrokerClient brokerClient,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        decimal initialStopLossPrice,
        decimal calculatedStopLossPrice,
        CancellationToken cancellationToken)
    {
        if (!strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            calculatedStopLossPrice <= (session.StopLossPrice ?? initialStopLossPrice) + 0.01m)
        {
            return;
        }

        var stopOrder = openOrders
            .Where(order =>
                order.Ticker.Equals(session.Ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.StopPrice is not null)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();
        if (stopOrder is null)
        {
            session.Report("monitoring", $"Guardian calculated a raised stop for {session.Ticker} at {calculatedStopLossPrice:F2}, but no broker stop leg is open.");
            return;
        }

        if (stopOrder.StopPrice is { } brokerStopPrice &&
            calculatedStopLossPrice <= brokerStopPrice + 0.01m)
        {
            return;
        }

        var modified = await brokerClient.ModifyOrderAsync(stopOrder.OrderId, calculatedStopLossPrice, 0m, cancellationToken);
        if (!modified)
        {
            session.Report("monitoring", $"Broker rejected trailing stop raise for {session.Ticker}.");
            return;
        }

        session.StopLossPrice = calculatedStopLossPrice;
        session.Report("monitoring", $"Raised broker trailing stop for {session.Ticker} to {calculatedStopLossPrice:F2}.");
        await PersistAsync(cancellationToken);
    }

    private Task PersistAsync(CancellationToken cancellationToken = default)
    {
        PruneExpiredSessions();
        return sessionStore.SaveAsync(sessions.Values.Select(x => x.ToSnapshot()).ToArray(), cancellationToken);
    }

    private void PruneExpiredSessions()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(MobileAutomationSessionStore.RetentionWindow);
        foreach (var item in sessions.ToArray())
        {
            if (!MobileAutomationSessionStore.IsRetained(item.Value.ToSnapshot(), cutoff))
            {
                sessions.TryRemove(item.Key, out _);
            }
        }
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

        var entryRejection = GetLongEntryGateRejection(strategy, latest, signal);
        if (entryRejection is not null)
        {
            throw new InvalidOperationException(entryRejection);
        }

        var executionSignal = ResolveExecutionOrderSignal(strategy, signal, state.SnapshotsByTimeframe)
            ?? throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");

        return new PreparedEntryExecution(signal, executionSignal);
    }

    private PreparedEntryExecution PrepareAutomationEntryExecution(
        BacktestRunConfig runConfig,
        StrategyDefinition strategy,
        MutableAutomationSession session,
        TickerMarketState state,
        string entryMode)
    {
        if (entryMode.Equals("immediate_paper", StringComparison.OrdinalIgnoreCase))
        {
            return PrepareImmediateEntryExecution(strategy, session.Ticker, state);
        }

        try
        {
            return PrepareEntryExecution(runConfig, strategy, session.Ticker, state);
        }
        catch (InvalidOperationException exception) when (CanFallbackToStockPulseImmediateEntry(session, exception))
        {
            session.Report(
                "entry_fallback",
                "Stock Pulse alert had usable price/ATR state but RVOL was unavailable. Entering paper trade and letting guardian manage exits.");
            return PrepareImmediateEntryExecution(strategy, session.Ticker, state);
        }
    }

    private static bool CanFallbackToStockPulseImmediateEntry(MutableAutomationSession session, InvalidOperationException exception)
    {
        return session.Source.Equals("notification", StringComparison.OrdinalIgnoreCase) &&
            exception.Message.Contains("relative_volume", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("atr", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("vwap", StringComparison.OrdinalIgnoreCase) &&
            !exception.Message.Contains("macd", StringComparison.OrdinalIgnoreCase);
    }

    private PreparedEntryExecution PrepareImmediateEntryExecution(
        StrategyDefinition strategy,
        string ticker,
        TickerMarketState state)
    {
        if (!state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            throw new InvalidOperationException($"Execution timeframe {strategy.Execution.Timeframe} is unavailable for {ticker}.");
        }

        var latest = executionSnapshots[^1];
        if (latest.Atr is null or <= 0m)
        {
            throw new InvalidOperationException("signal_missing_indicators (atr)");
        }

        var signal = new TradeSignal(
            Ticker: ticker,
            Timestamp: latest.Timestamp,
            Timeframe: latest.Timeframe,
            CurrentPrice: latest.CurrentPrice,
            CurrentVolume: latest.CurrentVolume,
            CurrentRsi: latest.Rsi ?? 50m,
            CurrentAtr: latest.Atr.Value,
            IsAboveVwap: latest.Vwap is not null && latest.CurrentPrice >= latest.Vwap.Value,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsVwapRejection: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsOpeningRangeBreakdown: false,
            IsRecentHighBreakout: false,
            IsRecentLowBreakdown: false,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: latest.Ema20 is not null && latest.CurrentPrice >= latest.Ema20.Value,
            IsPriceAboveEma50: latest.Ema50 is not null && latest.CurrentPrice >= latest.Ema50.Value,
            IsEma20AboveEma50: latest.Ema20 is not null && latest.Ema50 is not null && latest.Ema20.Value >= latest.Ema50.Value,
            VwapExtensionAtr: latest.Vwap is not null && latest.Atr is > 0m
                ? (latest.CurrentPrice - latest.Vwap.Value) / latest.Atr.Value
                : null,
            IsAboveBollingerMiddle: latest.BollingerMiddle is not null && latest.CurrentPrice >= latest.BollingerMiddle.Value,
            IsMacdHistogramPositive: latest.MacdHistogram is > 0m,
            IsMacdNotBearish: latest.MacdHistogram is null or >= 0m,
            IsPriceAboveEma10: latest.Ema10 is not null && latest.CurrentPrice >= latest.Ema10.Value,
            IsEma10AboveEma20: latest.Ema10 is not null && latest.Ema20 is not null && latest.Ema10.Value >= latest.Ema20.Value,
            SessionRelativeVolume: latest.SessionRelativeVolume,
            SlotRelativeVolume: latest.SlotRelativeVolume,
            Catalyst: latest.Catalyst);

        return new PreparedEntryExecution(signal, signal);
    }

    private static string NormalizeEntryMode(string? entryMode)
    {
        if (String.IsNullOrWhiteSpace(entryMode))
        {
            return "validate_strategy";
        }

        return entryMode.Trim().Equals("immediate_paper", StringComparison.OrdinalIgnoreCase)
            ? "immediate_paper"
            : "validate_strategy";
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

    internal string? GetLongEntryGateRejection(
        StrategyDefinition strategy,
        IndicatorSnapshot snapshot,
        TradeSignal signal)
    {
        var relativeVolume = ResolveEntryRelativeVolume(strategy, snapshot);
        if (relativeVolume is null)
        {
            return $"entry_relative_volume_unavailable (Source: {strategy.EntryRules.MinVolumeSpikeSource})";
        }

        return strategyEvaluator.GetLongEntryRejection(strategy, signal, relativeVolume.Value);
    }

    internal static decimal? ResolveEntryRelativeVolume(StrategyDefinition strategy, IndicatorSnapshot snapshot)
    {
        return strategy.EntryRules.MinVolumeSpikeSource.ToLowerInvariant() switch
        {
            "session_vs_average_day" or "session" or "finviz_style" => snapshot.SessionRelativeVolume,
            "slot_bar" or "bar_same_time" => snapshot.SlotRelativeVolume,
            _ => snapshot.RelativeVolume
        };
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
