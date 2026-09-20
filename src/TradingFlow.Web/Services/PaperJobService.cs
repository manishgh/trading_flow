using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services.Discovery;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Web.Services;

public sealed record PaperJobServiceOptions(
    int MaximumConcurrentJobs,
    int MaximumPendingJobs,
    TimeSpan SnapshotInterval,
    TimeSpan RecoveryReadinessTimeout)
{
    public static PaperJobServiceOptions Default { get; } = new(
        4,
        16,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));

    public PaperJobServiceOptions Validate()
    {
        if (MaximumConcurrentJobs <= 0 || MaximumPendingJobs <= 0 ||
            SnapshotInterval <= TimeSpan.Zero || RecoveryReadinessTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentJobs));
        }

        return this;
    }
}

public sealed class PaperJobService : IDurableJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, MutablePaperJob> jobs = new();
    private readonly SimpleYamlReader yamlReader;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ILogger<PaperJobService> logger;
    private readonly ILogger<LiveRunner> liveRunnerLogger;
    private readonly IArtifactWriter artifactWriter;
    private readonly ICandleStore candleStore;
    private readonly IOrderSubmissionService? orderSubmissionService;
    private readonly IOrderLifecycleService? orderLifecycleService;
    private readonly TradingFlow.Domain.Persistence.IOrderIntentRepository? orderIntents;
    private readonly TradingFlow.Domain.Persistence.IOrderEventRepository? orderEvents;
    private readonly TradingFlow.Domain.Persistence.IPositionLedgerRepository? positionLedger;
    private readonly ConfigCatalogService configCatalog;
    private readonly IPaperDiscoverySessionFactory? discoverySessionFactory;
    private readonly TradingFlow.Engine.Pipeline.IMarketStateSnapshotProvider? marketStateSnapshots;
    private readonly TradingFlow.Domain.Persistence.ICandidateRepository? candidateRepository;
    private readonly IDurableJobRepository durableJobs;
    private readonly IPaperJobRecoveryService? recoveryService;
    private readonly PaperJobServiceOptions jobOptions;
    private int retentionPruned;

    public PaperJobService(
        SimpleYamlReader yamlReader,
        IServiceScopeFactory scopeFactory,
        AlpacaCredentialProvider alpacaCredentials,
        ProjectPaths paths,
        ILogger<PaperJobService>? logger = null,
        ILogger<LiveRunner>? liveRunnerLogger = null,
        ILogger<TradingFlow.Alpaca.AlpacaNewsProvider>? alpacaNewsLogger = null,
        IArtifactWriter? artifactWriter = null,
        ICandleStore? candleStore = null,
        IConfiguration? configuration = null,
        TradingFlow.Domain.Locking.ITickerLockService? lockService = null,
        TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo = null,
        PaperRuntimeFactory? runtimeFactory = null,
        IOrderSubmissionService? orderSubmissionService = null,
        IOrderLifecycleService? orderLifecycleService = null,
        TradingFlow.Domain.Persistence.IOrderIntentRepository? orderIntents = null,
        TradingFlow.Domain.Persistence.IOrderEventRepository? orderEvents = null,
        TradingFlow.Domain.Persistence.IPositionLedgerRepository? positionLedger = null,
        ConfigCatalogService? configCatalog = null,
        IPaperDiscoverySessionFactory? discoverySessionFactory = null,
        TradingFlow.Engine.Pipeline.IMarketStateSnapshotProvider? marketStateSnapshots = null,
        TradingFlow.Domain.Persistence.ICandidateRepository? candidateRepository = null,
        IDurableJobRepository? durableJobs = null,
        IPaperJobRecoveryService? recoveryService = null,
        PaperJobServiceOptions? jobOptions = null)
    {
        this.yamlReader = yamlReader;
        _ = alpacaCredentials;
        _ = paths;
        this.runtimeFactory = runtimeFactory ?? new PaperRuntimeFactory(alpacaCredentials, paths, alpacaNewsLogger);
        this.logger = logger ?? NullLogger<PaperJobService>.Instance;
        this.liveRunnerLogger = liveRunnerLogger ?? NullLogger<LiveRunner>.Instance;
        _ = alpacaNewsLogger ?? NullLogger<TradingFlow.Alpaca.AlpacaNewsProvider>.Instance;
        this.artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
        this.candleStore = candleStore ?? NullCandleStore.Instance;
        _ = configuration;
        _lockService = lockService;
        _auditRepo = auditRepo;
        this.orderSubmissionService = orderSubmissionService;
        this.orderLifecycleService = orderLifecycleService;
        this.orderIntents = orderIntents;
        this.orderEvents = orderEvents;
        this.positionLedger = positionLedger;
        this.configCatalog = configCatalog
            ?? throw new ArgumentNullException(nameof(configCatalog));
        this.discoverySessionFactory = discoverySessionFactory;
        this.marketStateSnapshots = marketStateSnapshots;
        this.candidateRepository = candidateRepository;
        this.durableJobs = durableJobs ?? ResolveDurableRepository(scopeFactory);
        this.recoveryService = recoveryService;
        this.jobOptions = (jobOptions ?? PaperJobServiceOptions.Default).Validate();
    }

    public string JobType => "paper";
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReconcileBrokerExposure;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref retentionPruned, 1) == 0)
        {
            await durableJobs.PruneTerminalJobsOlderThanAsync(
                DateTimeOffset.UtcNow.Subtract(MobileAutomationSessionStore.RetentionWindow),
                cancellationToken);
        }
        var allJobs = await durableJobs.GetJobsAsync(JobType, cancellationToken);

        foreach (var pJob in allJobs)
        {
            await PublishResultMarkerAsync(pJob, cancellationToken);
            var job = new MutablePaperJob(pJob.Id, pJob.RunName, pJob.ConfigPath, pJob.CreatedAt)
            {
                Status = pJob.Status,
                StartedAt = pJob.StartedAt,
                FinishedAt = pJob.FinishedAt,
                ErrorMessage = pJob.ErrorMessage
            };
            if (!String.IsNullOrWhiteSpace(pJob.SnapshotJson))
            {
                var snapshot = JsonSerializer.Deserialize<BacktestJobSnapshot>(pJob.SnapshotJson, JsonOptions);
                if (snapshot is not null)
                {
                    job.ApplySnapshot(snapshot);
                }
            }
            jobs[job.JobId] = job;
        }
    }

    public Task RefreshProjectionAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken);

    public async Task<Guid> StartJobAsync(
        string configPath,
        string runName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        cancellationToken.ThrowIfCancellationRequested();

        var fullConfigPath = Path.GetFullPath(configPath);
        var job = new MutablePaperJob(Guid.NewGuid(), runName, fullConfigPath, DateTimeOffset.UtcNow);
        try
        {
            var runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
            var fileRequest = DurableFileJobRequest.Capture([fullConfigPath, .. runConfig.Strategies]);
            using (fileRequest.AcquireVerifiedReadLease())
            {
                runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
                fileRequest.EnsureContains([fullConfigPath, .. runConfig.Strategies]);
            }
            var persisted = new PersistedJob
            {
                Id = job.JobId,
                JobType = "paper",
                RunName = job.RunName,
                ConfigPath = job.ConfigPath,
                RequestJson = fileRequest.Serialize(),
                SnapshotJson = JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions),
                Status = job.Status,
                CreatedAt = job.CreatedAt
            };
            if (!await durableJobs.TryEnqueueAsync(persisted, jobOptions.MaximumPendingJobs, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"The paper queue already contains its maximum of {jobOptions.MaximumPendingJobs} non-terminal jobs.");
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Paper job {JobId} for run {RunName} was not scheduled because queued-state persistence failed.",
                job.JobId,
                job.RunName);
            throw;
        }

        jobs.GetOrAdd(job.JobId, job);

        return job.JobId;
    }

    public async Task<BacktestJobSnapshot> StartAsync(
        string runName,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        var jobId = await StartJobAsync(configPath, runName, cancellationToken);
        return jobs[jobId].ToSnapshot();
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var stored = await durableJobs.GetJobAsync(jobId, cancellationToken);
        if (stored is null || !stored.JobType.Equals(JobType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsTerminalStatus(stored.Status))
        {
            return false;
        }

        var accepted = await durableJobs.RequestCancellationAsync(jobId, DateTimeOffset.UtcNow, cancellationToken);
        var refreshed = await durableJobs.GetJobAsync(jobId, cancellationToken);
        if (refreshed is not null)
        {
            var job = jobs.GetOrAdd(jobId, _ => new MutablePaperJob(
                refreshed.Id, refreshed.RunName, refreshed.ConfigPath, refreshed.CreatedAt));
            job.ApplyPersistedState(refreshed);
            job.Report("cancelling", "Cancellation requested. Waiting for the paper runner to drain.", null, 0, 0);
        }

        return accepted;
    }

    public async Task<bool> CancelAllBrokerOrdersAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return false;

        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);

        if (brokerClient == null) return false;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var openOrders = await brokerClient.GetOpenOrdersAsync(cts.Token);
            var ownedClientOrderIds = await GetRunScopedClientOrderIdsAsync(job, cts.Token);
            var runOrders = openOrders
                .Where(order =>
                    ownedClientOrderIds.Contains(order.ClientOrderId) ||
                    order.ParentClientOrderId is not null &&
                    ownedClientOrderIds.Contains(order.ParentClientOrderId))
                .ToArray();

            if (runOrders.Length == 0)
            {
                job.Report(job.CurrentStage, "No open broker orders were found for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
                return true;
            }

            var cancelled = 0;
            foreach (var order in runOrders)
            {
                if (orderSubmissionService is null)
                {
                    throw new InvalidOperationException(
                        "Paper order cancellation requires the common order command service.");
                }

                var state = await orderSubmissionService.RequestCancelAsync(
                    new OrderCancellationSubmission(
                        order.ParentClientOrderId ?? order.ClientOrderId,
                        order.OrderId,
                        "operator_cancel_paper_run_orders",
                        DateTimeOffset.UtcNow),
                    brokerClient,
                    cts.Token);
                if (OrderStateMachine.IsTerminal(state.State))
                {
                    cancelled++;
                }
            }

            if (cancelled == runOrders.Length)
            {
                job.Report(job.CurrentStage, $"Cancelled {cancelled} open broker order(s) for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }
            else
            {
                job.Report(job.CurrentStage, $"Cancelled {cancelled}/{runOrders.Length} open broker order(s) for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }

            return cancelled == runOrders.Length;
        }
        catch (Exception ex)
        {
            job.Report(job.CurrentStage, $"Broker failed to cancel orders: {ex.Message}", null, job.CompletedTickerCount, job.TotalTickerCount);
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

    public async Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>> GetOpenOrdersAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var orders = await brokerClient.GetOpenOrdersAsync(cts.Token);
            var ownedClientOrderIds = await GetRunScopedClientOrderIdsAsync(job, cts.Token);
            return orders
                .Where(order =>
                    ownedClientOrderIds.Contains(order.ClientOrderId) ||
                    order.ParentClientOrderId is not null &&
                    ownedClientOrderIds.Contains(order.ParentClientOrderId))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to load open broker orders for paper job {JobId}.",
                jobId);
            return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.BrokerPosition>> GetOpenPositionsAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();

        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var positions = await brokerClient.GetOpenPositionsAsync(cts.Token);
            var scopedTickers = await GetRunScopedActiveTickersAsync(job, cts.Token);
            if (scopedTickers.Count == 0)
            {
                return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();
            }

            return positions
                .Where(position => scopedTickers.Contains(position.Ticker))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to load open broker positions for paper job {JobId}.",
                jobId);
            return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public async Task<bool> ClosePositionAsync(Guid jobId, string ticker)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return false;
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return false;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var scopedQuantity = await GetRunScopedActiveQuantityAsync(job, ticker, cts.Token);
            if (scopedQuantity <= 0)
            {
                job.Report(job.CurrentStage, $"Refused to close {ticker}: no active order ownership was found for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
                return false;
            }

            var activeOwners = await GetActiveOwnerRunIdsAsync(ticker, cts.Token);
            if (activeOwners.Any(owner => owner != job.JobId))
            {
                job.Report(job.CurrentStage, $"Refused to close {ticker}: another active run also owns this ticker.", null, job.CompletedTickerCount, job.TotalTickerCount);
                return false;
            }

            var brokerPosition = (await brokerClient.GetOpenPositionsAsync(cts.Token))
                .FirstOrDefault(position => position.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            if (brokerPosition is null || brokerPosition.Qty <= 0)
            {
                job.Report(job.CurrentStage, $"No open broker position was found for {ticker}.", null, job.CompletedTickerCount, job.TotalTickerCount);
                return false;
            }

            if (orderSubmissionService is null || orderIntents is null)
            {
                throw new InvalidOperationException(
                    "Paper exits require the durable order command service and run journal.");
            }

            var productionRun = await orderIntents.GetRunAsync(job.JobId, cts.Token)
                ?? throw new InvalidOperationException(
                    $"Paper run {job.JobId:N} has no durable execution provenance.");
            var executionRunContext = new ExecutionRunContext(
                productionRun.RunId,
                productionRun.Profile,
                productionRun.ConfigHash,
                productionRun.CodeVersion,
                productionRun.StartedAtUtc);

            var closeQuantity = Math.Min(scopedQuantity, (int)Math.Floor(brokerPosition.Qty));
            var submitted = await orderSubmissionService.SubmitPositionExitAsync(
                new PositionExitSubmission(
                    executionRunContext,
                    ticker,
                    closeQuantity,
                    "operator_requested_paper_exit",
                    DateTimeOffset.UtcNow,
                    runConfig.Execution.AllowExtendedHoursTrading),
                brokerClient,
                cts.Token);
            job.Report(
                job.CurrentStage,
                $"Broker accepted an exit for {closeQuantity} share(s) of {ticker}. ClientOrderId={submitted.ClientOrderId}.",
                null,
                job.CompletedTickerCount,
                job.TotalTickerCount);
            return true;
        }
        catch (PositionExitNoLongerRequiredException)
        {
            job.Report(job.CurrentStage, $"{ticker} is already flat; no additional exit was submitted.", null, job.CompletedTickerCount, job.TotalTickerCount);
            return true;
        }
        catch (Exception ex)
        {
            job.Report(job.CurrentStage, $"Failed to close position for {ticker}: {ex.Message}", null, job.CompletedTickerCount, job.TotalTickerCount);
            return false;
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public IReadOnlyList<BacktestJobSnapshot> List()
    {
        return jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => job.ToSnapshot())
            .ToArray();
    }

    public BacktestJobSnapshot? Get(Guid jobId)
    {
        return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
    }

    public async Task RecoverAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        var request = DurableFileJobRequest.Deserialize(persisted.RequestJson);
        using var inputLease = request.AcquireVerifiedReadLease();
        var run = yamlReader.ReadBacktestRun(persisted.ConfigPath);
        request.EnsureContains([persisted.ConfigPath, .. run.Strategies]);
        if (recoveryService is null)
        {
            throw new InvalidOperationException(
                "Paper recovery requires the broker reconciliation service.");
        }
        await recoveryService.RecoverAsync(persisted, cancellationToken);
    }

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
            var request = DurableFileJobRequest.Deserialize(context.Job.RequestJson);
            using var inputLease = request.AcquireVerifiedReadLease();
            var job = jobs.GetOrAdd(
                context.Job.Id,
                _ => new MutablePaperJob(
                    context.Job.Id,
                    context.Job.RunName,
                    context.Job.ConfigPath,
                    context.Job.CreatedAt));
            job.Status = "running";
            job.StartedAt ??= context.Job.StartedAt ?? DateTimeOffset.UtcNow;
            job.Report("starting", "Paper durable worker started.", null, 0, 0);
            await context.SaveSnapshotAsync(
                JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions),
                cancellationToken);

            var logicalRunConfig = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(job.ConfigPath));
            request.EnsureContains([job.ConfigPath, .. logicalRunConfig.Strategies]);
            foreach (var strategyPath in logicalRunConfig.Strategies)
            {
                await configCatalog.RequireAuthorizedPaperRunSnapshotAsync(
                    strategyPath,
                    cancellationToken);
            }
            var validatedStrategies = logicalRunConfig.Strategies
                .Select(configCatalog.ReadAndValidatePaperSnapshot)
                .ToArray();
            var strategies = validatedStrategies
                .Select(strategy => strategy.Definition)
                .ToArray();
            var runtimeStrategies = validatedStrategies
                .Select(strategy => new AuthorizedRuntimeStrategy(
                    strategy.Manifest.Identity,
                    strategy.Manifest.SelectionMode,
                    strategy.Definition,
                    strategy.Manifest.AdmissionProfile))
                .ToArray();
            var executionRunContext = ExecutionRunContextFactory.Create(
                job.JobId,
                logicalRunConfig.Mode,
                new { Run = logicalRunConfig, Strategies = strategies },
                job.StartedAt ?? DateTimeOffset.UtcNow);
            var runConfig = ScopeAttemptArtifacts(
                logicalRunConfig,
                job.JobId,
                context.LeaseToken);
            ClearLiveChartSnapshots(runConfig);
            Directory.CreateDirectory(Path.Combine(runConfig.ResultsRoot, "live", runConfig.RunName));

            var provider = runtimeFactory.CreateProvider(runConfig);
            var newsProvider = runtimeFactory.CreateNewsProvider(runConfig);
            var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
            if (runConfig.Discovery is { Enabled: true } && discoverySessionFactory is null)
            {
                throw new InvalidOperationException("Durable discovery is enabled but no discovery session factory is registered.");
            }

            if (strategies.Any(strategy => !TimeframeParser.IsDailyOrHigher(strategy.Timeframe)))
            {
                throw new InvalidOperationException(
                    "Paper trading supports swing strategies only. Every strategy must use a daily setup timeframe.");
            }

            await using var discoverySession = discoverySessionFactory?.Create(job.JobId, runConfig, "swing");

            var runner = new LiveRunner(
                provider,
                newsProvider,
                brokerClient,
                _lockService,
                orderIntents,
                orderEvents,
                positionLedger,
                _auditRepo,
                liveRunnerLogger,
                artifactWriter,
                candleStore,
                orderSubmissionService,
                executionRunContext,
                orderLifecycleService,
                discoverySession,
                marketStateSnapshots,
                candidateRepository);

            var progress = new Progress<string>(msg =>
            {
                job.Report("running", msg, null, 0, 0);
            });

            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(runConfig.RunName);
            var runTask = runner.RunAsync(runConfig, runtimeStrategies, cancellationToken, progress);
            while (!runTask.IsCompleted)
            {
                var delay = Task.Delay(jobOptions.SnapshotInterval, cancellationToken);
                if (await Task.WhenAny(runTask, delay) == runTask)
                {
                    break;
                }

                await context.SaveSnapshotAsync(
                    JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions),
                    cancellationToken);
            }
            await runTask;
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = job.ToTerminalSnapshot("completed", DateTimeOffset.UtcNow, null);
            return DurableJobCompletion.Completed(
                JsonSerializer.Serialize(snapshot, JsonOptions),
                Path.Combine(runConfig.ResultsRoot, "live", runConfig.RunName));
    }

    public async Task OnTerminalPublishedAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        await PublishResultMarkerAsync(persisted, cancellationToken);
        var job = jobs.GetOrAdd(
            persisted.Id,
            _ => new MutablePaperJob(persisted.Id, persisted.RunName, persisted.ConfigPath, persisted.CreatedAt));
        if (!String.IsNullOrWhiteSpace(persisted.SnapshotJson))
        {
            var snapshot = JsonSerializer.Deserialize<BacktestJobSnapshot>(persisted.SnapshotJson, JsonOptions);
            if (snapshot is not null)
            {
                job.ApplySnapshot(snapshot);
            }
        }

        job.ApplyPersistedState(persisted);
        if (persisted.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            job.Report("cancelled", "Paper runner drained and cancellation is complete.", null, 0, 0);
        }

    }

    private async Task PublishResultMarkerAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        try
        {
            await DurableJobResultPublication.PublishIfCompletedAsync(persisted, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Durable paper run {JobId} result marker is not available yet; projection remains visible and the next refresh will retry.",
                persisted.Id);
        }
    }

    private static bool IsTerminalStatus(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    internal static BacktestRunConfig ScopeAttemptArtifacts(
        BacktestRunConfig logicalRunConfig,
        Guid jobId,
        Guid leaseToken) =>
        logicalRunConfig with
        {
            ResultsRoot = Path.Combine(
                logicalRunConfig.ResultsRoot,
                ".durable-attempts",
                "paper",
                jobId.ToString("N"),
                leaseToken.ToString("N"))
        };

    private static IDurableJobRepository ResolveDurableRepository(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
    }

    private void ClearLiveChartSnapshots(BacktestRunConfig run)
    {
        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName);
        if (!Directory.Exists(resultsDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(resultsDir, "*_chart.json", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to delete stale chart snapshot {ChartSnapshotPath}; the next writer may replace it.",
                    file);
            }
        }
    }

    private async Task<HashSet<string>> GetRunScopedActiveTickersAsync(MutablePaperJob job, CancellationToken cancellationToken)
    {
        if (positionLedger is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var positions = await positionLedger.ListCurrentForRunAsync(job.JobId, cancellationToken);
        return positions
            .Where(item => item.Position.Quantity != 0m)
            .Select(item => item.Position.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<decimal> GetRunScopedActiveQuantityAsync(MutablePaperJob job, string ticker, CancellationToken cancellationToken)
    {
        if (positionLedger is null)
        {
            return 0m;
        }

        var positions = await positionLedger.ListCurrentForRunAsync(job.JobId, cancellationToken);
        return positions
            .Where(item => item.Position.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            .Sum(item => Math.Abs(item.Position.Quantity));
    }

    private async Task<IReadOnlyList<Guid>> GetActiveOwnerRunIdsAsync(string ticker, CancellationToken cancellationToken)
    {
        if (positionLedger is null)
        {
            return Array.Empty<Guid>();
        }

        var positions = await positionLedger.ListCurrentOwnersForSymbolAsync(ticker, cancellationToken);
        return positions
            .Select(item => item.OwningRunId)
            .Distinct()
            .ToArray();
    }

    private async Task<HashSet<string>> GetRunScopedClientOrderIdsAsync(
        MutablePaperJob job,
        CancellationToken cancellationToken)
    {
        var clientOrderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (orderIntents is null)
        {
            return clientOrderIds;
        }

        var intents = await orderIntents.ListByRunAsync(job.JobId, cancellationToken);
        foreach (var intent in intents)
        {
            if (!String.IsNullOrWhiteSpace(intent.ClientOrderId))
            {
                clientOrderIds.Add(intent.ClientOrderId);
            }
        }

        return clientOrderIds;
    }

    private sealed class MutablePaperJob
    {
        private readonly object stateLock = new();

        public MutablePaperJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
        {
            JobId = jobId; RunName = runName; ConfigPath = configPath; CreatedAt = createdAt; Status = "queued";
        }
        public Guid JobId { get; }
        public string RunName { get; }
        public string ConfigPath { get; }
        public string Status { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string CurrentStage { get; private set; } = "queued";
        public int CompletedTickerCount { get; private set; }
        public int TotalTickerCount { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public void Report(string stage, string message, string? ticker, int completedTickerCount, int totalTickerCount)
        {
            lock (stateLock)
            {
                CurrentStage = stage; CompletedTickerCount = completedTickerCount; TotalTickerCount = totalTickerCount;
                var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
                Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}{tickerText}: {message}");
                while (Events.Count > 80 && Events.TryDequeue(out _)) { }
            }
        }

        public void ApplyPersistedState(PersistedJob persisted)
        {
            lock (stateLock)
            {
                Status = persisted.Status;
                StartedAt = persisted.StartedAt;
                FinishedAt = persisted.FinishedAt;
                ErrorMessage = persisted.ErrorMessage;
            }
        }

        public void ApplySnapshot(BacktestJobSnapshot snapshot)
        {
            lock (stateLock)
            {
                CurrentStage = snapshot.CurrentStage;
                CompletedTickerCount = snapshot.CompletedTickerCount;
                TotalTickerCount = snapshot.TotalTickerCount;
                while (Events.TryDequeue(out _)) { }
                foreach (var entry in snapshot.Events.TakeLast(80))
                {
                    Events.Enqueue(entry);
                }
            }
        }

        public BacktestJobSnapshot ToSnapshot() =>
            ToTerminalSnapshot(Status, FinishedAt, ErrorMessage);

        public BacktestJobSnapshot ToTerminalSnapshot(
            string status,
            DateTimeOffset? finishedAt,
            string? error)
        {
            lock (stateLock)
            {
                return new BacktestJobSnapshot(
                    JobId, RunName, ConfigPath, status, CreatedAt, StartedAt, finishedAt, error,
                    CurrentStage, CompletedTickerCount, TotalTickerCount, Events.ToArray(), null);
            }
        }
    }
}
