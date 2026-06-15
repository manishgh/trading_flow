using System.Collections.Concurrent;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class BacktestJobService
{
    private readonly ConcurrentDictionary<Guid, MutableBacktestJob> jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellationTokens = new();
    private readonly BacktestRunner runner;

    public BacktestJobService(BacktestRunner runner)
    {
        this.runner = runner;
    }

    public BacktestJobSnapshot Start(string runName, string configPath)
    {
        var job = new MutableBacktestJob(Guid.NewGuid(), runName, configPath, DateTimeOffset.UtcNow);
        jobs[job.JobId] = job;
        var cts = new CancellationTokenSource();
        cancellationTokens[job.JobId] = cts;
        _ = Task.Run(() => RunAsync(job, cts.Token));
        return job.ToSnapshot();
    }

    public void CancelJob(Guid jobId)
    {
        if (cancellationTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
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

    private async Task RunAsync(MutableBacktestJob job, CancellationToken cancellationToken)
    {
        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Report("starting", "Backtest worker started.", null, 0, 0, null);
        try
        {
            var progress = new Progress<BacktestProgress>(update =>
                job.Report(update.Stage, update.Message, update.Ticker, update.CompletedTickerCount, update.TotalTickerCount, update.StrategyName));
            job.Result = await runner.RunAsync(job.ConfigPath, cancellationToken, progress);
            job.Status = "completed";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.Report(job.CurrentStage, "Backtest cancelled by user.", null, job.CompletedTickerCount, job.TotalTickerCount, null);
        }
        catch (Exception exception)
        {
            job.ErrorMessage = exception.Message;
            job.Status = "failed";
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (cancellationTokens.TryRemove(job.JobId, out var cts))
            {
                cts.Dispose();
            }
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
        public ConcurrentDictionary<string, MutableStrategyRunGroup> StrategyGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TradingFlow.Domain.Backtesting.BacktestResult? Result { get; set; }

        public void Report(string stage, string message, string? ticker, int completedTickerCount, int totalTickerCount, string? strategyName)
        {
            CurrentStage = stage;
            CompletedTickerCount = completedTickerCount;
            TotalTickerCount = totalTickerCount;
            var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
            var strategyText = String.IsNullOrWhiteSpace(strategyName) ? String.Empty : $" [{strategyName}]";
            var eventLine = $"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}{strategyText}{tickerText}: {message}";
            Events.Enqueue(eventLine);
            while (Events.Count > 80 && Events.TryDequeue(out _))
            {
            }

            if (!String.IsNullOrWhiteSpace(strategyName))
            {
                var group = StrategyGroups.GetOrAdd(strategyName, name => new MutableStrategyRunGroup(name));
                group.Report(stage, ticker, message, totalTickerCount);
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
                Result,
                StrategyGroups.Values
                    .OrderBy(group => group.StrategyName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.ToSnapshot())
                    .ToArray());
        }
    }

    private sealed class MutableStrategyRunGroup
    {
        private readonly ConcurrentDictionary<string, byte> completedTickers = new(StringComparer.OrdinalIgnoreCase);

        public MutableStrategyRunGroup(string strategyName)
        {
            StrategyName = strategyName;
        }

        public string StrategyName { get; }
        public string Status { get; private set; } = "queued";
        public int TotalTickerCount { get; private set; }
        public string? CurrentTicker { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();

        public void Report(string stage, string? ticker, string message, int totalTickerCount)
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

                Status = TotalTickerCount > 0 && completedTickers.Count >= TotalTickerCount ? "completed" : "running";
            }

            var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
            Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)}{tickerText}: {message}");
            while (Events.Count > 12 && Events.TryDequeue(out _))
            {
            }
        }

        public BacktestStrategyRunGroup ToSnapshot()
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
