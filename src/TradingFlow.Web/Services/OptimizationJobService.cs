using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Optimization;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed record OptimizationJobServiceOptions(
    int MaximumConcurrentJobs,
    int MaximumPendingJobs,
    int RetainedTerminalJobs,
    TimeSpan SnapshotInterval)
{
    public static OptimizationJobServiceOptions Default { get; } = new(1, 8, 20, TimeSpan.FromSeconds(1));

    public OptimizationJobServiceOptions Validate()
    {
        if (MaximumConcurrentJobs <= 0 || MaximumPendingJobs <= 0 || SnapshotInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentJobs));
        }

        if (RetainedTerminalJobs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetainedTerminalJobs));
        }

        return this;
    }
}

public sealed class OptimizationJobService : IDurableJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, MutableOptimizationJob> jobs = new();
    private readonly SimpleYamlReader yamlReader;
    private readonly BacktestRunner backtestRunner;
    private readonly IDurableJobRepository repository;
    private readonly OptimizationJobServiceOptions options;
    private readonly ILogger<OptimizationJobService> logger;

    public OptimizationJobService(
        SimpleYamlReader yamlReader,
        BacktestRunner backtestRunner,
        IDurableJobRepository repository,
        OptimizationJobServiceOptions options,
        ILogger<OptimizationJobService>? logger = null)
    {
        this.yamlReader = yamlReader;
        this.backtestRunner = backtestRunner;
        this.repository = repository;
        this.options = options.Validate();
        this.logger = logger ?? NullLogger<OptimizationJobService>.Instance;
    }

    public string JobType => "optimization";
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var persisted in await repository.GetJobsAsync(JobType, cancellationToken))
        {
            await PublishResultMarkerAsync(persisted, cancellationToken);
            jobs[persisted.Id] = MutableOptimizationJob.FromPersisted(persisted, JsonOptions);
        }

        PruneTerminalJobs();
    }

    public Task RefreshProjectionAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken);

    public async Task<OptimizationJobSnapshot> StartAsync(
        string runName,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        var optimization = yamlReader.ReadOptimizationConfig(fullConfigPath);
        var backtest = yamlReader.ReadBacktestRun(optimization.BacktestConfigPath);
        var request = DurableFileJobRequest.Capture([
            fullConfigPath,
            optimization.BaseStrategyPath,
            optimization.BacktestConfigPath,
            .. backtest.Strategies
        ]);
        using (request.AcquireVerifiedReadLease())
        {
            optimization = yamlReader.ReadOptimizationConfig(fullConfigPath);
            backtest = yamlReader.ReadBacktestRun(optimization.BacktestConfigPath);
            request.EnsureContains([
                fullConfigPath,
                optimization.BaseStrategyPath,
                optimization.BacktestConfigPath,
                .. backtest.Strategies
            ]);
        }
        var job = new MutableOptimizationJob(Guid.NewGuid(), runName, fullConfigPath, DateTimeOffset.UtcNow);
        var persisted = new PersistedJob
        {
            Id = job.JobId,
            JobType = JobType,
            RunName = job.RunName,
            ConfigPath = job.ConfigPath,
            RequestJson = request.Serialize(),
            SnapshotJson = JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions),
            Status = "queued",
            CreatedAt = job.CreatedAt
        };
        if (!await repository.TryEnqueueAsync(persisted, options.MaximumPendingJobs, cancellationToken))
        {
            throw new InvalidOperationException(
                $"The optimization queue already contains its maximum of {options.MaximumPendingJobs} non-terminal jobs.");
        }

        jobs[job.JobId] = job;
        return job.ToSnapshot();
    }

    public IReadOnlyList<OptimizationJobSnapshot> List() => jobs.Values
        .OrderByDescending(job => job.CreatedAt)
        .Select(job => job.ToSnapshot())
        .ToArray();

    public OptimizationJobSnapshot? Get(Guid jobId) =>
        jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var stored = await repository.GetJobAsync(jobId, cancellationToken);
        if (stored is null || !stored.JobType.Equals(JobType, StringComparison.OrdinalIgnoreCase) || IsTerminal(stored.Status))
        {
            return false;
        }

        var accepted = await repository.RequestCancellationAsync(jobId, DateTimeOffset.UtcNow, cancellationToken);
        if (accepted)
        {
            var refreshed = await repository.GetJobAsync(jobId, cancellationToken);
            if (refreshed is not null)
            {
                jobs.GetOrAdd(jobId, _ => MutableOptimizationJob.FromPersisted(refreshed, JsonOptions))
                    .ApplyPersistedState(refreshed);
            }
        }

        return accepted;
    }

    public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DurableFileJobRequest.Deserialize(context.Job.RequestJson);
        using var inputLease = request.AcquireVerifiedReadLease();
        var job = jobs.GetOrAdd(
            context.Job.Id,
            _ => MutableOptimizationJob.FromPersisted(context.Job, JsonOptions));
        job.MarkRunning(context.Job.StartedAt ?? DateTimeOffset.UtcNow);
        job.Report("starting", "Optimization durable worker started.");
        await context.SaveSnapshotAsync(JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions), cancellationToken);

        var optimizer = new StrategyOptimizer(yamlReader, backtestRunner);
        var progress = new InlineProgress<BacktestProgress>(update => job.Report(update.Stage, update.Message));
        var optimizationProgress = new InlineProgress<OptimizationProgress>(job.ReportOptimization);
        var config = yamlReader.ReadOptimizationConfig(job.ConfigPath);
        var backtestConfig = yamlReader.ReadBacktestRun(config.BacktestConfigPath);
        request.EnsureContains([
            job.ConfigPath,
            config.BaseStrategyPath,
            config.BacktestConfigPath,
            .. backtestConfig.Strategies
        ]);
        var attemptRoot = Path.Combine(
            backtestConfig.ResultsRoot,
            ".durable-attempts",
            JobType,
            job.JobId.ToString("N"),
            context.LeaseToken.ToString("N"));
        var runTask = optimizer.OptimizeAsync(
            job.ConfigPath,
            cancellationToken,
            progress,
            optimizationProgress,
            attemptRoot,
            request.CapturedAtUtc);
        while (!runTask.IsCompleted)
        {
            var delay = Task.Delay(options.SnapshotInterval, cancellationToken);
            if (await Task.WhenAny(runTask, delay) == runTask)
            {
                break;
            }

            await context.SaveSnapshotAsync(JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions), cancellationToken);
        }

        var result = await runTask;
        cancellationToken.ThrowIfCancellationRequested();
        var resultPath = Path.Combine(attemptRoot, "summary.json");
        var completed = job.ToTerminalSnapshot("completed", DateTimeOffset.UtcNow, null, result);
        return DurableJobCompletion.Completed(JsonSerializer.Serialize(completed, JsonOptions), resultPath);
    }

    public async Task OnTerminalPublishedAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        await PublishResultMarkerAsync(persisted, cancellationToken);
        var job = jobs.GetOrAdd(
            persisted.Id,
            _ => MutableOptimizationJob.FromPersisted(persisted, JsonOptions));
        if (!String.IsNullOrWhiteSpace(persisted.SnapshotJson))
        {
            var snapshot = JsonSerializer.Deserialize<OptimizationJobSnapshot>(persisted.SnapshotJson, JsonOptions);
            if (snapshot is not null)
            {
                job.ApplySnapshot(snapshot);
            }
        }

        job.ApplyPersistedState(persisted);
        PruneTerminalJobs();
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
                "Durable optimization {JobId} result marker is not available yet; projection remains visible and the next refresh will retry.",
                persisted.Id);
        }
    }

    private void PruneTerminalJobs()
    {
        foreach (var job in jobs.Values
                     .Where(job => IsTerminal(job.Status))
                     .OrderByDescending(job => job.FinishedAt ?? job.CreatedAt)
                     .Skip(options.RetainedTerminalJobs))
        {
            jobs.TryRemove(job.JobId, out _);
        }
    }

    private static bool IsTerminal(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";

    private sealed class MutableOptimizationJob
    {
        private readonly object stateLock = new();

        public MutableOptimizationJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
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
        public string? ErrorMessage { get; private set; }
        public string CurrentStage { get; private set; } = "queued";
        public ConcurrentQueue<string> Events { get; } = new();
        public OptimizationResult? Result { get; private set; }
        public OptimizationRunSnapshot? CurrentRun { get; private set; }
        public ConcurrentQueue<OptimizationRunSnapshot> CompletedRuns { get; } = new();

        public static MutableOptimizationJob FromPersisted(PersistedJob persisted, JsonSerializerOptions options)
        {
            var job = new MutableOptimizationJob(persisted.Id, persisted.RunName, persisted.ConfigPath, persisted.CreatedAt);
            if (!String.IsNullOrWhiteSpace(persisted.SnapshotJson))
            {
                var snapshot = JsonSerializer.Deserialize<OptimizationJobSnapshot>(persisted.SnapshotJson, options);
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

        public void ApplySnapshot(OptimizationJobSnapshot snapshot)
        {
            lock (stateLock)
            {
                CurrentStage = snapshot.CurrentStage;
                CurrentRun = snapshot.CurrentRun;
                Result = snapshot.Result;
                while (Events.TryDequeue(out _)) { }
                while (CompletedRuns.TryDequeue(out _)) { }
                foreach (var entry in snapshot.Events.TakeLast(80)) Events.Enqueue(entry);
                foreach (var run in snapshot.CompletedRuns.TakeLast(200)) CompletedRuns.Enqueue(run);
            }
        }

        public void Report(string stage, string message)
        {
            lock (stateLock)
            {
                CurrentStage = stage;
                Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}: {message}");
                while (Events.Count > 80 && Events.TryDequeue(out _)) { }
            }
        }

        public void ReportOptimization(OptimizationProgress update)
        {
            lock (stateLock)
            {
                if (update.Kind.Equals("started", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentRun = ToSnapshot(update.CurrentPermutation, update.TotalPermutations, update.StrategyName,
                        update.ParameterValues, null, "running");
                }
                else if (update.Kind.Equals("completed", StringComparison.OrdinalIgnoreCase) && update.CompletedRun is not null)
                {
                    CompletedRuns.Enqueue(ToSnapshot(update.CurrentPermutation, update.TotalPermutations, update.StrategyName,
                        update.CompletedRun.ParameterValues, update.CompletedRun, "completed"));
                    CurrentRun = null;
                    while (CompletedRuns.Count > 200 && CompletedRuns.TryDequeue(out _)) { }
                }
            }

            Report(update.Kind.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "optimization_result" : "optimization_run", update.Message);
        }

        public OptimizationJobSnapshot ToSnapshot() => ToTerminalSnapshot(Status, FinishedAt, ErrorMessage, Result);

        public OptimizationJobSnapshot ToTerminalSnapshot(
            string status,
            DateTimeOffset? finishedAt,
            string? error,
            OptimizationResult? result)
        {
            lock (stateLock)
            {
                return new OptimizationJobSnapshot(
                    JobId, RunName, ConfigPath, status, CreatedAt, StartedAt, finishedAt, error,
                    CurrentStage, Events.ToArray(), CurrentRun, CompletedRuns.ToArray().Reverse().ToArray(), result);
            }
        }

        private static OptimizationRunSnapshot ToSnapshot(
            int permutation,
            int totalPermutations,
            string strategyName,
            IReadOnlyDictionary<string, object> parameterValues,
            OptimizationRun? run,
            string status) => new(
                permutation,
                totalPermutations,
                strategyName,
                parameterValues,
                run?.MetricValue,
                run?.TotalReturnPct,
                run?.AverageDailyReturnPct,
                run?.NetProfit,
                run?.MaxDrawdownPct,
                run?.WinningTradeCount,
                run?.LosingTradeCount,
                status);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
