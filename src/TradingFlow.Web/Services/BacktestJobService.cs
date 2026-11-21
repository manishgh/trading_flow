using System.Collections.Concurrent;
using System.Runtime;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public interface IBacktestRunExecutor
{
    Task<BacktestResult> RunAsync(
        string configPath,
        string resultPath,
        DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null);

    /// <summary>Assessment execution requires an executor that propagates the exact retained evidence and its live owner guard.</summary>
    Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken, FrozenBacktestEvidence verified, Action<EngineConfig> requireLiveScope,
        IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null) =>
        throw new NotSupportedException("This executor does not support operation-scoped evidence verification.");
}

public sealed class BacktestRunExecutor(BacktestRunner runner) : IBacktestRunExecutor
{
    /// <summary>Passes one worker's existing provider and required lifetime guard through all runner workspaces.</summary>
    public Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken, FrozenBacktestEvidence verified, Action<EngineConfig> requireLiveScope,
        IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null) =>
        runner.RunAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, verified, requireLiveScope, progress, preparedDataReference);

    public Task<BacktestResult> RunAsync(
        string configPath,
        string resultPath,
        DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null) =>
        runner.RunAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, progress, preparedDataReference);
}

public enum BacktestCancellationOutcome
{
    Accepted,
    NotFound,
    AlreadyFinished
}

public sealed record BacktestJobServiceOptions(
    int MaxConcurrentJobs,
    int MaximumPendingJobs,
    int RetainedTerminalJobs,
    int RetainedRecentTrades,
    int RetainedMissedMoves,
    TimeSpan SnapshotInterval)
{
    public static BacktestJobServiceOptions Default { get; } = new(
        MaxConcurrentJobs: 1,
        MaximumPendingJobs: 16,
        RetainedTerminalJobs: 20,
        RetainedRecentTrades: 80,
        RetainedMissedMoves: 100,
        SnapshotInterval: TimeSpan.FromSeconds(1));
}

