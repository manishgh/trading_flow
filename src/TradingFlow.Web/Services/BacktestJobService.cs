using System.Collections.Concurrent;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class BacktestJobService
{
    private readonly ConcurrentDictionary<Guid, MutableBacktestJob> jobs = new();
    private readonly SimpleYamlReader yamlReader;

    public BacktestJobService(SimpleYamlReader yamlReader)
    {
        this.yamlReader = yamlReader;
    }

    public BacktestJobSnapshot Start(string runName, string configPath)
    {
        var job = new MutableBacktestJob(Guid.NewGuid(), runName, configPath, DateTimeOffset.UtcNow);
        jobs[job.JobId] = job;
        _ = Task.Run(() => RunAsync(job));
        return job.ToSnapshot();
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

    private async Task RunAsync(MutableBacktestJob job)
    {
        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Report("starting", "Backtest worker started.", null, 0, 0);
        try
        {
            var runner = new BacktestRunner(yamlReader);
            var progress = new Progress<BacktestProgress>(update =>
                job.Report(update.Stage, update.Message, update.Ticker, update.CompletedTickerCount, update.TotalTickerCount));
            job.Result = await runner.RunAsync(job.ConfigPath, CancellationToken.None, progress);
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

    private sealed class MutableBacktestJob
    {
        public MutableBacktestJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
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
        public int CompletedTickerCount { get; private set; }
        public int TotalTickerCount { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public TradingFlow.Domain.Backtesting.BacktestResult? Result { get; set; }

        public void Report(string stage, string message, string? ticker, int completedTickerCount, int totalTickerCount)
        {
            CurrentStage = stage;
            CompletedTickerCount = completedTickerCount;
            TotalTickerCount = totalTickerCount;
            var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
            Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}{tickerText}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _))
            {
            }
        }

        public BacktestJobSnapshot ToSnapshot()
        {
            return new BacktestJobSnapshot(
                JobId,
                RunName,
                ConfigPath,
                Status,
                CreatedAt,
                StartedAt,
                FinishedAt,
                ErrorMessage,
                CurrentStage,
                CompletedTickerCount,
                TotalTickerCount,
                Events.ToArray(),
                Result);
        }
    }
}
