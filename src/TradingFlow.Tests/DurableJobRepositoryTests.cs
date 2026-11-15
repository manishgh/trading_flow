using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Persistence;
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

    // The durable lease alone authorizes state changes for this exact paper run.
    [Fact]
    public async Task PaperRunLifecycle_OnlyCurrentLeaseCanReopenOrFinishAfterCrash()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var first = await repository.TryAcquireAsync(job.Id, "first-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        var production = CreatePaperRun(job.Id);
        Assert.NotNull(await repository.StartPaperRunAsync(production, first.LeaseToken, default));
        var original = await ReadRunAsync(job.Id);
        Assert.Equal("running", original.Status);

        var other = CreatePaperRun(Guid.NewGuid());
        await using (var db = factory.CreateDbContext())
        {
            db.ProductionRuns.Add(other);
            await db.SaveChangesAsync();
            await db.Jobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.LeaseExpiresAtUtcTicks, 0L));
        }

        var recovered = await repository.TryAcquireAsync(job.Id, "next-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(recovered);
        var abandoned = await ReadRunAsync(job.Id);
        Assert.Equal("interrupted", abandoned.Status);
        Assert.NotNull(abandoned.FinishedAtUtc);
        Assert.Null(await repository.StartPaperRunAsync(CreatePaperRun(job.Id), first.LeaseToken, default));
        Assert.False(await repository.CompleteAsync(job.Id, first.LeaseToken, "failed", null, null, "stale owner", DateTimeOffset.UtcNow, default));

        var compatible = CreatePaperRun(job.Id);
        Assert.NotNull(await repository.StartPaperRunAsync(compatible, recovered.LeaseToken, default));
        var reopened = await ReadRunAsync(job.Id);
        Assert.Equal(original.StartedAtUtc, reopened.StartedAtUtc);
        Assert.Null(reopened.FinishedAtUtc);

        Assert.True(await repository.RequestCancellationAsync(job.Id, DateTimeOffset.UtcNow, default));
        Assert.True(await repository.CompleteAsync(job.Id, recovered.LeaseToken, "cancelled", null, null, null, DateTimeOffset.UtcNow, default));
        Assert.Equal("cancelled", (await ReadRunAsync(job.Id)).Status);
        Assert.NotNull((await ReadRunAsync(job.Id)).FinishedAtUtc);
        Assert.Equal("running", (await ReadRunAsync(other.RunId)).Status);
    }

    // A normal host stop releases the lease and closes the row; the same provenance can resume later.
    [Fact]
    public async Task PaperRunLifecycle_ShutdownClosesRunWithNoChangeToItsProvenance()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var lease = await repository.TryAcquireAsync(job.Id, "worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.NotNull(await repository.StartPaperRunAsync(CreatePaperRun(job.Id), lease.LeaseToken, default));
        await repository.ReleaseLeaseAsync(job.Id, lease.LeaseToken, default);

        var run = await ReadRunAsync(job.Id);
        Assert.Equal("stopped", run.Status);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.Equal(CreatePaperRun(job.Id).CodeVersion, run.CodeVersion);
        Assert.Null((await repository.GetJobAsync(job.Id, default))?.LeaseToken);
    }

    // All stored identity fields remain exact after a rejected restart.
    [Theory]
    [InlineData("profile")]
    [InlineData("schema")]
    [InlineData("config")]
    [InlineData("universe")]
    [InlineData("decision")]
    [InlineData("started")]
    public async Task PaperRunLifecycle_ConflictingStartupDoesNotRewriteStoredRun(string changedField)
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var lease = await repository.TryAcquireAsync(job.Id, "worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var original = CreatePaperRun(job.Id);
        Assert.NotNull(await repository.StartPaperRunAsync(original, lease.LeaseToken, default));
        var changed = CreatePaperRun(job.Id);
        changed.CodeVersion = new string('c', 40);
        switch (changedField)
        {
            case "profile": changed.Profile = "paper_shadow"; break;
            case "schema": changed.SchemaVersion++; break;
            case "config": changed.ConfigHash = new string('d', 64); break;
            case "universe": changed.UniverseSnapshotId = Guid.NewGuid(); break;
            case "decision": changed.DecisionRunId = Guid.NewGuid(); break;
            case "started": changed.StartedAtUtc = changed.StartedAtUtc.AddDays(1); break;
        }

        var preflight = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ValidatePaperRunAsync(changed, default));
        var registration = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.StartPaperRunAsync(changed, lease.LeaseToken, default));
        Assert.Equal(preflight.Message, registration.Message);
        Assert.Contains("different provenance", preflight.Message, StringComparison.Ordinal);

        var stored = await ReadRunAsync(job.Id);
        Assert.Equal(original.CodeVersion, stored.CodeVersion);
        Assert.Equal(original.ConfigHash, stored.ConfigHash);
        Assert.Equal(original.UniverseSnapshotId, stored.UniverseSnapshotId);
        Assert.Equal(original.DecisionRunId, stored.DecisionRunId);
        Assert.Equal(original.StartedAtUtc, stored.StartedAtUtc);
    }

    // The replacement lease latches the stop and records the actual build before publishing the run.
    [Fact]
    public async Task PaperRunLifecycle_RebuildContinuationIsStickyAcrossRestartsAndBuildRollback()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var first = await repository.TryAcquireAsync(job.Id, "first", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        var original = CreatePaperRun(job.Id);
        var initial = await repository.StartPaperRunAsync(original, first.LeaseToken, default);
        Assert.NotNull(initial);
        Assert.Null(initial.ExitOnlyContinuation);
        Assert.Null(initial.Run.PaperEntriesStoppedAtUtc);

        var current = CreatePaperRun(job.Id);
        current.CodeVersion = new string('d', 40);
        await repository.ValidatePaperRunAsync(current, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.StartPaperRunAsync(current, first.LeaseToken, default));
        await repository.ReleaseLeaseAsync(job.Id, first.LeaseToken, default);

        var next = await repository.TryAcquireAsync(job.Id, "replacement", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(next);
        Assert.Null(await repository.StartPaperRunAsync(current, first.LeaseToken, default));
        var continued = await repository.StartPaperRunAsync(current, next.LeaseToken, default);
        Assert.NotNull(continued);
        Assert.NotNull(continued.ExitOnlyContinuation);
        Assert.Equal(next.LeaseToken, continued.Run.PaperJobLeaseToken);
        Assert.Equal(continued.ExitOnlyContinuation.EntriesStoppedAtUtc, continued.Run.PaperEntriesStoppedAtUtc);
        Assert.Equal(original.RunId, continued.Run.RunId);
        Assert.Equal(original.CodeVersion, continued.Run.CodeVersion);
        Assert.Equal(original.StartedAtUtc, continued.Run.StartedAtUtc);
        Assert.Equal(current.CodeVersion, continued.ExitOnlyContinuation.ExecutingCodeVersion);
        Assert.Equal(original.CodeVersion, continued.ExitOnlyContinuation.OriginCodeVersion);
        Assert.Equal(2, continued.ExitOnlyContinuation.LeaseAttempt);
        var repeated = await repository.StartPaperRunAsync(current, next.LeaseToken, default);
        Assert.Equal(continued.ExitOnlyContinuation, repeated?.ExitOnlyContinuation);
        await repository.ReleaseLeaseAsync(job.Id, next.LeaseToken, default);

        // No job snapshot was saved, as if the replacement crashed just after committing the latch.
        var restored = await repository.TryAcquireAsync(job.Id, "rollback-build", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(restored);
        Assert.Null(restored.Job.SnapshotJson);
        var resumed = await repository.StartPaperRunAsync(CreatePaperRun(job.Id), restored.LeaseToken, default);
        Assert.NotNull(resumed);
        Assert.NotNull(resumed.ExitOnlyContinuation);
        Assert.Equal(continued.Run.PaperEntriesStoppedAtUtc, resumed.Run.PaperEntriesStoppedAtUtc);
        Assert.Equal(original.CodeVersion, resumed.ExitOnlyContinuation.ExecutingCodeVersion);
        Assert.Equal(3, resumed.ExitOnlyContinuation.LeaseAttempt);

        await using var db = factory.CreateDbContext();
        var records = await db.ApplicationEvents.Where(record => record.StreamName == PaperExitOnlyContinuation.StreamName(job.Id))
            .OrderBy(record => record.EventSequence).ToArrayAsync();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.All(records, record => Assert.Equal(PaperExitOnlyContinuation.EventType, record.EventType));
        Assert.Equal([continued.ExitOnlyContinuation, resumed.ExitOnlyContinuation],
            records.Select(record => JsonSerializer.Deserialize<PaperExitOnlyContinuation>(record.PayloadJson, json)));
    }

    // A cancellation recorded before startup prevents registration under the otherwise current lease.
    [Fact]
    public async Task PaperRunLifecycle_CancellationFencesRunStartup()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var lease = await repository.TryAcquireAsync(job.Id, "worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.True(await repository.RequestCancellationAsync(job.Id, DateTimeOffset.UtcNow, default));
        Assert.Null(await repository.StartPaperRunAsync(CreatePaperRun(job.Id), lease.LeaseToken, default));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.ProductionRuns.AnyAsync(run => run.RunId == job.Id));
    }

    // A terminal health snapshot and its entry fence survive release and compatible retry together.
    [Fact]
    public async Task PaperRunLifecycle_EntryStopSurvivesACompatibleRestart()
    {
        var repository = new SqliteJobRepository(factory);
        var job = CreateJob();
        job.JobType = "paper";
        Assert.True(await repository.TryEnqueueAsync(job, 10, default));
        var first = await repository.TryAcquireAsync(job.Id, "first", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(first);
        Assert.NotNull(await repository.StartPaperRunAsync(CreatePaperRun(job.Id), first.LeaseToken, default));
        const string snapshot = "{\"paperPipelineHealth\":{\"entriesStopped\":true}}";
        var stopped = DateTimeOffset.UtcNow;
        Assert.True(await repository.SavePaperEntryStopSnapshotAsync(job.Id, first.LeaseToken, snapshot, stopped, stopped, default));
        await repository.ReleaseLeaseAsync(job.Id, first.LeaseToken, default);

        var second = await repository.TryAcquireAsync(job.Id, "second", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(second);
        Assert.Equal("stopped", (await ReadRunAsync(job.Id)).Status);
        Assert.Equal(snapshot, second.Job.SnapshotJson);
        Assert.NotNull(await repository.StartPaperRunAsync(CreatePaperRun(job.Id), second.LeaseToken, default));

        var reopened = await ReadRunAsync(job.Id);
        Assert.Equal("running", reopened.Status);
        Assert.Equal(stopped, reopened.PaperEntriesStoppedAtUtc);
        Assert.Equal(second.LeaseToken, reopened.PaperJobLeaseToken);
        Assert.False(await repository.SavePaperEntryStopSnapshotAsync(job.Id, first.LeaseToken, "{}", stopped.AddSeconds(1), stopped, default));
        Assert.Equal(snapshot, (await repository.GetJobAsync(job.Id, default))?.SnapshotJson);
    }

    // Restart reconciles only marked, expired host rows. Shutdown affects just the current host ID.
    [Fact]
    public async Task HostRunLifecycle_LeavesLiveHostsAndUnclassifiedRowsUntouched()
    {
        var repository = new SqliteProductionHostRunRepository(factory);
        var expired = CreatePaperRun(Guid.NewGuid());
        expired.HostLeaseExpiresAtUtcTicks = 1;
        var active = CreatePaperRun(Guid.NewGuid());
        var unclassified = CreatePaperRun(Guid.NewGuid());
        await using (var db = factory.CreateDbContext())
        {
            db.ProductionRuns.AddRange(expired, unclassified);
            await db.SaveChangesAsync();
        }
        await repository.StartAsync(active, TimeSpan.FromMinutes(1), default);
        var secondHost = CreatePaperRun(Guid.NewGuid());
        await repository.StartAsync(secondHost, TimeSpan.FromMinutes(1), default);
        Assert.Equal("interrupted", (await ReadRunAsync(expired.RunId)).Status);
        Assert.NotNull((await ReadRunAsync(expired.RunId)).FinishedAtUtc);
        Assert.False(await repository.RenewAsync(expired.RunId, TimeSpan.FromMinutes(1), default));

        await repository.StopAsync(secondHost.RunId, default);
        Assert.Equal("stopped", (await ReadRunAsync(secondHost.RunId)).Status);
        Assert.NotNull((await ReadRunAsync(secondHost.RunId)).FinishedAtUtc);
        Assert.Equal("running", (await ReadRunAsync(active.RunId)).Status);
        Assert.Equal("running", (await ReadRunAsync(unclassified.RunId)).Status);
        Assert.Null((await ReadRunAsync(unclassified.RunId)).FinishedAtUtc);
    }

    // Read the committed row with a separate connection to expose transaction and ownership defects.
    private async Task<ProductionRun> ReadRunAsync(Guid id)
    {
        await using var context = factory.CreateDbContext();
        return await context.ProductionRuns.AsNoTracking().SingleAsync(run => run.RunId == id);
    }

    // Use one explicit immutable identity for all lifecycle transitions of an owned test run.
    private static ProductionRun CreatePaperRun(Guid id) => new()
    {
        RunId = id,
        Profile = "paper",
        Status = "running",
        ConfigHash = new string('a', 64),
        CodeVersion = new string('b', 40),
        StartedAtUtc = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero)
    };

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