/// <summary>
/// Application-facing backtest façade and durable execution handler. Requests are
/// admitted by the database queue and executed only by the hosted durable worker.
/// </summary>
public sealed class BacktestJobService : IDurableJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, MutableBacktestJob> jobs = new();
    private readonly IBacktestRunExecutor runner;
    private readonly IDurableJobRepository repository;
    private readonly SimpleYamlReader yamlReader;
    private readonly BacktestJobServiceOptions options;
    private readonly ILogger<BacktestJobService> logger;
    private readonly StrategyRunArtifactValidator strategyValidator;
    private readonly MonthlyRunResultWriter monthlyResults;
    private readonly ResearchQueue research;
    private readonly DurableResultMarkers markers;

    /// <summary>Use the existing durable worker and immutable strategy validator for execution and monthly publication.</summary>
    public BacktestJobService(
        IBacktestRunExecutor runner,
        IDurableJobRepository repository,
        SimpleYamlReader yamlReader,
        BacktestJobServiceOptions options,
        StrategyArtifactCatalog strategyArtifacts,
        ResearchQueue research,
        ILogger<BacktestJobService>? logger = null,
        ProjectPaths? paths = null)
    {
        this.runner = runner;
        this.repository = repository;
        this.yamlReader = yamlReader;
        this.research = research;
        this.options = ValidateOptions(options);
        this.logger = logger ?? NullLogger<BacktestJobService>.Instance;
        markers = new DurableResultMarkers("backtest", this.logger, paths);
        strategyValidator = new StrategyRunArtifactValidator(yamlReader, strategyArtifacts);
        monthlyResults = new MonthlyRunResultWriter(strategyValidator);
    }

    public string JobType => "backtest";
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var persisted in await repository.GetJobsAsync(JobType, cancellationToken))
        {
            await markers.PublishAsync(persisted, cancellationToken);
            await RecordResearchResultAsync(persisted, cancellationToken);
            jobs[persisted.Id] = MutableBacktestJob.FromPersisted(persisted, JsonOptions);
        }

        PruneTerminalJobs();
    }

    public Task RefreshProjectionAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken);

    /// <summary>Captures immutable inputs before admission and discards only this rejected job's uniquely owned parameter file.</summary>
    public async Task<BacktestJobSnapshot> StartAsync(
        string runName,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullConfigPath = Path.GetFullPath(configPath);
        var runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
        var metadata = DurableFileJobRequest.Capture(BacktestRunInputFiles.ResolveForResearchRegistration(fullConfigPath, runConfig));
        using var frozenMetadata = metadata.AcquireVerifiedReadLease();
        runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
        metadata.EnsureContains(BacktestRunInputFiles.ResolveForResearchRegistration(fullConfigPath, runConfig));
        var strategies = runConfig.Strategies.Select(path => strategyValidator.ReadAndAuthorizeRuntimeArtifact(path,
            TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest)).ToArray();
        var inputHash = EvidenceCanonicalJson.ComputeSha256(metadata.Files);
        const string route = "web-file-backtest";
        var key = runName.Trim() + ":" + inputHash;
        var jobId = ResearchQueue.FileJobId(route, key);
        var researchStart = ResearchFileStart.Portfolio(route, key, jobId.ToString("N"), inputHash, research.CodeSha256, runConfig, strategies);
        var preparedPath = Path.Combine(Path.GetFullPath(runConfig.ResultsRoot), "_prepared-requests", jobId.ToString("N"),
            Guid.NewGuid().ToString("N"), "request.json");
        PersistedJob? staged = null;
        try
        {
            var accepted = await research.AdmitAsync(researchStart, options.MaximumPendingJobs, async (start, token) =>
            {
                staged = await PrepareFileJobAsync(jobId, runName, fullConfigPath, runConfig, start.RegisteredAtUtc, preparedPath, token);
                return new[] { staged };
            }, cancellationToken);
            var persisted = accepted.Single();
            var projectedJob = jobs.GetOrAdd(jobId, _ => MutableBacktestJob.FromPersisted(persisted, JsonOptions));
            PruneTerminalJobs();
            return projectedJob.ToSnapshot();
        }
        finally
        {
            if (staged is not null && !await research.OwnsInputsAsync(staged, CancellationToken.None) &&
                DurableFileJobRequest.Deserialize(staged.RequestJson).PreparedMarketData is { } reference)
                await PreparedRequestStagingCleanup.DiscardAsync(reference, preparedPath, repository, jobId);
        }
    }

    /// <summary>Prepares exact captured observations only after the registry has admitted the frozen file intent.</summary>
    private async Task<PersistedJob> PrepareFileJobAsync(Guid id, string runName, string path, BacktestRunConfig run, DateTimeOffset cutoff,
        string preparedPath, CancellationToken token)
    {
        using var evidenceScope = WorkflowEvidenceVerificationScope.Open(this, run.Engine);
        var job = new MutableBacktestJob(id, runName, path, cutoff);
        var inputs = ResolveInputs(path, run, evidenceScope);
        using var lease = DurableFileJobRequest.Capture(inputs).AcquireVerifiedReadLease();
        PreparedMarketDataRequestReference? reference = null;
        try
        {
            if (run.Provider.Equals("finviz", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Finviz does not provide a raw API for historical OHLCV bars. Use Alpaca or frozen catalog evidence.");
            if (!run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var dataRequest = new PreparedMarketDataRequest(1, "run-" + id.ToString("N"), cutoff,
                    [new(id.ToString("N"), run with { Strategies = [] }, run.Strategies.Select(yamlReader.ReadStrategy).ToArray())]);
                new PreparedMarketDataService(ConfiguredDataProviders.FromEnvironment()).ValidateRequest(dataRequest);
                reference = await PreparedMarketDataService.WriteRequestAsync(dataRequest, preparedPath, token);
            }
            var request = DurableFileJobRequest.Capture(reference is null ? inputs : inputs.Append(reference.Path)) with
                { CapturedAtUtc = cutoff, PreparedMarketData = reference };
            using var verified = request.AcquireVerifiedReadLease();
            request.EnsureContains(ResolveInputs(path, yamlReader.ReadBacktestRun(path), evidenceScope));
            return new PersistedJob
            {
                Id = id, JobType = JobType, RunName = runName, ConfigPath = path, RequestJson = request.Serialize(),
                SnapshotJson = JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions), Status = "queued", CreatedAt = cutoff
            };
        }
        catch
        {
            if (reference is not null) await PreparedRequestStagingCleanup.DiscardAsync(reference, preparedPath, repository, id);
            throw;
        }
    }

    public async Task<BacktestCancellationOutcome> CancelJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var stored = await repository.GetJobAsync(jobId, cancellationToken);
        if (stored is null || !stored.JobType.Equals(JobType, StringComparison.OrdinalIgnoreCase))
        {
            return BacktestCancellationOutcome.NotFound;
        }

        if (IsTerminalStatus(stored.Status))
        {
            return BacktestCancellationOutcome.AlreadyFinished;
        }

        if (!await repository.RequestCancellationAsync(jobId, DateTimeOffset.UtcNow, cancellationToken))
        {
            return BacktestCancellationOutcome.AlreadyFinished;
        }

        var refreshed = await repository.GetJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Backtest job {jobId:N} disappeared after cancellation.");
        var job = jobs.GetOrAdd(jobId, _ => MutableBacktestJob.FromPersisted(refreshed, JsonOptions));
        job.ApplyPersistedState(refreshed);
        job.Report("cancelling", "Cancellation requested. Waiting for the worker to drain.", null, 0, 0, null);
        return BacktestCancellationOutcome.Accepted;
    }

    public IReadOnlyList<BacktestJobSnapshot> List()
    {
        PruneTerminalJobs();
        return jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => job.ToSnapshot())
            .ToArray();
    }

    public BacktestJobSnapshot? Get(Guid jobId) =>
        jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;

    /// <summary>Reads queued or recovered work directly, including cohort jobs not yet loaded by a worker.</summary>
    public async Task<BacktestJobSnapshot?> GetSnapshotAsync(Guid jobId, CancellationToken token = default)
    {
        var persisted = await repository.GetJobAsync(jobId, token);
        return persisted?.JobType == JobType ? MutableBacktestJob.FromPersisted(persisted, JsonOptions).ToSnapshot() : null;
    }

    /// <summary>Lists recent persisted executions even when all worker slots are occupied.</summary>
    public async Task<IReadOnlyList<BacktestJobSnapshot>> RecentSnapshotsAsync(int count, CancellationToken token = default) =>
        (await repository.GetJobsAsync(JobType, token)).OrderByDescending(job => job.CreatedAt).Take(count)
            .Select(job => MutableBacktestJob.FromPersisted(job, JsonOptions).ToSnapshot()).ToArray();

    /// <summary>Reads all persisted backtest job snapshots belonging to one saved plan, newest first, without loading unrelated job snapshots.</summary>
    public async Task<IReadOnlyList<BacktestJobSnapshot>> GetSnapshotsForPlanAsync(long planId, CancellationToken token = default)
    {
        if (planId <= 0) return [];
        var persisted = await repository.GetJobsForPlanAsync(JobType, planId, token);
        return persisted
            .Where(job => TryReadPlanId(job.RequestJson) == planId)
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => MutableBacktestJob.FromPersisted(job, JsonOptions).ToSnapshot())
            .ToArray();
    }

    // Extracts workflow.cohort.planId via lightweight JsonDocument without allocating file evidence lists or crashing on malformed legacy payloads.
    internal static long? TryReadPlanId(string? requestJson)
    {
        if (String.IsNullOrWhiteSpace(requestJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetJsonProperty(root, "workflow", out var workflow) || workflow.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetJsonProperty(workflow, "cohort", out var cohort) || cohort.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetJsonProperty(cohort, "planId", out var planIdElement) || planIdElement.ValueKind != JsonValueKind.Number)
                return null;
            return planIdElement.TryGetInt64(out var planId) ? planId : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // Case-insensitive property lookup so camelCase and PascalCase durable request payloads resolve identically.
    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Execute frozen inputs under this lease and publish monthly evidence before trimming the persisted UI snapshot.</summary>
    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var cutoff = await research.VerifyWorkerAsync(context.Job, cancellationToken);
        using var protectedObservations = ResearchObservationBoundary.Enter(cutoff);
        var request = DurableFileJobRequest.Deserialize(context.Job.RequestJson);
        using var inputLease = request.AcquireVerifiedReadLease();
        var capturedRun = yamlReader.ReadBacktestRun(context.Job.ConfigPath);
        RequireEnabledStrategies(capturedRun);
        using var evidenceScope = WorkflowEvidenceVerificationScope.Open(this, capturedRun.Engine);
        var job = jobs.GetOrAdd(
            context.Job.Id,
            _ => MutableBacktestJob.FromPersisted(context.Job, JsonOptions));
        job.MarkRunning(context.Job.StartedAt ?? DateTimeOffset.UtcNow);
        job.Report("starting", "Backtest durable worker started.", null, 0, 0, null);
        await context.SaveSnapshotAsync(JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions), cancellationToken);

        var progress = new InlineProgress<BacktestProgress>(update =>
        {
            job.SetPreparedMarketData(update.PreparedMarketData);
            job.Report(
                update.Stage,
                update.Message,
                update.Ticker,
                update.CompletedTickerCount,
                update.TotalTickerCount,
                update.StrategyName);
        });
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        request.EnsureContains(ResolveInputs(job.ConfigPath, runConfig, evidenceScope));
        var attemptResultPath = Path.Combine(
            runConfig.ResultsRoot,
            ".durable-attempts",
            JobType,
            job.JobId.ToString("N"),
            context.LeaseToken.ToString("N"),
            "result.json");
        var runTask = evidenceScope is not null ? runner.RunAsync(job.ConfigPath, attemptResultPath, request.CapturedAtUtc,
            cancellationToken, evidenceScope.Resolve(runConfig.Engine), evidenceScope.Guard, progress, request.PreparedMarketData)
            : runner.RunAsync(
            job.ConfigPath,
            attemptResultPath,
            request.CapturedAtUtc,
            cancellationToken,
            progress,
            request.PreparedMarketData);
        await PersistWhileRunningAsync(runTask, job, context, cancellationToken);
        var result = await runTask;
        cancellationToken.ThrowIfCancellationRequested();

        var monthly = await monthlyResults.WriteAsync(runConfig, result, request.CapturedAtUtc,
            request.Workflow?.Cohort?.CohortName ?? job.RunName, request.Workflow?.Provenance.Strategies,
            cancellationToken, request.Workflow?.Cohort);
        var projected = ProjectResultForUi(result);
        var completedAt = DateTimeOffset.UtcNow;
        var completedSnapshot = job.ToTerminalSnapshot("completed", completedAt, null, projected, monthly);
        return DurableJobCompletion.Completed(
            JsonSerializer.Serialize(completedSnapshot, JsonOptions),
            result.ResultPath);
    }

    // Applies current catalog availability to submissions and worker starts before market-data preparation.
    private void RequireEnabledStrategies(BacktestRunConfig run)
    {
        foreach (var path in run.Strategies)
            strategyValidator.RequireEnabledForExecution(path);
    }

    /// <summary>Retains all ordinary captured-input checks while reusing only this attempt's exact diagnostic graph.</summary>
    private static IReadOnlyList<string> ResolveInputs(string path, BacktestRunConfig run, WorkflowEvidenceVerificationScope? scope) =>
        scope is null ? BacktestRunInputFiles.Resolve(path, run)
        : BacktestRunInputFiles.Resolve(path, run, scope.Resolve(run.Engine), scope.Guard);

    public Task OnTerminalPublishedAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        return ApplyTerminalPublicationAsync(persisted, cancellationToken);
    }

    private async Task ApplyTerminalPublicationAsync(
        PersistedJob persisted,
        CancellationToken cancellationToken)
    {
        await markers.PublishAsync(persisted, cancellationToken);
        await RecordResearchResultAsync(persisted, cancellationToken);
        var job = jobs.GetOrAdd(
            persisted.Id,
            _ => MutableBacktestJob.FromPersisted(persisted, JsonOptions));
        job.ApplyPersistedState(persisted);
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
            job.Report("cancelled", "Backtest cancelled after execution drained.", null, 0, 0, null);
        }

        CompactManagedHeap();
        PruneTerminalJobs();
    }

    /// <summary>Retries cross-store outcome projection on refresh while retaining the durable queue result.</summary>
    private async Task RecordResearchResultAsync(PersistedJob persisted, CancellationToken token)
    {
        try
        {
            await research.RecordResultAsync(persisted, token, async ct =>
                markers.Locate(persisted.ResultReference) is { } reference ? await BacktestResearchPublication.VerifyAsync(reference, ct) : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Backtest {JobId} outcome could not be recorded in the research ledger.", persisted.Id);
        }
    }

    /// <summary>Persists progress until execution ends; cancellation waits for the runner to drain before releasing its lease and input files.</summary>
    private async Task PersistWhileRunningAsync(
        Task runTask,
        MutableBacktestJob job,
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!runTask.IsCompleted)
            {
                var delay = Task.Delay(options.SnapshotInterval, cancellationToken);
                if (await Task.WhenAny(runTask, delay) == runTask)
                {
                    break;
                }

                await context.SaveSnapshotAsync(
                    JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions),
                    cancellationToken);
            }
        }
        catch
        {
            // Snapshot failure must drain children as well, so no evidence lease is released under a running workspace.
            try { await runTask; }
            catch { /* Preserve the original snapshot/cancellation failure after execution drains. */ }
            throw;
        }
    }

    // Keep window-wide benchmark comparisons in the immutable audit result, with monthly values in the job view.
    private BacktestResult ProjectResultForUi(BacktestResult result) => result with
    {
        Validation = result.Validation with
        {
            Benchmark = result.Validation.Benchmark with { BenchmarkReturnPct = null, StrategyComparisons = [] }
        },
        CompletedTrades = result.CompletedTrades.TakeLast(options.RetainedRecentTrades).ToArray(),
        StrategyResults = result.StrategyResults
            .Select(strategy => strategy with { CompletedTrades = Array.Empty<BacktestTrade>() })
            .ToArray(),
        MissedMoves = result.MissedMoves.TakeLast(options.RetainedMissedMoves).ToArray(),
        AcceptedOrders = Array.Empty<TradingFlow.Domain.Orders.FinalizedOrder>(),
        CandidateDecisionAudit = Array.Empty<BacktestCandidateDecisionAudit>(),
        ExecutionAudit = Array.Empty<BacktestExecutionAuditEvent>()
    };

    private void PruneTerminalJobs()
    {
        foreach (var job in jobs.Values
                     .Where(job => job.IsTerminal)
                     .OrderByDescending(job => job.FinishedAt ?? job.CreatedAt)
                     .Skip(options.RetainedTerminalJobs))
        {
            jobs.TryRemove(job.JobId, out _);
        }
    }

    private static void CompactManagedHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static bool IsTerminalStatus(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";

    private static BacktestJobServiceOptions ValidateOptions(BacktestJobServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrentJobs <= 0 || options.MaximumPendingJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Backtest worker and queue limits must be positive.");
        }

        if (options.RetainedTerminalJobs < 0 || options.RetainedRecentTrades < 0 || options.RetainedMissedMoves < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Backtest retention limits cannot be negative.");
        }

        if (options.SnapshotInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SnapshotInterval must be positive.");
        }

        return options;
    }

    private sealed class MutableBacktestJob
    {
        private readonly object stateLock = new();

        public MutableBacktestJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
        {
            JobId = jobId;
            RunName = runName;
            ConfigPath = configPath;
            CreatedAt = createdAt;
        }

        public Guid JobId { get; }
        public string RunName { get; }
        public string ConfigPath { get; }
        public string Status { get; private set; } = "queued";
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public DateTimeOffset? CancellationRequestedAt { get; private set; }
        public string? ErrorMessage { get; private set; }
        public string CurrentStage { get; private set; } = "queued";
        public int CompletedTickerCount { get; private set; }
        public int TotalTickerCount { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentDictionary<string, MutableStrategyRunGroup> StrategyGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public BacktestResult? Result { get; private set; }
        public PreparedMarketDataSummary? PreparedMarketData { get; private set; }
        public IReadOnlyList<MonthlyRunResult> MonthlyResults { get; private set; } = [];
        public string? MonthlyUnavailableReason { get; private set; }
        public BacktestCohortExecution? Cohort { get; private set; }
        public string? ArtifactDirectory { get; private set; }
        public bool IsTerminal => IsTerminalStatus(Status);

        /// <summary>Restores the persisted progress/result evidence, then applies the repository's authoritative job status.</summary>
        public static MutableBacktestJob FromPersisted(PersistedJob persisted, JsonSerializerOptions jsonOptions)
        {
            var job = new MutableBacktestJob(persisted.Id, persisted.RunName, persisted.ConfigPath, persisted.CreatedAt);
            if (!String.IsNullOrWhiteSpace(persisted.SnapshotJson))
            {
                var snapshot = JsonSerializer.Deserialize<BacktestJobSnapshot>(persisted.SnapshotJson, jsonOptions);
                if (snapshot is not null)
                {
                    job.ApplySnapshot(snapshot);
                }
            }

            job.ApplyPersistedState(persisted);
            return job;
        }

        public void MarkRunning(DateTimeOffset startedAt)
        {
            lock (stateLock)
            {
                Status = "running";
                StartedAt ??= startedAt;
            }
        }

        /// <summary>Restores durable status and the exact saved cohort so Web snapshots retain original capital and plan identity.</summary>
        public void ApplyPersistedState(PersistedJob persisted)
        {
            lock (stateLock)
            {
                Status = persisted.Status;
                StartedAt = persisted.StartedAt;
                FinishedAt = persisted.FinishedAt;
                CancellationRequestedAt = persisted.CancellationRequestedAtUtc;
                ErrorMessage = persisted.ErrorMessage;
                Cohort = DurableFileJobRequest.Deserialize(persisted.RequestJson).Workflow?.Cohort;
            }
        }

        /// <summary>Restore both compact monthly evidence and the bounded result when a durable projection is refreshed.</summary>
        public void ApplySnapshot(BacktestJobSnapshot snapshot)
        {
            lock (stateLock)
            {
                CurrentStage = snapshot.CurrentStage;
                CompletedTickerCount = snapshot.CompletedTickerCount;
                TotalTickerCount = snapshot.TotalTickerCount;
                Result = snapshot.Result;
                PreparedMarketData = snapshot.PreparedMarketData ?? snapshot.Result?.PreparedMarketData;
                MonthlyResults = snapshot.MonthlyResults ?? [];
                MonthlyUnavailableReason = snapshot.MonthlyUnavailableReason;
                ArtifactDirectory = snapshot.ArtifactDirectory;
                while (Events.TryDequeue(out _)) { }
                foreach (var entry in snapshot.Events.TakeLast(80))
                {
                    Events.Enqueue(entry);
                }
            }
        }

        public void Report(
            string stage,
            string message,
            string? ticker,
            int completedTickerCount,
            int totalTickerCount,
            string? strategyName)
        {
            lock (stateLock)
            {
                CurrentStage = stage;
                if (totalTickerCount > 0)
                {
                    CompletedTickerCount = completedTickerCount;
                    TotalTickerCount = totalTickerCount;
                }

                var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
                var strategyText = String.IsNullOrWhiteSpace(strategyName) ? String.Empty : $" [{strategyName}]";
                EnqueueEvent($"{stage}{strategyText}{tickerText}", message);
            }

            if (!String.IsNullOrWhiteSpace(strategyName))
            {
                StrategyGroups.GetOrAdd(strategyName, name => new MutableStrategyRunGroup(name))
                    .Report(stage, ticker, message, totalTickerCount);
            }
        }

        /// <summary>Retains committed public input evidence as progress advances beyond the preparation stage.</summary>
        public void SetPreparedMarketData(PreparedMarketDataSummary? summary)
        {
            if (summary is null) return;
            lock (stateLock) PreparedMarketData = summary;
        }

        public BacktestJobSnapshot ToSnapshot() => ToTerminalSnapshot(Status, FinishedAt, ErrorMessage, Result);

        /// <summary>Capture one consistent terminal view, retaining monthly evidence even after full trades are trimmed.</summary>
        public BacktestJobSnapshot ToTerminalSnapshot(
            string status,
            DateTimeOffset? finishedAt,
            string? error,
            BacktestResult? result,
            MonthlyRunResultReport? monthly = null)
        {
            lock (stateLock)
            {
                return new BacktestJobSnapshot(
                    JobId,
                    RunName,
                    ConfigPath,
                    status,
                    CreatedAt,
                    StartedAt,
                    finishedAt,
                    error,
                    CurrentStage,
                    CompletedTickerCount,
                    TotalTickerCount,
                    Events.ToArray(),
                    result,
                    StrategyGroups.Values
                        .OrderBy(group => group.StrategyName, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.ToSnapshot())
                        .ToArray(),
                    CancellationRequestedAt,
                    ArtifactDirectory: result is null ? ArtifactDirectory : Path.GetDirectoryName(result.ResultPath),
                    MonthlyResults: monthly?.Results ?? MonthlyResults,
                    MonthlyUnavailableReason: monthly?.UnavailableReason ?? MonthlyUnavailableReason,
                    Cohort: monthly?.Cohort ?? Cohort,
                    PreparedMarketData: result?.PreparedMarketData ?? PreparedMarketData);
            }
        }

        private void EnqueueEvent(string stage, string message)
        {
            Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _))
            {
            }
        }
    }

    private sealed class MutableStrategyRunGroup
    {
        private readonly object stateLock = new();
        private readonly ConcurrentDictionary<string, byte> completedTickers = new(StringComparer.OrdinalIgnoreCase);

        public MutableStrategyRunGroup(string strategyName) => StrategyName = strategyName;

        public string StrategyName { get; }
        public string Status { get; private set; } = "queued";
        public int TotalTickerCount { get; private set; }
        public string? CurrentTicker { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();

        public void Report(string stage, string? ticker, string message, int totalTickerCount)
        {
            lock (stateLock)
            {
                if (stage.Equals("strategy_group_ready", StringComparison.OrdinalIgnoreCase))
                {
                    TotalTickerCount = totalTickerCount;
                    Status = "queued";
                }
                else if (stage.Equals("running_strategy_ticker", StringComparison.OrdinalIgnoreCase))
                {
                    Status = "running";
                    CurrentTicker = ticker;
                    if (TotalTickerCount <= 0 && totalTickerCount > 0)
                    {
                        TotalTickerCount = totalTickerCount;
                    }
                }
                else if (stage.Equals("finished_strategy_ticker", StringComparison.OrdinalIgnoreCase))
                {
                    if (!String.IsNullOrWhiteSpace(ticker))
                    {
                        completedTickers.TryAdd(ticker, 0);
                    }

                    Status = TotalTickerCount > 0 && completedTickers.Count >= TotalTickerCount
                        ? "completed"
                        : "running";
                }

                var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
                Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)}{tickerText}: {message}");
                while (Events.Count > 12 && Events.TryDequeue(out _))
                {
                }
            }
        }

        public BacktestStrategyRunGroup ToSnapshot()
        {
            lock (stateLock)
            {
                return new BacktestStrategyRunGroup(
                    StrategyName,
                    Status,
                    completedTickers.Count,
                    TotalTickerCount,
                    CurrentTicker,
                    Events.ToArray());
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
