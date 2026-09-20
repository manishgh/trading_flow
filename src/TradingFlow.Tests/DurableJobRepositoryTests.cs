using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Tests;

public sealed class DurableJobRepositoryTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-job-queue-{Guid.NewGuid():N}.db");
    private DbContextOptions<TradingFlowDbContext> options = null!;
    private TestContextFactory factory = null!;

    public async Task InitializeAsync()
    {
        options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        factory = new TestContextFactory(options);
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
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
    public async Task TryAcquireNextAsync_ConcurrentWorkers_ProducesOneOwner()
    {
        var repository = new SqliteJobRepository(factory);
        Assert.True(await repository.TryEnqueueAsync(CreateJob(), 10, CancellationToken.None));
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        var results = await Task.WhenAll(
            repository.TryAcquireNextAsync("backtest", "worker-a", now, TimeSpan.FromSeconds(30), CancellationToken.None),
            repository.TryAcquireNextAsync("backtest", "worker-b", now, TimeSpan.FromSeconds(30), CancellationToken.None));

        var lease = Assert.Single(results, result => result is not null);
        Assert.Equal("running", lease!.Job.Status);
        Assert.Equal(1, lease.Job.AttemptCount);
        Assert.Contains(lease.Job.LeaseOwner, new[] { "worker-a", "worker-b" });
    }

    [Fact]
    public async Task TryAcquireAsync_ExpiredLease_IsRecoveredWithNewFence()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var firstTime = DateTimeOffset.UtcNow;
        var first = await repository.TryAcquireAsync(
            job.Id, "worker-a", firstTime, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(180);

        var recovered = await repository.TryAcquireAsync(
            job.Id, "worker-b", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(recovered);
        Assert.NotEqual(first!.LeaseToken, recovered!.LeaseToken);
        Assert.Equal("worker-b", recovered.Job.LeaseOwner);
        Assert.Equal(2, recovered.Job.AttemptCount);
    }

    [Fact]
    public async Task TryAcquireAsync_FastWorkerClockCannotStealLiveLease()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var first = await repository.TryAcquireAsync(
            job.Id, "worker-a", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), CancellationToken.None);

        var stolen = await repository.TryAcquireAsync(
            job.Id,
            "fast-clock-worker",
            DateTimeOffset.UtcNow.AddDays(1),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(stolen);
        Assert.Equal("worker-a", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.LeaseOwner);
    }

    [Fact]
    public async Task CompleteAsync_StaleLeaseToken_CannotOverwriteCurrentOwner()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var first = await repository.TryAcquireAsync(job.Id, "worker-a", now, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(180);
        var current = await repository.TryAcquireAsync(job.Id, "worker-b", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), CancellationToken.None);

        var staleCompleted = await repository.CompleteAsync(
            job.Id,
            first!.LeaseToken,
            "completed",
            "{}",
            null,
            null,
            now.AddSeconds(12),
            CancellationToken.None);

        Assert.NotNull(current);
        Assert.False(staleCompleted);
        Assert.Equal("running", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RequestCancellationAsync_QueuedJob_TerminalizesWithoutDispatch()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var now = DateTimeOffset.UtcNow;

        var accepted = await repository.RequestCancellationAsync(job.Id, now, CancellationToken.None);
        var lease = await repository.TryAcquireAsync(
            job.Id, "worker-a", now.AddSeconds(1), TimeSpan.FromSeconds(30), CancellationToken.None);
        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);

        Assert.True(accepted);
        Assert.Null(lease);
        Assert.Equal("cancelled", stored!.Status);
        Assert.Equal(now, stored.CancellationRequestedAtUtc);
        Assert.Equal(now, stored.FinishedAt);
    }

    [Fact]
    public async Task TryEnqueueAsync_ConcurrentAdmission_EnforcesDatabaseCapacity()
    {
        var repository = new SqliteJobRepository(factory);

        var admitted = await Task.WhenAll(
            repository.TryEnqueueAsync(CreateJob(), 1, CancellationToken.None),
            repository.TryEnqueueAsync(CreateJob(), 1, CancellationToken.None));

        Assert.Single(admitted, value => value);
        Assert.Single(await repository.GetJobsAsync("backtest", CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_ExpiredLeaseCannotPublishWithoutAReplacementOwner()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var lease = await repository.TryAcquireAsync(
            job.Id, "worker-a", now, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Task.Delay(180);

        var completed = await repository.CompleteAsync(
            job.Id,
            lease!.LeaseToken,
            "completed",
            "{}",
            null,
            null,
            now.AddSeconds(11),
            CancellationToken.None);

        Assert.False(completed);
        Assert.Equal("running", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task CompleteAsync_PersistedCancellationPreventsCompletedResultPublication()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var now = DateTimeOffset.UtcNow;
        var lease = await repository.TryAcquireAsync(
            job.Id, "worker-a", now, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(lease);
        Assert.True(await repository.RequestCancellationAsync(
            job.Id, now.AddSeconds(1), CancellationToken.None));

        var completed = await repository.CompleteAsync(
            job.Id,
            lease!.LeaseToken,
            "completed",
            "{}",
            "result.json",
            null,
            now.AddSeconds(2),
            CancellationToken.None);
        var cancelled = await repository.CompleteAsync(
            job.Id,
            lease.LeaseToken,
            "cancelled",
            "{}",
            null,
            null,
            now.AddSeconds(2),
            CancellationToken.None);

        Assert.False(completed);
        Assert.True(cancelled);
        var stored = await repository.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal("cancelled", stored!.Status);
        Assert.Null(stored.ResultReference);
    }

    [Fact]
    public async Task CompleteAsync_DatabaseClockRejectsDelayedCallWithPreExpiryCallerTimestamp()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var acquiredAt = DateTimeOffset.UtcNow;
        var lease = await repository.TryAcquireAsync(
            job.Id, "worker-a", acquiredAt, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.NotNull(lease);
        await Task.Delay(180);

        var completed = await repository.CompleteAsync(
            job.Id,
            lease!.LeaseToken,
            "completed",
            "{}",
            "result.json",
            null,
            acquiredAt.AddMilliseconds(50),
            CancellationToken.None);

        Assert.False(completed);
        Assert.Equal("running", (await repository.GetJobAsync(job.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task HeartbeatMutations_DatabaseClockRejectsDelayedPreExpiryCallerTimestamp()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        Assert.True(await repository.TryEnqueueAsync(job, 10, CancellationToken.None));
        var acquiredAt = DateTimeOffset.UtcNow;
        var lease = await repository.TryAcquireAsync(
            job.Id, "worker-a", acquiredAt, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.NotNull(lease);
        await Task.Delay(180);

        var snapshotSaved = await repository.SaveSnapshotAsync(
            job.Id,
            lease!.LeaseToken,
            "{}",
            acquiredAt.AddMilliseconds(50),
            CancellationToken.None);
        var renewed = await repository.RenewLeaseAsync(
            job.Id,
            lease.LeaseToken,
            acquiredAt.AddMilliseconds(50),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.False(snapshotSaved);
        Assert.False(renewed);
    }

    private static PersistedJob CreateJob() => new()
    {
        Id = Guid.NewGuid(),
        JobType = "backtest",
        RunName = $"test-{Guid.NewGuid():N}",
        ConfigPath = "run.yaml",
        RequestJson = "{}",
        Status = "queued",
        CreatedAt = new DateTimeOffset(2026, 9, 6, 11, 59, 0, TimeSpan.Zero)
    };

    private sealed class TestContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
