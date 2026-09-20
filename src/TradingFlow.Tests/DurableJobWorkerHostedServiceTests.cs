using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class DurableJobWorkerHostedServiceTests
{
    [Fact]
    public async Task ProjectionRefreshFailure_IsRetriedWithoutTerminatingWorkers()
    {
        var repository = new Mock<IDurableJobRepository>();
        repository.Setup(value => value.TryAcquireNextAsync(
                "backtest", It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DurableJobLease?)null);
        var handler = new TransientProjectionFailureHandler();
        var workerOptions = DurableJobWorkerOptions.Default with
        {
            IdlePollInterval = TimeSpan.FromMilliseconds(10),
            ProjectionRefreshInterval = TimeSpan.FromMilliseconds(20)
        };
        var service = new DurableJobWorkerHostedService(
            repository.Object,
            [handler],
            new Dictionary<string, DurableJobWorkerOptions> { ["backtest"] = workerOptions },
            DurableJobHostOptions.Default,
            TimeProvider.System,
            NullLogger<DurableJobWorkerHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await handler.SuccessfulRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(handler.RefreshAttempts >= 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task QueueReadFailure_RestartsWorkerAfterBoundedDelay()
    {
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var repository = new Mock<IDurableJobRepository>();
        repository.Setup(value => value.TryAcquireNextAsync(
                "backtest", It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    throw new IOException("job store temporarily unavailable");
                }

                secondRead.TrySetResult();
                return Task.FromResult<DurableJobLease?>(null);
            });
        var handler = new TransientProjectionFailureHandler();
        var workerOptions = DurableJobWorkerOptions.Default with
        {
            IdlePollInterval = TimeSpan.FromMilliseconds(10),
            ProjectionRefreshInterval = TimeSpan.FromSeconds(1)
        };
        var service = new DurableJobWorkerHostedService(
            repository.Object,
            [handler],
            new Dictionary<string, DurableJobWorkerOptions> { ["backtest"] = workerOptions },
            DurableJobHostOptions.Default,
            TimeProvider.System,
            NullLogger<DurableJobWorkerHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Volatile.Read(ref reads) >= 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task StopAsync_ReservesHostBudgetWhenHandlerDrainIsSlow()
    {
        var job = new PersistedJob
        {
            Id = Guid.NewGuid(),
            JobType = "backtest",
            RunName = "slow-shutdown",
            ConfigPath = "run.yaml",
            RequestJson = "{}",
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            AttemptCount = 1
        };
        var lease = new DurableJobLease(job, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10));
        var repository = new Mock<IDurableJobRepository>();
        var claims = 0;
        repository.Setup(value => value.TryAcquireNextAsync(
                "backtest", It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref claims) == 1 ? lease : null);
        repository.Setup(value => value.GetJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repository.Setup(value => value.RenewLeaseAsync(
                job.Id, lease.LeaseToken, It.IsAny<DateTimeOffset>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(value => value.ReleaseLeaseAsync(
                job.Id, lease.LeaseToken, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var handler = new SlowDrainHandler();
        var workerOptions = DurableJobWorkerOptions.Default with
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            IdlePollInterval = TimeSpan.FromMilliseconds(10),
            ProjectionRefreshInterval = TimeSpan.FromSeconds(1)
        };
        var service = new DurableJobWorkerHostedService(
            repository.Object,
            [handler],
            new Dictionary<string, DurableJobWorkerOptions> { ["backtest"] = workerOptions },
            new DurableJobHostOptions(TimeSpan.FromMilliseconds(50)),
            TimeProvider.System,
            NullLogger<DurableJobWorkerHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(handler.Drained.Task.IsCompleted);
        handler.AllowDrain.TrySetResult();
        await handler.Drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Dispose();
    }

    private sealed class SlowDrainHandler : IDurableJobHandler
    {
        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshProjectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
                await AllowDrain.Task;
                Drained.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Expected cancellation.");
        }

        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TransientProjectionFailureHandler : IDurableJobHandler
    {
        private int refreshAttempts;

        public string JobType => "backtest";
        public DurableJobRecoveryMode RecoveryMode => DurableJobRecoveryMode.ReplayFromStart;
        public int RefreshAttempts => Volatile.Read(ref refreshAttempts);
        public TaskCompletionSource SuccessfulRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshProjectionAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref refreshAttempts) == 1)
            {
                throw new IOException("projection store temporarily unavailable");
            }

            SuccessfulRefresh.TrySetResult();
            return Task.CompletedTask;
        }

        public Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DurableJobCompletion> ExecuteAsync(
            DurableJobExecutionContext context,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No job should be acquired.");
        public Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
