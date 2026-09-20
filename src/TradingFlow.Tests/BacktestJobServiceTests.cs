using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class BacktestJobServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-backtest-service-{Guid.NewGuid():N}.db");
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-backtest-service-files-{Guid.NewGuid():N}");
    private SqliteJobRepository repository = null!;
    private string configPath = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestContextFactory(options);
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        repository = new SqliteJobRepository(factory);
        var sourceConfigsRoot = Path.Combine(TestRepository.FindRoot(), "configs");
        var copiedConfigsRoot = Path.Combine(testRoot, "configs");
        CopyDirectory(sourceConfigsRoot, copiedConfigsRoot);
        configPath = Path.Combine(copiedConfigsRoot, "backtest", "swing-backtest-profile.yaml");
        var configText = await File.ReadAllTextAsync(configPath);
        configText = configText.Replace(
            "results_root: data/backtest/results",
            $"results_root: {Path.Combine(testRoot, "results").Replace('\\', '/')}",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(configPath, configText);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PersistsQueuedJobWithoutStartingDetachedExecution()
    {
        var executor = new ImmediateBacktestExecutor();
        var service = CreateService(executor);

        var started = await service.StartAsync("queued-only", configPath);

        Assert.Equal("queued", started.Status);
        Assert.Equal(0, executor.ExecutionCount);
        Assert.Equal("queued", (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task StartAsync_SucceedsWhenConcurrentRefreshProjectsThePersistedJobFirst()
    {
        PersistedJob? persisted = null;
        BacktestJobService? service = null;
        var durable = new Mock<IDurableJobRepository>();
        durable.Setup(candidate => candidate.GetJobsAsync("backtest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted is null ? [] : [persisted]);
        durable.Setup(candidate => candidate.TryEnqueueAsync(
                It.IsAny<PersistedJob>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (PersistedJob job, int _, CancellationToken token) =>
            {
                persisted = job;
                await service!.RefreshProjectionAsync(token);
                return true;
            });
        service = CreateService(new ImmediateBacktestExecutor(), durableRepository: durable.Object);

        var started = await service.StartAsync("projection-race", configPath);

        Assert.Equal("queued", started.Status);
        Assert.Equal(persisted!.Id, started.JobId);
        Assert.Single(service.List(), job => job.JobId == persisted.Id);
    }

    [Fact]
    public async Task Cancellation_RemainsNonTerminalUntilWorkerDrains()
    {
        var executor = new BlockingBacktestExecutor();
        var service = CreateService(executor);
        var started = await service.StartAsync("cancel-after-drain", configPath);
        var processing = ProcessOneAsync(service);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var running = await repository.GetJobAsync(started.JobId, CancellationToken.None);
        Assert.Equal("running", running!.Status);
        Assert.NotNull(running.LeaseToken);

        var outcome = await service.CancelJobAsync(started.JobId);
        await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(BacktestCancellationOutcome.Accepted, outcome);
        Assert.Equal("cancelling", (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.Status);
        Assert.False(processing.IsCompleted);

        executor.AllowDrain.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cancelled", service.Get(started.JobId)!.Status);
    }

    [Fact]
    public async Task QueueCapacity_BoundsDurableWaitingAndRunningJobs()
    {
        var options = BacktestJobServiceOptions.Default with { MaximumPendingJobs = 1 };
        var service = CreateService(new BlockingBacktestExecutor(), options);
        _ = await service.StartAsync("first-capacity", configPath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync("second-capacity", configPath));

        Assert.Contains("maximum of 1", error.Message, StringComparison.Ordinal);
        Assert.Single(await repository.GetJobsAsync("backtest", CancellationToken.None));
    }

    [Fact]
    public async Task Restart_LoadsAndReplaysQueuedJobFromDurableRequest()
    {
        var first = CreateService(new ImmediateBacktestExecutor());
        var started = await first.StartAsync("restart-replay", configPath);
        var resumedExecutor = new ImmediateBacktestExecutor();
        var resumed = CreateService(resumedExecutor);
        await resumed.InitializeAsync(CancellationToken.None);

        await ProcessOneAsync(resumed);

        Assert.Equal(1, resumedExecutor.ExecutionCount);
        Assert.Equal("completed", resumed.Get(started.JobId)!.Status);
        Assert.Equal("completed", (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Execution_UsesFrozenCutoffAndLeaseScopedResultPath()
    {
        var executor = new ImmediateBacktestExecutor();
        var service = CreateService(executor);
        var started = await service.StartAsync("frozen-at-enqueue", configPath);
        var persistedBeforeRun = await repository.GetJobAsync(started.JobId, CancellationToken.None);
        var request = DurableFileJobRequest.Deserialize(persistedBeforeRun!.RequestJson);

        await ProcessOneAsync(service);

        Assert.Equal(request.CapturedAtUtc, executor.EvaluationCutoffUtc);
        Assert.Contains(started.JobId.ToString("N"), executor.ResultPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".durable-attempts", executor.ResultPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            executor.ResultPath,
            (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.ResultReference);
        Assert.True(DurableJobResultPublication.IsPublished(executor.ResultPath));
    }

    [Fact]
    public async Task RefreshProjectionAsync_ObservesCompletionPublishedByAnotherProcess()
    {
        var owner = CreateService(new ImmediateBacktestExecutor());
        var observer = CreateService(new ImmediateBacktestExecutor());
        var started = await owner.StartAsync("cross-process-projection", configPath);
        await observer.InitializeAsync(CancellationToken.None);
        Assert.Equal("queued", observer.Get(started.JobId)!.Status);

        await ProcessOneAsync(owner);
        Assert.Equal("queued", observer.Get(started.JobId)!.Status);
        await observer.RefreshProjectionAsync(CancellationToken.None);

        Assert.Equal("completed", observer.Get(started.JobId)!.Status);
    }

    [Fact]
    public async Task MissingResultMarker_DoesNotHideCompletionAndIsRetriedLater()
    {
        var owner = CreateService(new ImmediateBacktestExecutor());
        var started = await owner.StartAsync("marker-retry", configPath);
        var queuedSnapshot = (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.SnapshotJson;
        var lease = await repository.TryAcquireAsync(
            started.JobId,
            "marker-test",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.NotNull(lease);
        var resultPath = Path.Combine(
            testRoot,
            "results",
            ".durable-attempts",
            "backtest",
            started.JobId.ToString("N"),
            lease!.LeaseToken.ToString("N"),
            "result.json");
        Assert.True(await repository.CompleteAsync(
            started.JobId,
            lease.LeaseToken,
            "completed",
            queuedSnapshot,
            resultPath,
            null,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        var observer = CreateService(new ImmediateBacktestExecutor());

        await observer.InitializeAsync(CancellationToken.None);

        Assert.Equal("completed", observer.Get(started.JobId)!.Status);
        Assert.False(DurableJobResultPublication.IsPublished(resultPath));

        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await File.WriteAllTextAsync(resultPath, "{}");
        await observer.RefreshProjectionAsync(CancellationToken.None);

        Assert.True(DurableJobResultPublication.IsPublished(resultPath));
    }

    [Fact]
    public async Task CompletedResult_IsProjectedToBoundedUiRetention()
    {
        var options = BacktestJobServiceOptions.Default with
        {
            RetainedRecentTrades = 3,
            RetainedMissedMoves = 2
        };
        var service = CreateService(new ImmediateBacktestExecutor(5, 4), options);
        var started = await service.StartAsync("bounded-result", configPath);

        await ProcessOneAsync(service);

        var completed = service.Get(started.JobId)!;
        Assert.Equal("completed", completed.Status);
        Assert.NotNull(completed.Result);
        Assert.Equal(3, completed.Result.CompletedTrades.Count);
        Assert.Equal(2, completed.Result.MissedMoves.Count);
        Assert.All(completed.Result.StrategyResults, strategy => Assert.Empty(strategy.CompletedTrades));
        Assert.Empty(completed.Result.AcceptedOrders);
    }

    [Fact]
    public async Task ModifiedInput_AfterEnqueueFailsBeforeExecutorRuns()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"trading-flow-durable-config-{Guid.NewGuid():N}");
        var sourceConfigsRoot = Directory.GetParent(Path.GetDirectoryName(configPath)!)!.FullName;
        CopyDirectory(sourceConfigsRoot, tempRoot);
        var copiedConfig = Path.Combine(tempRoot, "backtest", Path.GetFileName(configPath));
        try
        {
            var executor = new ImmediateBacktestExecutor();
            var service = CreateService(executor);
            var started = await service.StartAsync("immutable-input", copiedConfig);
            await File.AppendAllTextAsync(copiedConfig, Environment.NewLine + "# changed after enqueue");

            await ProcessOneAsync(service);

            Assert.Equal(0, executor.ExecutionCount);
            Assert.Equal("failed", service.Get(started.JobId)!.Status);
            Assert.Contains("changed after enqueue", service.Get(started.JobId)!.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private BacktestJobService CreateService(
        IBacktestRunExecutor executor,
        BacktestJobServiceOptions? options = null,
        IDurableJobRepository? durableRepository = null) =>
        new(executor, durableRepository ?? repository, new SimpleYamlReader(), options ?? BacktestJobServiceOptions.Default);

    private Task<bool> ProcessOneAsync(BacktestJobService service) =>
        new DurableJobProcessor(repository).ProcessNextAsync(
            "test-worker",
            service,
            DurableJobWorkerOptions.Default with
            {
                LeaseDuration = TimeSpan.FromSeconds(10),
                HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                IdlePollInterval = TimeSpan.FromMilliseconds(10)
            },
            CancellationToken.None);

    private sealed class BlockingBacktestExecutor : IBacktestRunExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BacktestResult> RunAsync(
            string configPath,
            string resultPath,
            DateTimeOffset evaluationCutoffUtc,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            Started.TrySetResult();
            progress?.Report(new BacktestProgress("running_strategy_ticker", "Evaluating RGTI.", "RGTI", 3, 8, "Test Strategy"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await AllowDrain.Task;
                throw;
            }

            throw new InvalidOperationException("The blocking executor should finish only through cancellation.");
        }
    }

    private sealed class ImmediateBacktestExecutor(int completedTradeCount = 0, int missedMoveCount = 0) : IBacktestRunExecutor
    {
        private int executions;
        public int ExecutionCount => Volatile.Read(ref executions);
        public string ResultPath { get; private set; } = String.Empty;
        public DateTimeOffset? EvaluationCutoffUtc { get; private set; }

        public Task<BacktestResult> RunAsync(
            string configPath,
            string resultPath,
            DateTimeOffset evaluationCutoffUtc,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null)
        {
            Interlocked.Increment(ref executions);
            ResultPath = resultPath;
            EvaluationCutoffUtc = evaluationCutoffUtc;
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, "{}");
            var now = DateTimeOffset.UtcNow;
            var trades = Enumerable.Repeat<BacktestTrade>(null!, completedTradeCount).ToArray();
            var missedMoves = Enumerable.Repeat<MissedMoveAudit>(null!, missedMoveCount).ToArray();
            var strategy = new StrategyBacktestResult(
                "test", "Test", "test", 100_000m, 100_000m, 0m, 0m, 0m, 1,
                0m, 0, 0, 0, 0, 0, trades);
            var result = new BacktestResult(
                Path.GetFileNameWithoutExtension(configPath),
                resultPath,
                now, now, 100_000m, 100_000m, 0m, 0m, 0m, 1, 0m, null,
                0, 0, 0, 0, 0, 0, [strategy], [], CreateValidation(), trades, [], missedMoves, []);
            return Task.FromResult(result);
        }

        private static BacktestValidationReport CreateValidation() => new(
            new OutOfSampleValidation(false, 0m, null, []),
            [],
            new BenchmarkValidation(false, "", null, []),
            new DataQualityValidation(0, 0, 0, 0, 0, []),
            new BiasRiskValidation("test", null, "none", false, false, []));
    }

    private sealed class TestContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
        public Task<TradingFlowDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
