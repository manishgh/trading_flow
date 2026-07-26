using System.Collections.Concurrent;
using System.Runtime;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public interface IBacktestRunExecutor
{
    Task<BacktestResult> RunAsync(
        string configPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null);
}

public sealed class BacktestRunExecutor(BacktestRunner runner) : IBacktestRunExecutor
{
    public Task<BacktestResult> RunAsync(
        string configPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        return runner.RunAsync(configPath, cancellationToken, progress);
    }
}

public enum BacktestCancellationOutcome
{
    Accepted,
    NotFound,
    AlreadyFinished
}

public sealed record BacktestJobServiceOptions(
    int MaxConcurrentJobs,
    int RetainedTerminalJobs,
    int RetainedRecentTrades,
    int RetainedMissedMoves)
{
    public static BacktestJobServiceOptions Default { get; } = new(
        MaxConcurrentJobs: 1,
        RetainedTerminalJobs: 20,
        RetainedRecentTrades: 80,
        RetainedMissedMoves: 100);
}

public sealed class BacktestJobService
{
    private readonly ConcurrentDictionary<Guid, MutableBacktestJob> jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellationTokens = new();
    private readonly SemaphoreSlim executionSlots;
    private readonly IBacktestRunExecutor runner;
    private readonly BacktestJobServiceOptions options;

    public BacktestJobService(IBacktestRunExecutor runner)
        : this(runner, BacktestJobServiceOptions.Default)
    {
    }

    public BacktestJobService(IBacktestRunExecutor runner, BacktestJobServiceOptions options)
    {
        this.runner = runner;
        this.options = ValidateOptions(options);
        executionSlots = new SemaphoreSlim(this.options.MaxConcurrentJobs, this.options.MaxConcurrentJobs);
    }

    public BacktestJobSnapshot Start(string runName, string configPath)
    {
        PruneTerminalJobs();
        var job = new MutableBacktestJob(Guid.NewGuid(), runName, configPath, DateTimeOffset.UtcNow);
        jobs[job.JobId] = job;
        var cts = new CancellationTokenSource();
        cancellationTokens[job.JobId] = cts;
        _ = Task.Run(() => RunAsync(job, cts.Token));
        return job.ToSnapshot();
    }

    public BacktestCancellationOutcome CancelJob(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job))
        {
            return BacktestCancellationOutcome.NotFound;
        }

        if (!job.RequestCancellation())
        {
            return BacktestCancellationOutcome.AlreadyFinished;
        }

        if (cancellationTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }

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

    public BacktestJobSnapshot? Get(Guid jobId)
    {
        return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
    }

    private async Task RunAsync(MutableBacktestJob job, CancellationToken cancellationToken)
    {
        var slotAcquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Report("queued", "Waiting for an available backtest execution slot.", null, 0, 0, null);
            await executionSlots.WaitAsync(cancellationToken);
            slotAcquired = true;
            if (!job.TryMarkRunning())
            {
                throw new OperationCanceledException(cancellationToken);
            }

            job.Report("starting", "Backtest worker started.", null, 0, 0, null);
            var progress = new InlineProgress<BacktestProgress>(update =>
                job.Report(update.Stage, update.Message, update.Ticker, update.CompletedTickerCount, update.TotalTickerCount, update.StrategyName));
            var result = await runner.RunAsync(job.ConfigPath, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            job.MarkCompleted(ProjectResultForUi(result));
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
        }
        catch (Exception exception)
        {
            job.MarkFailed(exception.Message);
        }
        finally
        {
            job.MarkFinished();
            if (cancellationTokens.TryRemove(job.JobId, out var cts))
            {
                cts.Dispose();
            }

            PruneTerminalJobs();
            if (slotAcquired)
            {
                CompactManagedHeap();
                executionSlots.Release();
            }
        }
    }

    private static void CompactManagedHeap()
    {
        // Backtests create short-lived candle, indicator, and news arrays on the large object heap.
        // Reclaim them at the batch boundary before another queued job can pressure the web host.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private BacktestResult ProjectResultForUi(BacktestResult result)
    {
        return result with
        {
            CompletedTrades = result.CompletedTrades
                .TakeLast(options.RetainedRecentTrades)
                .ToArray(),
            StrategyResults = result.StrategyResults
                .Select(strategy => strategy with { CompletedTrades = Array.Empty<BacktestTrade>() })
                .ToArray(),
            MissedMoves = result.MissedMoves
                .TakeLast(options.RetainedMissedMoves)
                .ToArray(),
            AcceptedOrders = Array.Empty<TradingFlow.Domain.Orders.FinalizedOrder>()
        };
    }

    private void PruneTerminalJobs()
    {
        var terminalJobs = jobs.Values
            .Where(job => job.IsTerminal)
            .OrderByDescending(job => job.FinishedAt ?? job.CreatedAt)
            .Skip(options.RetainedTerminalJobs)
            .ToArray();

        foreach (var job in terminalJobs)
        {
            jobs.TryRemove(job.JobId, out _);
        }
    }

    private static BacktestJobServiceOptions ValidateOptions(BacktestJobServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrentJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentJobs must be positive.");
        }

        if (options.RetainedTerminalJobs < 0
            || options.RetainedRecentTrades < 0
            || options.RetainedMissedMoves < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Backtest retention limits cannot be negative.");
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
            Status = "queued";
        }

        public Guid JobId { get; }
        public string RunName { get; }
        public string ConfigPath { get; }
        public string Status { get; private set; }
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
        public bool IsTerminal => Status is "completed" or "failed" or "cancelled";

        public bool TryMarkRunning()
        {
            lock (stateLock)
            {
                if (Status != "queued")
                {
                    return false;
                }

                Status = "running";
                StartedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public bool RequestCancellation()
        {
            lock (stateLock)
            {
                if (Status is "completed" or "failed" or "cancelled")
                {
                    return false;
                }

                if (Status == "cancelling")
                {
                    return true;
                }

                Status = "cancelling";
                CancellationRequestedAt = DateTimeOffset.UtcNow;
                EnqueueEvent("cancelling", "Cancellation requested. Finishing the current safe cancellation point.");
                return true;
            }
        }

        public void MarkCompleted(BacktestResult result)
        {
            lock (stateLock)
            {
                Result = result;
                Status = "completed";
            }
        }

        public void MarkCancelled()
        {
            lock (stateLock)
            {
                Status = "cancelled";
                EnqueueEvent(CurrentStage, "Backtest cancelled by user.");
            }
        }

        public void MarkFailed(string errorMessage)
        {
            lock (stateLock)
            {
                ErrorMessage = errorMessage;
                Status = "failed";
                EnqueueEvent(CurrentStage, $"Backtest failed: {errorMessage}");
            }
        }

        public void MarkFinished()
        {
            lock (stateLock)
            {
                FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Report(string stage, string message, string? ticker, int completedTickerCount, int totalTickerCount, string? strategyName)
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
                var group = StrategyGroups.GetOrAdd(strategyName, name => new MutableStrategyRunGroup(name));
                group.Report(stage, ticker, message, totalTickerCount);
            }
        }

        public BacktestJobSnapshot ToSnapshot()
        {
            lock (stateLock)
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

                    Status = TotalTickerCount > 0 && completedTickers.Count >= TotalTickerCount ? "completed" : "running";
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
        public void Report(T value)
        {
            report(value);
        }
    }
}
