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
public sealed partial class MobileAutomationService
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
    private readonly IOrderSubmissionService? orderSubmissionService;
    private readonly IOrderLifecycleService? orderLifecycleService;

    public MobileAutomationService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        MobileAutomationSessionStore sessionStore,
        ILogger<MobileAutomationService>? logger = null,
        ICandleStore? candleStore = null,
        IOrderStateRepository? orderRepo = null,
        IDecisionAuditRepository? auditRepo = null,
        IOrderSubmissionService? orderSubmissionService = null,
        IOrderLifecycleService? orderLifecycleService = null)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
        this.sessionStore = sessionStore;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileAutomationService>.Instance;
        this.candleStore = candleStore ?? NullCandleStore.Instance;
        this.orderRepo = orderRepo;
        this.auditRepo = auditRepo;
        this.orderSubmissionService = orderSubmissionService;
        this.orderLifecycleService = orderLifecycleService;
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

            if (orderSubmissionService is null)
            {
                throw new InvalidOperationException(
                    "Mobile order submission is not armed because the durable submission service is unavailable.");
            }

            session.Report("submitting_entry", $"Submitting entry for {session.Ticker} from {session.Source}. EntryMode={entryMode}.");
            var submittedAt = DateTimeOffset.UtcNow;
            var executionRunContext = ExecutionRunContextFactory.Create(
                session.SessionId,
                runConfig.Mode,
                new { Run = runConfig, Strategy = strategy },
                session.StartedAt ?? submittedAt);
            var submission = await orderSubmissionService.SubmitBracketOrderAsync(
                new BracketOrderSubmission(
                    session.SessionId,
                    new ValidatedEntryCandidate(
                        session.SessionId,
                        DiscoverySource: session.Source,
                        Horizon: strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)
                            ? "swing"
                            : "intraday",
                        DiscoveredAtUtc: execution.ExecutionSignal.Timestamp,
                        RevalidatedAtUtc: submittedAt,
                        SetupEvidenceJson: JsonSerializer.Serialize(execution.ExecutionSignal)),
                    executionRunContext,
                    strategy.StrategyId,
                    Side: "buy",
                    OrderType: ResolveEntryOrderType(runConfig),
                    TimeInForce: ResolveEntryTimeInForce(runConfig),
                    ExecutionRunContextFactory.ResolveSessionDate(submittedAt, strategy.Session.ExchangeTimezone),
                    submittedAt,
                    order,
                    AllowExtendedHoursTrading: runConfig.Execution.AllowExtendedHoursTrading),
                brokerClient,
                cancellationToken);
            var orderId = submission.BrokerOrderId;
            session.Status = "running";
            session.EntryOrderId = orderId;
            session.EntryPrice = order.LimitPrice;
            session.StopLossPrice = order.StopLossPrice;
            session.TakeProfitPrice = order.TakeProfitPrice;
            session.ShareQuantity = order.ShareQuantity;
            session.EntrySubmittedAt = DateTimeOffset.UtcNow;
            session.ExitSafetyOrdersSubmitted = !submission.SubmittedOutsideRegularHours;
            session.Report("running", $"Entry submitted: {order.ShareQuantity} shares of {session.Ticker} at {order.LimitPrice:F2}.");
            await PersistAsync(cancellationToken);

            if (orderRepo is not null)
            {
                await orderRepo.SaveOrderAsync(new PersistedOrder
                {
                    OrderId = orderId,
                    Ticker = session.Ticker,
                    RunName = session.RunName,
                    ClientOrderId = submission.ClientOrderId,
                    StrategyName = strategy.StrategyName,
                    Broker = runConfig.Execution.Broker,
                    Status = submission.SubmittedOutsideRegularHours ? "pending_exit_setup" : "new",
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

    private sealed record PreparedEntryExecution(TradeSignal Signal, TradeSignal ExecutionSignal);

    private static string ResolveEntryOrderType(BacktestRunConfig run) =>
        String.IsNullOrWhiteSpace(run.Execution.EntryOrderType)
            ? run.Execution.OrderType.Trim().ToLowerInvariant()
            : run.Execution.EntryOrderType.Trim().ToLowerInvariant();

    private static string ResolveEntryTimeInForce(BacktestRunConfig run) =>
        run.Execution.OrderExpiration.Equals("day", StringComparison.OrdinalIgnoreCase)
            ? "day"
            : "gtc";

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
