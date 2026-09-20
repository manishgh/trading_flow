using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Tests;

public sealed class DurableJobProcessorTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-job-processor-{Guid.NewGuid():N}.db");
    private SqliteJobRepository repository = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestContextFactory(options);
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        repository = new SqliteJobRepository(factory);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentWorkers_ExecuteAndPublishJobOnce()
    {
        await repository.TryEnqueueAsync(CreateJob(), 4, CancellationToken.None);
        var handler = new ImmediateHandler(DurableJobRecoveryMode.ReplayFromStart);
        var processor = new DurableJobProcessor(repository);
        var options = FastOptions();

        await Task.WhenAll(
            processor.ProcessNextAsync("worker-a", handler, options, CancellationToken.None),
            processor.ProcessNextAsync("worker-b", handler, options, CancellationToken.None));

        var stored = Assert.Single(await repository.GetJobsAsync("backtest", CancellationToken.None));
        Assert.Equal("completed", stored.Status);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal(1, handler.TerminalPublicationCount);
    }

    [Fact]
    public async Task RunningCancellation_BecomesTerminalOnlyAfterHandlerDrains()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var handler = new DrainingHandler();
        var processor = new DurableJobProcessor(repository);
        var processing = processor.ProcessNextAsync("worker-a", handler, FastOptions(), CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await repository.RequestCancellationAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None));
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cancelling", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
        Assert.False(processing.IsCompleted);

        handler.AllowDrain.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("cancelled", stored!.Status);
        Assert.NotNull(stored.FinishedAt);
    }

    [Fact]
    public async Task RecoveredBrokerJob_ReconcilesBeforeExecution()
    {
        var job = CreateJob("paper");
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var expiredAt = DateTimeOffset.UtcNow;
        _ = await repository.TryAcquireAsync(
            job.Id,
            "dead-worker",
            expiredAt,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        await Task.Delay(180);
        var handler = new ImmediateHandler(DurableJobRecoveryMode.ReconcileBrokerExposure, "paper");

        await new DurableJobProcessor(repository).ProcessNextAsync(
            "recovery-worker",
            handler,
            FastOptions(),
            CancellationToken.None);

        Assert.Equal(new[] { "recover", "execute", "published" }, handler.Events);
        Assert.Equal(2, (await repository.GetJobAsync(job.Id, CancellationToken.None))!.AttemptCount);
    }

    [Fact]
    public async Task RecoveredCancellingBrokerJob_ReconcilesThenCancelsWithoutNormalExecution()
    {
        var job = CreateJob("paper");
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var expiredAt = DateTimeOffset.UtcNow;
        _ = await repository.TryAcquireAsync(
            job.Id,
            "dead-worker",
            expiredAt,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        await Task.Delay(180);
        Assert.True(await repository.RequestCancellationAsync(job.Id, expiredAt, CancellationToken.None));
        var handler = new ImmediateHandler(DurableJobRecoveryMode.ReconcileBrokerExposure, "paper");

        await new DurableJobProcessor(repository).ProcessNextAsync(
            "recovery-worker",
            handler,
            FastOptions(),
            CancellationToken.None);

        Assert.Equal(new[] { "recover", "published" }, handler.Events);
        Assert.Equal("cancelled", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task HostShutdown_DrainsHandlerThenReleasesReplayableLease()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var handler = new DrainingHandler();
        var processor = new DurableJobProcessor(repository);
        using var shutdown = new CancellationTokenSource();
        var processing = processor.ProcessNextAsync("worker-a", handler, FastOptions(), shutdown.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        shutdown.Cancel();
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(processing.IsCompleted);
        handler.AllowDrain.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("running", stored!.Status);
        Assert.Null(stored.LeaseToken);
        Assert.Null(stored.FinishedAt);
    }

    [Fact]
    public async Task MonitorStoreFailure_CancelsWorkAndSuppressesResultPublication()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var guardedRepository = new Mock<IDurableJobRepository>(MockBehavior.Strict);
        guardedRepository
            .Setup(value => value.TryAcquireNextAsync(
                "backtest",
                "worker-a",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns((string jobType, string ownerId, DateTimeOffset now, TimeSpan duration, CancellationToken token) =>
                repository.TryAcquireNextAsync(jobType, ownerId, now, duration, token));
        guardedRepository
            .Setup(value => value.GetJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("durable store unavailable"));
        var handler = new DrainingHandler();

        var processing = new DurableJobProcessor(guardedRepository.Object).ProcessNextAsync(
            "worker-a",
            handler,
            FastOptions(),
            CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        handler.AllowDrain.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("running", stored!.Status);
        Assert.Null(stored.FinishedAt);
        Assert.Equal(0, handler.TerminalPublicationCount);
    }

    [Fact]
    public async Task MonitorStoreFailure_NonCooperativeHandlerCannotPublishFailure()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var guardedRepository = new Mock<IDurableJobRepository>(MockBehavior.Strict);
        guardedRepository
            .Setup(value => value.TryAcquireNextAsync(
                "backtest",
                "worker-a",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns((string jobType, string ownerId, DateTimeOffset now, TimeSpan duration, CancellationToken token) =>
                repository.TryAcquireNextAsync(jobType, ownerId, now, duration, token));
        guardedRepository
            .Setup(value => value.GetJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("durable store unavailable"));
        var handler = new NonCooperativeCancellationHandler();

        await new DurableJobProcessor(guardedRepository.Object).ProcessNextAsync(
            "worker-a",
            handler,
            FastOptions(),
            CancellationToken.None);

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("running", stored!.Status);
        Assert.Null(stored.FinishedAt);
        Assert.Equal(0, handler.TerminalPublicationCount);
        guardedRepository.Verify(value => value.CompleteAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MonitorStoreFailure_HandlerIgnoringCancellationCannotPublishSuccess()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var guardedRepository = new Mock<IDurableJobRepository>(MockBehavior.Strict);
        guardedRepository
            .Setup(value => value.TryAcquireNextAsync(
                "backtest",
                "worker-a",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns((string jobType, string ownerId, DateTimeOffset now, TimeSpan duration, CancellationToken token) =>
                repository.TryAcquireNextAsync(jobType, ownerId, now, duration, token));
        guardedRepository
            .SetupSequence(value => value.GetJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("durable store unavailable"))
            .ReturnsAsync(job);
        guardedRepository
            .Setup(value => value.CompleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid id, Guid token, string status, string? snapshot, string? result,
                string? error, DateTimeOffset finished, CancellationToken cancellationToken) =>
                repository.CompleteAsync(id, token, status, snapshot, result, error, finished, cancellationToken));
        var handler = new CancellationIgnoringSuccessHandler();

        await new DurableJobProcessor(guardedRepository.Object).ProcessNextAsync(
            "worker-a",
            handler,
            FastOptions(),
            CancellationToken.None);

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("running", stored!.Status);
        Assert.Null(stored.FinishedAt);
        guardedRepository.Verify(value => value.CompleteAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TerminalProjectionFailure_DoesNotRecompleteOrFailTheDurableJob()
    {
        var job = CreateJob();
        await repository.TryEnqueueAsync(job, 4, CancellationToken.None);
        var handler = new ThrowingPublicationHandler();
        var processor = new DurableJobProcessor(repository);

        Assert.True(await processor.ProcessNextAsync(
            "worker-a",
            handler,
            FastOptions(),
            CancellationToken.None));

        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("completed", stored!.Status);
        Assert.Equal("result.json", stored.ResultReference);
        Assert.Null(stored.LeaseToken);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(1, handler.TerminalPublicationCount);
        Assert.False(await processor.ProcessNextAsync(
            "worker-a",
            handler,
            FastOptions(),
            CancellationToken.None));
    }

    private static DurableJobWorkerOptions FastOptions() => new(
        MaximumPendingJobs: 4,
        MaximumConcurrentWorkers: 1,
        LeaseDuration: TimeSpan.FromSeconds(1),
        HeartbeatInterval: TimeSpan.FromMilliseconds(20),
        IdlePollInterval: TimeSpan.FromMilliseconds(10),
        ProjectionRefreshInterval: TimeSpan.FromMilliseconds(20));

    private static PersistedJob CreateJob(string jobType = "backtest") => new()
    {
        Id = Guid.NewGuid(),
        JobType = jobType,
        RunName = $"test-{Guid.NewGuid():N}",
        ConfigPath = "run.yaml",
        RequestJson = "{}",
        Status = "queued",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class ImmediateHandler(
        DurableJobRecoveryMode recoveryMode,
        string jobType = "backtest") : IDurableJobHandler
    {
        private int executions;
        private int publications;

        public string JobType => jobType;
        public DurableJobRecoveryMode RecoveryMode => recoveryMode;
        public int ExecutionCount => Volatile.Read(ref executions);
        public int TerminalPublicationCount => Volatile.Read(ref publications);
        public List<string> Events { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken)
        {
            Events.Add("recover");
            return Task.CompletedTask;
        }

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executions);
            Events.Add("execute");
            return Task.FromResult(DurableJobCompletion.Completed("{}", "result.json"));
        }

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref publications);
            Events.Add("published");
            return Task.CompletedTask;
        }
    }

    private sealed class DrainingHandler : IDurableJobHandler
    {
        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TerminalPublicationCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
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

            throw new InvalidOperationException("The test handler should complete only through cancellation.");
        }

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken)
        {
            TerminalPublicationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublicationHandler : IDurableJobHandler
    {
        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
        public int TerminalPublicationCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(DurableJobCompletion.Completed("{}", "result.json"));

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken)
        {
            TerminalPublicationCount++;
            throw new IOException("projection unavailable");
        }
    }

    private sealed class NonCooperativeCancellationHandler : IDurableJobHandler
    {
        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
        public int TerminalPublicationCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("handler translated cancellation into failure");
            }

            throw new InvalidOperationException("Expected cancellation.");
        }

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken)
        {
            TerminalPublicationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationIgnoringSuccessHandler : IDurableJobHandler
    {
        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DurableJobCompletion.Completed("{}", "result.json");
            }

            throw new InvalidOperationException("Expected cancellation.");
        }

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
