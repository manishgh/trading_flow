using System.Collections.Concurrent;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Domain.Optimization;
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

    public bool Cancel(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Cancel();
        return true;
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
            var optimizationProgress = new Progress<OptimizationProgress>(job.ReportOptimization);
            job.Result = await optimizer.OptimizeAsync(job.ConfigPath, job.CancellationToken, progress, optimizationProgress);
            job.Status = job.CancellationToken.IsCancellationRequested ? "cancelled" : "completed";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.Report("cancelled", "Optimization run cancelled.");
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
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken CancellationToken => Cancellation.Token;
        public OptimizationRunSnapshot? CurrentRun { get; private set; }
        public ConcurrentQueue<OptimizationRunSnapshot> CompletedRuns { get; } = new();

        public void Cancel()
        {
            if (Status is "queued" or "running")
            {
                Report("cancelling", "Cancellation requested.");
                Cancellation.Cancel();
                Status = "cancelling";
            }
        }

        public void Report(string stage, string message)
        {
            CurrentStage = stage;
            Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _))
            {
            }
        }

        public void ReportOptimization(OptimizationProgress update)
        {
            if (update.Kind.Equals("started", StringComparison.OrdinalIgnoreCase))
            {
                CurrentRun = new OptimizationRunSnapshot(
                    update.CurrentPermutation,
                    update.TotalPermutations,
                    update.StrategyName,
                    update.ParameterValues,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "running");
                Report("optimization_run", update.Message);
                return;
            }

            if (update.Kind.Equals("completed", StringComparison.OrdinalIgnoreCase) && update.CompletedRun is not null)
            {
                var completed = ToSnapshot(
                    update.CurrentPermutation,
                    update.TotalPermutations,
                    update.StrategyName,
                    update.CompletedRun,
                    "completed");
                CompletedRuns.Enqueue(completed);
                CurrentRun = null;
                while (CompletedRuns.Count > 200 && CompletedRuns.TryDequeue(out _))
                {
                }

                Report("optimization_result", update.Message);
            }
        }

        private static OptimizationRunSnapshot ToSnapshot(
            int permutation,
            int totalPermutations,
            string strategyName,
            OptimizationRun run,
            string status)
        {
            return new OptimizationRunSnapshot(
                permutation,
                totalPermutations,
                strategyName,
                run.ParameterValues,
                run.MetricValue,
                run.TotalReturnPct,
                run.AverageDailyReturnPct,
                run.NetProfit,
                run.MaxDrawdownPct,
                run.WinningTradeCount,
                run.LosingTradeCount,
                status);
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
                CurrentRun,
                CompletedRuns.ToArray().Reverse().ToArray(),
                Result);
        }
    }
}
