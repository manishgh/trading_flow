using System.Collections.Concurrent;
using System.Text.Json;
using TradingFlow.Domain.Audit;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
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
    private readonly MobileAutomationSessionCoordinator sessionCoordinator = new();
    private readonly SemaphoreSlim persistenceSync = new(1, 1);
    private readonly SimpleYamlReader yamlReader;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ProjectPaths paths;
    private readonly MobileAutomationSessionStore sessionStore;
    private readonly IOrderStateRepository? orderRepo;
    private readonly IDecisionAuditRepository? auditRepo;
    private readonly ILogger<MobileAutomationService> logger;
    private readonly ICandleStore candleStore;
    private readonly PositionGuardianEngine positionGuardianEngine = new();
    private readonly IOrderSubmissionService? orderSubmissionService;
    private readonly IOrderLifecycleService? orderLifecycleService;
    private readonly ConfigCatalogService configCatalog;
    private readonly IStrategyCandidateDecisionOrchestrator? candidateDecisions;
    private readonly ManualEntryOptions manualEntryOptions;
    private readonly TimeProvider timeProvider;
    private readonly TradingFlow.Engine.Regime.RegimeGateService regimeGate = new();

    public MobileAutomationService(
        SimpleYamlReader yamlReader,
        PaperRuntimeFactory runtimeFactory,
        ProjectPaths paths,
        MobileAutomationSessionStore sessionStore,
        ConfigCatalogService configCatalog,
        ILogger<MobileAutomationService>? logger = null,
        ICandleStore? candleStore = null,
        IOrderStateRepository? orderRepo = null,
        IDecisionAuditRepository? auditRepo = null,
        IOrderSubmissionService? orderSubmissionService = null,
        IOrderLifecycleService? orderLifecycleService = null,
        ICandidateRepository? candidateRepository = null,
        ManualEntryOptions? manualEntryOptions = null,
        TimeProvider? timeProvider = null)
    {
        this.yamlReader = yamlReader;
        this.runtimeFactory = runtimeFactory;
        this.paths = paths;
        this.sessionStore = sessionStore;
        this.configCatalog = configCatalog;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileAutomationService>.Instance;
        this.candleStore = candleStore ?? NullCandleStore.Instance;
        this.orderRepo = orderRepo;
        this.auditRepo = auditRepo;
        this.orderSubmissionService = orderSubmissionService;
        this.orderLifecycleService = orderLifecycleService;
        candidateDecisions = candidateRepository is null
            ? null
            : new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), candidateRepository);
        this.manualEntryOptions = manualEntryOptions ?? new ManualEntryOptions(ManualEntryPolicy.StrategyGated);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var restored = await sessionStore.LoadAsync(cancellationToken);
        foreach (var snapshot in restored)
        {
            var session = MutableAutomationSession.FromSnapshot(snapshot);
            if (session.Status is "queued" or "starting" or "running")
            {
                session.Update(current =>
                {
                    current.Status = "interrupted";
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    current.Report("interrupted", "Backend restarted before the automation session completed.");
                });
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
        var selectedStrategy = await configCatalog.RequireSelectableAsync(
            StrategySelectionMode.RunPaperShadow,
            paths.ResolveRepositoryPath(request.StrategyPath),
            cancellationToken);
        var strategy = selectedStrategy.Definition;
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

        var ownedCancellation = sessionCoordinator.Reserve(session.SessionId, normalizedTicker);
        if (!sessions.TryAdd(session.SessionId, session))
        {
            sessionCoordinator.Release(session.SessionId);
            throw new InvalidOperationException("Unable to register the reserved automation session.");
        }

        var entryMode = NormalizeEntryMode(request.EntryMode);
        session.Report("queued", $"Queued {request.Source} automation for {normalizedTicker}. EntryMode={entryMode}.");
        try
        {
            await PersistAsync(CancellationToken.None);
        }
        catch
        {
            sessions.TryRemove(session.SessionId, out _);
            sessionCoordinator.Release(session.SessionId);
            throw;
        }

        _ = Task.Run(() => RunAsync(
            session,
            runConfig,
            new AuthorizedRuntimeStrategy(
                selectedStrategy.Identity,
                StrategySelectionMode.RunPaperShadow,
                strategy,
                selectedStrategy.Artifact.AdmissionProfile),
            entryMode,
            ownedCancellation), CancellationToken.None);
        return session.ToSnapshot();
    }

    public void Cancel(Guid sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || !sessionCoordinator.TryCancel(sessionId))
        {
            return;
        }

        session.Update(current =>
        {
            current.Status = "cancelled";
            current.Report("cancelled", "User cancelled the automation session.");
        });
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
            var quantity = session.ToSnapshot().ShareQuantity ?? 0;
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
                sessionCoordinator.TryCancel(sessionId);
                session.Update(current =>
                {
                    current.Status = "completed";
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    current.ExitSubmittedAt = DateTimeOffset.UtcNow;
                    current.ExitReason = "manual_sell";
                    current.Report("completed", $"Manually sold {current.Ticker} from Running Trades.");
                });
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
        AuthorizedRuntimeStrategy runtimeStrategy,
        string entryMode,
        CancellationTokenSource ownedCancellation)
    {
        var strategy = runtimeStrategy.Definition;
        var cancellationToken = ownedCancellation.Token;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Update(current =>
            {
                current.Status = "starting";
                current.StartedAt = DateTimeOffset.UtcNow;
                current.Report("starting", $"Preparing market state for {current.Ticker}.");
            });
            await PersistAsync(cancellationToken);

            var marketDataProvider = runtimeFactory.CreateProvider(runConfig);
            var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
            using var providerDisposable = marketDataProvider as IDisposable;
            using var brokerDisposable = brokerClient as IDisposable;

            if (brokerClient is null)
            {
                throw new InvalidOperationException("No broker client available for automation.");
            }

            var state = await LoadTickerStateAsync(runConfig, strategy, session.Ticker, marketDataProvider, cancellationToken);
            var decisionAtUtc = timeProvider.GetUtcNow();
            var startedAt = session.ToSnapshot().StartedAt ?? decisionAtUtc;
            var executionRunContext = ExecutionRunContextFactory.Create(
                session.SessionId,
                runConfig.Mode,
                new { Run = runConfig, Strategy = strategy, EntryMode = entryMode },
                startedAt);
            var execution = await PrepareAutomationEntryExecutionAsync(
                runConfig,
                runtimeStrategy,
                session,
                state,
                marketDataProvider,
                executionRunContext,
                entryMode,
                decisionAtUtc,
                cancellationToken);

            if (brokerClient is not IBrokerAccountProvider accountProvider)
            {
                throw new InvalidOperationException(
                    "Broker account equity is unavailable; automation sizing fails closed.");
            }

            if (!state.BarsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                !state.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
                executionBars.Count == 0 ||
                executionSnapshots.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Execution market state {strategy.Execution.Timeframe} is unavailable for {session.Ticker}.");
            }

            var account = await accountProvider.GetAccountSnapshotAsync(cancellationToken);
            var order = execution.OperatorOverride
                ? PrepareOperatorDirectOrder(
                    runConfig,
                    strategy,
                    session.Ticker,
                    account.Equity,
                    execution.OperatorSnapshot
                        ?? throw new InvalidOperationException("Operator entry snapshot is unavailable."))
                : PrepareStrategyOrder(
                    runConfig,
                    session.Ticker,
                    account.Equity,
                    execution);

            if (orderSubmissionService is null)
            {
                throw new InvalidOperationException(
                    "Mobile order submission is not armed because the durable submission service is unavailable.");
            }

            session.Report("submitting_entry", $"Submitting entry for {session.Ticker} from {session.Source}. EntryMode={entryMode}.");
            var submittedAt = timeProvider.GetUtcNow();
            var submission = await orderSubmissionService.SubmitBracketOrderAsync(
                new BracketOrderSubmission(
                    session.SessionId,
                    execution.Candidate,
                    executionRunContext,
                    execution.OperatorOverride ? "OPERATOR-ALERT-ENTRY" : strategy.StrategyId,
                    Side: "buy",
                    OrderType: ResolveEntryOrderType(runConfig),
                    TimeInForce: ResolveEntryTimeInForce(runConfig),
                    ExecutionRunContextFactory.ResolveSessionDate(submittedAt, strategy.Session.ExchangeTimezone),
                    submittedAt,
                    order,
                    AllowExtendedHoursTrading: runConfig.Execution.AllowExtendedHoursTrading,
                    StrategyIdentity: execution.OperatorOverride ? null : runtimeStrategy.Identity,
                    StrategySelectionMode: execution.OperatorOverride ? null : runtimeStrategy.SelectionMode,
                    OperatorOverride: execution.OperatorOverride
                        ? OperatorOverrideAuthorization.Issue(
                            manualEntryOptions,
                            "mobile_automation",
                            $"Explicit operator-direct alert from {session.Source}",
                            submittedAt)
                        : null,
                    ExitPolicyIdentity: execution.OperatorOverride
                        ? runtimeStrategy.Identity
                        : null),
                brokerClient,
                cancellationToken);
            var orderId = submission.BrokerOrderId;
            session.Update(current =>
            {
                current.Status = "running";
                current.EntryOrderId = orderId;
                current.EntryPrice = order.LimitPrice;
                current.StopLossPrice = order.StopLossPrice;
                current.TakeProfitPrice = order.TakeProfitPrice;
                current.ShareQuantity = order.ShareQuantity;
                current.EntrySubmittedAt = DateTimeOffset.UtcNow;
                current.ExitSafetyOrdersSubmitted = !submission.SubmittedOutsideRegularHours;
                current.Report("running", $"Entry submitted: {order.ShareQuantity} shares of {current.Ticker} at {order.LimitPrice:F2}.");
            });
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
            session.Update(current =>
            {
                current.Status = "cancelled";
                current.FinishedAt = DateTimeOffset.UtcNow;
                current.Report("cancelled", $"Automation cancelled for {current.Ticker}.");
            });
            await PersistAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mobile automation session {SessionId} failed for {Ticker}.", session.SessionId, session.Ticker);
            session.Update(current =>
            {
                current.Status = "failed";
                current.ErrorMessage = exception.Message;
                current.FinishedAt = DateTimeOffset.UtcNow;
                current.Report("failed", exception.Message);
            });
            await PersistAsync();
        }
        finally
        {
            sessionCoordinator.Release(session.SessionId);
            await PersistAsync();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        await persistenceSync.WaitAsync(cancellationToken);
        try
        {
            PruneExpiredSessions();
            var snapshot = sessions.Values.Select(x => x.ToSnapshot()).ToArray();
            await sessionStore.SaveAsync(snapshot, cancellationToken);
        }
        finally
        {
            persistenceSync.Release();
        }
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

    private sealed record PreparedEntryExecution(
        TradeSignal? Signal,
        TradeSignal? ExecutionSignal,
        ValidatedEntryCandidate Candidate,
        bool OperatorOverride,
        IndicatorSnapshot? OperatorSnapshot = null,
        StrategyOrderPlan? OrderPlan = null);

    private static string ResolveEntryOrderType(BacktestRunConfig run) =>
        String.IsNullOrWhiteSpace(run.Execution.EntryOrderType)
            ? run.Execution.OrderType.Trim().ToLowerInvariant()
            : run.Execution.EntryOrderType.Trim().ToLowerInvariant();

    private static string ResolveEntryTimeInForce(BacktestRunConfig run) =>
        run.Execution.OrderExpiration.Equals("day", StringComparison.OrdinalIgnoreCase)
            ? "day"
            : "gtc";

    internal sealed class MutableAutomationSession
    {
        private readonly object sync = new();
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

        public void Update(Action<MutableAutomationSession> mutation)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            lock (sync)
            {
                mutation(this);
            }
        }

        public void Report(string stage, string message)
        {
            lock (sync)
            {
                CurrentStage = stage;
                events.Add($"{DateTimeOffset.Now:HH:mm:ss} {stage}: {message}");
                if (events.Count > 80)
                {
                    events.RemoveAt(0);
                }
            }
        }

        public MobileAutomationSessionSnapshot ToSnapshot()
        {
            lock (sync)
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
                    SourceMessage,
                    ExitSafetyOrdersSubmitted);
            }
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
                ExitReason = snapshot.ExitReason,
                ExitSafetyOrdersSubmitted = snapshot.ExitSafetyOrdersSubmitted
            };

            foreach (var item in snapshot.Events)
            {
                session.events.Add(item);
            }

            return session;
        }
    }
}
