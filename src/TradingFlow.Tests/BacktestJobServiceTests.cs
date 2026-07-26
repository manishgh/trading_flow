using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class BacktestJobServiceTests
{
    [Fact]
    public async Task CancelJob_PropagatesCancellationAndPublishesTerminalState()
    {
        var executor = new BlockingBacktestExecutor();
        var service = new BacktestJobService(executor);

        var started = service.Start("cancel-test", "cancel-test.yaml");
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var outcome = service.CancelJob(started.JobId);
        var cancelled = await WaitForStatusAsync(service, started.JobId, "cancelled");

        Assert.Equal(BacktestCancellationOutcome.Accepted, outcome);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.NotNull(cancelled.CancellationRequestedAt);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Contains(cancelled.Events, item => item.Contains("Cancellation requested", StringComparison.Ordinal));
        Assert.Contains(cancelled.Events, item => item.Contains("Backtest cancelled by user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_IsVisibleBeforeTheRunReachesItsNextAwait()
    {
        var executor = new BlockingBacktestExecutor();
        var service = new BacktestJobService(executor);

        var started = service.Start("progress-test", "progress-test.yaml");
        await executor.ProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshot = service.Get(started.JobId);

        Assert.NotNull(snapshot);
        Assert.Equal("running_strategy_ticker", snapshot.CurrentStage);
        Assert.Equal(3, snapshot.CompletedTickerCount);
        Assert.Equal(8, snapshot.TotalTickerCount);
        Assert.Contains(snapshot.Events, item => item.Contains("Evaluating RGTI", StringComparison.Ordinal));

        service.CancelJob(started.JobId);
        await WaitForStatusAsync(service, started.JobId, "cancelled");
    }

    [Fact]
    public async Task StageOnlyProgress_DoesNotEraseCompletedWorkCounts()
    {
        var executor = new StageOnlyProgressExecutor();
        var service = new BacktestJobService(executor);

        var started = service.Start("stage-only-test", "stage-only-test.yaml");
        await executor.ProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshot = service.Get(started.JobId);

        Assert.NotNull(snapshot);
        Assert.Equal("building_results", snapshot.CurrentStage);
        Assert.Equal(8, snapshot.CompletedTickerCount);
        Assert.Equal(8, snapshot.TotalTickerCount);

        service.CancelJob(started.JobId);
        await WaitForStatusAsync(service, started.JobId, "cancelled");
    }

    [Fact]
    public void CancelJob_ReturnsNotFoundForUnknownJob()
    {
        var service = new BacktestJobService(new BlockingBacktestExecutor());

        var outcome = service.CancelJob(Guid.NewGuid());

        Assert.Equal(BacktestCancellationOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task ExecutionSlots_KeepHeavyBacktestsSequentialByDefault()
    {
        var executor = new CountingBlockingBacktestExecutor();
        var service = new BacktestJobService(executor);

        var first = service.Start("first", "first.yaml");
        var second = service.Start("second", "second.yaml");
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(1, executor.StartCount);
        var firstSnapshot = service.Get(first.JobId);
        var secondSnapshot = service.Get(second.JobId);
        var running = Assert.Single(
            new[] { firstSnapshot, secondSnapshot },
            snapshot => snapshot?.Status == "running");
        var queued = Assert.Single(
            new[] { firstSnapshot, secondSnapshot },
            snapshot => snapshot?.Status == "queued");

        service.CancelJob(running!.JobId);
        await WaitForStatusAsync(service, running.JobId, "cancelled");
        await executor.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, executor.StartCount);
        service.CancelJob(queued!.JobId);
        await WaitForStatusAsync(service, queued.JobId, "cancelled");
    }

    [Fact]
    public async Task TerminalJobRetention_PrunesOldInMemoryResults()
    {
        var options = BacktestJobServiceOptions.Default with { RetainedTerminalJobs = 2 };
        var service = new BacktestJobService(new ImmediateBacktestExecutor(), options);
        var first = service.Start("first", "first.yaml");
        await WaitForStatusAsync(service, first.JobId, "completed");
        var second = service.Start("second", "second.yaml");
        await WaitForStatusAsync(service, second.JobId, "completed");
        var third = service.Start("third", "third.yaml");
        await WaitForStatusAsync(service, third.JobId, "completed");

        var retained = service.List();

        Assert.Equal(2, retained.Count);
        Assert.Null(service.Get(first.JobId));
        Assert.NotNull(service.Get(second.JobId));
        Assert.NotNull(service.Get(third.JobId));
    }

    [Fact]
    public async Task CompletedResult_IsProjectedToBoundedUiRetention()
    {
        var options = BacktestJobServiceOptions.Default with
        {
            RetainedRecentTrades = 3,
            RetainedMissedMoves = 2
        };
        var service = new BacktestJobService(new ImmediateBacktestExecutor(5, 4), options);

        var started = service.Start("bounded", "bounded.yaml");
        var completed = await WaitForStatusAsync(service, started.JobId, "completed");

        Assert.NotNull(completed.Result);
        Assert.Equal(3, completed.Result.CompletedTrades.Count);
        Assert.Equal(2, completed.Result.MissedMoves.Count);
        Assert.All(completed.Result.StrategyResults, strategy => Assert.Empty(strategy.CompletedTrades));
        Assert.Empty(completed.Result.AcceptedOrders);
    }

    private static async Task<TradingFlow.Web.Models.BacktestJobSnapshot> WaitForStatusAsync(
        BacktestJobService service,
        Guid jobId,
        string expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = service.Get(jobId);
            if (snapshot?.Status == expectedStatus)
            {
                return snapshot;
            }

            await Task.Delay(20, timeout.Token);
        }

        throw new TimeoutException($"Backtest job {jobId} did not reach {expectedStatus}.");
    }

    private sealed class BlockingBacktestExecutor : IBacktestRunExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProgressReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BacktestResult> RunAsync(
            string configPath,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            Started.TrySetResult();
            progress?.Report(new BacktestProgress(
                "running_strategy_ticker",
                "Evaluating RGTI.",
                "RGTI",
                3,
                8,
                "Test Strategy"));
            ProgressReported.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking executor should only finish through cancellation.");
        }
    }

    private sealed class StageOnlyProgressExecutor : IBacktestRunExecutor
    {
        public TaskCompletionSource ProgressReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BacktestResult> RunAsync(
            string configPath,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            progress?.Report(new BacktestProgress("finished_strategy_ticker", "Finished final work item.", "RGTI", 8, 8, "Test Strategy"));
            progress?.Report(BacktestProgress.StageOnly("building_results", "Building results."));
            ProgressReported.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stage-only executor should only finish through cancellation.");
        }
    }

    private sealed class CountingBlockingBacktestExecutor : IBacktestRunExecutor
    {
        private int startCount;

        public int StartCount => Volatile.Read(ref startCount);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BacktestResult> RunAsync(
            string configPath,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            var current = Interlocked.Increment(ref startCount);
            if (current == 1)
            {
                FirstStarted.TrySetResult();
            }
            else if (current == 2)
            {
                SecondStarted.TrySetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The counting executor should only finish through cancellation.");
        }
    }

    private sealed class ImmediateBacktestExecutor(int completedTradeCount = 0, int missedMoveCount = 0) : IBacktestRunExecutor
    {
        public Task<BacktestResult> RunAsync(
            string configPath,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            var now = DateTimeOffset.UtcNow;
            var trades = Enumerable.Repeat<BacktestTrade>(null!, completedTradeCount).ToArray();
            var missedMoves = Enumerable.Repeat<MissedMoveAudit>(null!, missedMoveCount).ToArray();
            var strategy = new StrategyBacktestResult(
                "test",
                "Test",
                "test",
                100_000m,
                100_000m,
                0m,
                0m,
                0m,
                1,
                0m,
                0,
                0,
                0,
                0,
                0,
                trades);
            var result = new BacktestResult(
                Path.GetFileNameWithoutExtension(configPath),
                $"{configPath}.result.json",
                now,
                now,
                100_000m,
                100_000m,
                0m,
                0m,
                0m,
                1,
                0m,
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                [strategy],
                [],
                CreateValidation(),
                trades,
                [],
                missedMoves,
                []);
            return Task.FromResult(result);
        }

        private static BacktestValidationReport CreateValidation()
        {
            return new BacktestValidationReport(
                new OutOfSampleValidation(false, 0m, null, []),
                [],
                new BenchmarkValidation(false, "", null, []),
                new DataQualityValidation(0, 0, 0, 0, 0, []),
                new BiasRiskValidation("test", null, "none", false, false, []));
        }
    }
}
