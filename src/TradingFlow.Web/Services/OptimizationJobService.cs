using System.Collections.Concurrent;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class OptimizationJobService
{
    private readonly ConcurrentDictionary<Guid, MutableOptimizationJob> jobs = new();
    private readonly SimpleYamlReader yamlReader;
    private readonly BacktestRunner backtestRunner;

    public OptimizationJobService(SimpleYamlReader yamlReader, BacktestRunner backtestRunner)
    {
        this.yamlReader = yamlReader;
        this.backtestRunner = backtestRunner;
    }

    public OptimizationJobSnapshot Start(string runName, string configPath)
    {
        var job = new MutableOptimizationJob(Guid.NewGuid(), runName, configPath, DateTimeOffset.UtcNow);
        jobs[job.JobId] = job;
        _ = Task.Run(() => RunAsync(job));
        return job.ToSnapshot();
    }

    public IReadOnlyList<OptimizationJobSnapshot> List()
    {
        return jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => job.ToSnapshot())
            .ToArray();
    }

    public OptimizationJobSnapshot? Get(Guid jobId)
    {
        return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
    }

    private async Task RunAsync(MutableOptimizationJob job)
    {
        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Report("starting", "Optimization worker started.");
        try
        {
            var optimizer = new StrategyOptimizer(yamlReader, backtestRunner);
            var progress = new Progress<BacktestProgress>(update =>
                job.Report(update.Stage, update.Message));
            job.Result = await optimizer.OptimizeAsync(job.ConfigPath, CancellationToken.None, progress);
            job.Status = "completed";
        }
        catch (Exception exception)
        {
            job.ErrorMessage = exception.Message;
            job.Status = "failed";
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    private sealed class MutableOptimizationJob
    {
        public MutableOptimizationJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
        {
            JobId = jobId;
            RunName = runName;
            ConfigPath = configPath;
            CreatedAt = createdAt;
            Status = "queued";
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
        public ConcurrentQueue<string> Events { get; } = new();
        public TradingFlow.Domain.Optimization.OptimizationResult? Result { get; set; }

        public void Report(string stage, string message)
        {
            CurrentStage = stage;
            Events.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {stage}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _))
            {
            }
        }

        public OptimizationJobSnapshot ToSnapshot()
        {
            return new OptimizationJobSnapshot(
                JobId,
                RunName,
                ConfigPath,
                Status,
                CreatedAt,
                StartedAt,
                FinishedAt,
                ErrorMessage,
                CurrentStage,
                Events.ToArray(),
                Result);
        }
    }
}
