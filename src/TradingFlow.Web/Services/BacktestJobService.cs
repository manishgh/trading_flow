using System.Collections.Concurrent;
using System.Runtime;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
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
        IProgress<BacktestProgress>? progress = null);
}

public sealed class BacktestRunExecutor(BacktestRunner runner) : IBacktestRunExecutor
{
    public Task<BacktestResult> RunAsync(
        string configPath,
        string resultPath,
        DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null) =>
        runner.RunAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, progress);
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

    public BacktestJobService(
        IBacktestRunExecutor runner,
        IDurableJobRepository repository,
        SimpleYamlReader yamlReader,
        BacktestJobServiceOptions options,
        ILogger<BacktestJobService>? logger = null)
    {
        this.runner = runner;
        this.repository = repository;
        this.yamlReader = yamlReader;
        this.options = ValidateOptions(options);
        this.logger = logger ?? NullLogger<BacktestJobService>.Instance;
    }

    public string JobType => "backtest";
    public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var persisted in await repository.GetJobsAsync(JobType, cancellationToken))
        {
            await PublishResultMarkerAsync(persisted, cancellationToken);
            jobs[persisted.Id] = MutableBacktestJob.FromPersisted(persisted, JsonOptions);
        }

        PruneTerminalJobs();
    }

    public Task RefreshProjectionAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken);

    public async Task<BacktestJobSnapshot> StartAsync(
        string runName,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullConfigPath = Path.GetFullPath(configPath);
        var runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
        var request = DurableFileJobRequest.Capture([fullConfigPath, .. runConfig.Strategies]);
        using (request.AcquireVerifiedReadLease())
        {
            runConfig = yamlReader.ReadBacktestRun(fullConfigPath);
            request.EnsureContains([fullConfigPath, .. runConfig.Strategies]);
        }
        var job = new MutableBacktestJob(Guid.NewGuid(), runName, fullConfigPath, DateTimeOffset.UtcNow);
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
                $"The backtest queue already contains its maximum of {options.MaximumPendingJobs} non-terminal jobs.");
        }

        var projectedJob = jobs.GetOrAdd(job.JobId, job);

        PruneTerminalJobs();
        return projectedJob.ToSnapshot();
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

    public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = DurableFileJobRequest.Deserialize(context.Job.RequestJson);
        using var inputLease = request.AcquireVerifiedReadLease();
        var job = jobs.GetOrAdd(
            context.Job.Id,
            _ => MutableBacktestJob.FromPersisted(context.Job, JsonOptions));
        job.MarkRunning(context.Job.StartedAt ?? DateTimeOffset.UtcNow);
        job.Report("starting", "Backtest durable worker started.", null, 0, 0, null);
        await context.SaveSnapshotAsync(JsonSerializer.Serialize(job.ToSnapshot(), JsonOptions), cancellationToken);

        var progress = new InlineProgress<BacktestProgress>(update =>
            job.Report(
                update.Stage,
                update.Message,
                update.Ticker,
                update.CompletedTickerCount,
                update.TotalTickerCount,
                update.StrategyName));
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        request.EnsureContains([job.ConfigPath, .. runConfig.Strategies]);
        var attemptResultPath = Path.Combine(
            runConfig.ResultsRoot,
            ".durable-attempts",
            JobType,
            job.JobId.ToString("N"),
            context.LeaseToken.ToString("N"),
            "result.json");
        var runTask = runner.RunAsync(
            job.ConfigPath,
            attemptResultPath,
            request.CapturedAtUtc,
            cancellationToken,
            progress);
        await PersistWhileRunningAsync(runTask, job, context, cancellationToken);
        var result = await runTask;
        cancellationToken.ThrowIfCancellationRequested();

        var projected = ProjectResultForUi(result);
        var completedAt = DateTimeOffset.UtcNow;
        var completedSnapshot = job.ToTerminalSnapshot("completed", completedAt, null, projected);
        return DurableJobCompletion.Completed(
            JsonSerializer.Serialize(completedSnapshot, JsonOptions),
            result.ResultPath);
    }

    public Task OnTerminalPublishedAsync(PersistedJob persisted, CancellationToken cancellationToken)
    {
        return ApplyTerminalPublicationAsync(persisted, cancellationToken);
    }

    private async Task ApplyTerminalPublicationAsync(
        PersistedJob persisted,
        CancellationToken cancellationToken)
    {
        await PublishResultMarkerAsync(persisted, cancellationToken);
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
                "Durable backtest {JobId} result marker is not available yet; projection remains visible and the next refresh will retry.",
                persisted.Id);
        }
    }

    private async Task PersistWhileRunningAsync(
        Task runTask,
        MutableBacktestJob job,
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
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

    private BacktestResult ProjectResultForUi(BacktestResult result) => result with
    {
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
        public bool IsTerminal => IsTerminalStatus(Status);

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

        public void ApplyPersistedState(PersistedJob persisted)
        {
            lock (stateLock)
            {
                Status = persisted.Status;
                StartedAt = persisted.StartedAt;
                FinishedAt = persisted.FinishedAt;
                CancellationRequestedAt = persisted.CancellationRequestedAtUtc;
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
                Result = snapshot.Result;
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

        public BacktestJobSnapshot ToSnapshot() => ToTerminalSnapshot(Status, FinishedAt, ErrorMessage, Result);

        public BacktestJobSnapshot ToTerminalSnapshot(
            string status,
            DateTimeOffset? finishedAt,
            string? error,
            BacktestResult? result)
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
                    CancellationRequestedAt);
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
