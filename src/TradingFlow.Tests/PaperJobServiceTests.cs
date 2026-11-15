using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Persistence;
using TradingFlow.Web.Models;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PaperJobServiceTests
{
    private readonly Mock<IDurableJobRepository> repository = new();
    private readonly string configPath = Path.Combine(
        TestRepository.FindRoot(),
        "configs",
        "paper",
        "alpaca-paper.yaml");

    [Fact]
    public async Task StartAsync_AtomicallyEnqueuesWithoutStartingDetachedExecution()
    {
        PersistedJob? captured = null;
        repository.Setup(candidate => candidate.TryEnqueueAsync(
                It.IsAny<PersistedJob>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<PersistedJob, int, CancellationToken>((job, _, _) => captured = job)
            .ReturnsAsync(true);
        var service = CreateService();

        var started = await service.StartAsync("paper-durable", configPath);

        Assert.Equal("queued", started.Status);
        Assert.NotNull(captured);
        Assert.Equal(started.JobId, captured.Id);
        Assert.Equal("paper", captured.JobType);
        Assert.NotEqual("{}", captured.RequestJson);
        Assert.NotNull(captured.SnapshotJson);
    }

    [Fact]
    public async Task StartAsync_WhenDurableCapacityIsFull_DoesNotPublishRuntimeJob()
    {
        repository.Setup(candidate => candidate.TryEnqueueAsync(
                It.IsAny<PersistedJob>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync("paper-full", configPath));

        Assert.Empty(service.List());
    }

    [Fact]
    public async Task CancelJobAsync_PersistsCancellingAndDoesNotClaimDrainComplete()
    {
        var id = Guid.NewGuid();
        var running = CreatePersisted(id, "running");
        var cancelling = CreatePersisted(id, "cancelling");
        cancelling.CancellationRequestedAtUtc = DateTimeOffset.UtcNow;
        repository.SetupSequence(candidate => candidate.GetJobAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(running)
            .ReturnsAsync(cancelling);
        repository.Setup(candidate => candidate.RequestCancellationAsync(
                id,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService();

        var accepted = await service.CancelJobAsync(id);

        Assert.True(accepted);
        Assert.Equal("cancelling", service.Get(id)!.Status);
        Assert.Null(service.Get(id)!.FinishedAt);
        Assert.Contains(service.Get(id)!.Events, entry => entry.Contains("Waiting for the paper runner to drain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_LoadsRecoverableRunningJobWithoutMarkingItInterrupted()
    {
        var persisted = CreatePersisted(Guid.NewGuid(), "running");
        repository.Setup(candidate => candidate.PruneTerminalJobsOlderThanAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(candidate => candidate.GetJobsAsync("paper", It.IsAny<CancellationToken>()))
            .ReturnsAsync([persisted]);
        var service = CreateService();

        await service.InitializeAsync();

        Assert.Equal("running", service.Get(persisted.Id)!.Status);
    }

    // The operator snapshot retains both builds and the reason across service restarts.
    [Fact]
    public async Task InitializeAsync_ProjectsExitOnlyBuildIndependentlyOfPipelineHealth()
    {
        var persisted = CreatePersisted(Guid.NewGuid(), "running");
        var attempt = new PaperExitOnlyContinuation(
            persisted.Id, 2, new string('a', 40), new string('b', 40), persisted.CreatedAt, persisted.CreatedAt);
        var snapshot = new BacktestJobSnapshot(
            persisted.Id, persisted.RunName, persisted.ConfigPath, persisted.Status, persisted.CreatedAt,
            persisted.StartedAt, null, null, "running", 0, 0, [], null, PaperExitOnly: attempt);
        persisted.SnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        repository.Setup(candidate => candidate.GetJobsAsync("paper", It.IsAny<CancellationToken>()))
            .ReturnsAsync([persisted]);
        var service = CreateService();

        await service.InitializeAsync();

        var projected = service.Get(persisted.Id);
        Assert.NotNull(projected);
        Assert.Null(projected.PaperPipelineHealth);
        Assert.Equal(attempt, projected.PaperExitOnly);
        var label = TradingFlow.Web.Pages.PaperJobModel.FormatExitOnlyContinuation(projected.PaperExitOnly);
        Assert.Contains(PaperExitOnlyContinuation.Reason, label);
        Assert.Contains($"Executing build {attempt.ExecutingCodeVersion} on lease attempt 2", label);
        Assert.Contains($"Original build {attempt.OriginCodeVersion}", label);
    }

    [Fact]
    public async Task InitializeAsync_ProjectsDurableJobWhenOriginalConfigWasRemoved()
    {
        var persisted = CreatePersisted(Guid.NewGuid(), "failed");
        persisted.ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yaml");
        persisted.ErrorMessage = "The captured configuration is unavailable.";
        repository.Setup(candidate => candidate.PruneTerminalJobsOlderThanAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(candidate => candidate.GetJobsAsync("paper", It.IsAny<CancellationToken>()))
            .ReturnsAsync([persisted]);
        var service = CreateService();

        await service.InitializeAsync();

        var projected = service.Get(persisted.Id);
        Assert.NotNull(projected);
        Assert.Equal("failed", projected.Status);
        Assert.Equal(persisted.ErrorMessage, projected.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_SucceedsWhenConcurrentRefreshProjectsThePersistedJobFirst()
    {
        PersistedJob? persisted = null;
        PaperJobService? service = null;
        repository.Setup(candidate => candidate.PruneTerminalJobsOlderThanAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(candidate => candidate.GetJobsAsync("paper", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted is null ? [] : [persisted]);
        repository.Setup(candidate => candidate.TryEnqueueAsync(
                It.IsAny<PersistedJob>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (PersistedJob job, int _, CancellationToken token) =>
            {
                persisted = job;
                await service!.RefreshProjectionAsync(token);
                return true;
            });
        service = CreateService();

        var started = await service.StartAsync("projection-race", configPath);

        Assert.Equal("queued", started.Status);
        Assert.Equal(persisted!.Id, started.JobId);
        Assert.Single(service.List(), job => job.JobId == persisted.Id);
    }

    [Fact]
    public async Task RecoverAsync_VerifiesFrozenInputsThenRunsBrokerReconciliation()
    {
        var recovery = new Mock<IPaperJobRecoveryService>();
        recovery.Setup(candidate => candidate.RecoverAsync(
                It.IsAny<PersistedJob>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = CreateService(recovery.Object);
        var run = new SimpleYamlReader().ReadBacktestRun(configPath);
        var persisted = CreatePersisted(Guid.NewGuid(), "running");
        persisted.RequestJson = DurableFileJobRequest.Capture([configPath, .. run.Strategies]).Serialize();

        await service.RecoverAsync(persisted, CancellationToken.None);

        recovery.Verify(candidate => candidate.RecoverAsync(
            persisted,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A frozen identity mismatch or failed reconciliation prevents startup, even on a recovered lease.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryWorker_DoesNotRegisterWhenRecoveryFails(bool frozenMismatch)
    {
        var lifecycle = new Mock<IPaperRunLifecycleRepository>(MockBehavior.Strict);
        var recovery = new Mock<IPaperJobRecoveryService>(MockBehavior.Strict);
        var service = CreateService(recovery.Object, lifecycle.Object);
        var persisted = CreatePersisted(Guid.NewGuid(), "running");
        var run = new SimpleYamlReader().ReadBacktestRun(configPath);
        persisted.RequestJson = DurableFileJobRequest.Capture([configPath, .. run.Strategies]).Serialize();
        persisted.AttemptCount = 2;
        var leaseToken = Guid.NewGuid();
        SetOwnedLease(persisted, leaseToken);

        var sequence = new MockSequence();
        lifecycle.InSequence(sequence).Setup(candidate => candidate.ValidatePaperRunAsync(
                It.Is<ProductionRun>(requested => requested.RunId == persisted.Id), It.IsAny<CancellationToken>()))
            .Returns(() => frozenMismatch
                ? Task.FromException(new InvalidOperationException("The frozen run configuration has different provenance."))
                : Task.CompletedTask);
        recovery.InSequence(sequence).Setup(candidate => candidate.RecoverAsync(
                persisted, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic broker timeout."));

        var worker = new DurableJobProcessor(repository.Object);
        Assert.True(await worker.ProcessNextAsync("test-owner", service, DurableJobWorkerOptions.Default, default));

        Assert.Equal("interrupted", persisted.Status);
        Assert.NotNull(persisted.ErrorMessage);
        var expectedError = frozenMismatch
            ? "The frozen run configuration has different provenance. Broker protection recovery also failed: Synthetic broker timeout."
            : "Synthetic broker timeout.";
        Assert.Equal($"Paper job recovery failed: {expectedError}", persisted.ErrorMessage);
        Assert.Equal(persisted.ErrorMessage, service.Get(persisted.Id)?.ErrorMessage);
        lifecycle.Verify(candidate => candidate.ValidatePaperRunAsync(
            It.IsAny<ProductionRun>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.Verify(candidate => candidate.RecoverAsync(persisted, It.IsAny<CancellationToken>()), Times.Once);
        lifecycle.Verify(candidate => candidate.StartPaperRunAsync(
            It.IsAny<ProductionRun>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(candidate => candidate.CompleteAsync(
            persisted.Id, leaseToken, "interrupted", null, null, persisted.ErrorMessage,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A routine progress snapshot carrying a stop uses the same atomic route as the health transition.
    [Fact]
    public async Task PaperSnapshots_AlwaysPersistStoppedHealthWithItsFence()
    {
        var lifecycle = repository.As<IPaperRunLifecycleRepository>();
        var persisted = CreatePersisted(Guid.NewGuid(), "running");
        var leaseToken = Guid.NewGuid();
        SetOwnedLease(persisted, leaseToken);
        var stoppedAt = DateTimeOffset.UtcNow;
        var writes = new List<string>();
        repository.Setup(candidate => candidate.SaveSnapshotAsync(
                persisted.Id, leaseToken, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, DateTimeOffset, CancellationToken>((_, _, json, _, _) =>
            {
                Assert.DoesNotContain("\"entriesStopped\":true", json);
                writes.Add("progress");
            })
            .ReturnsAsync(true);
        lifecycle.Setup(candidate => candidate.SavePaperEntryStopSnapshotAsync(
                persisted.Id, leaseToken, It.IsAny<string>(), stoppedAt, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, DateTimeOffset, DateTimeOffset, CancellationToken>((_, _, json, _, _, _) =>
            {
                Assert.Contains("\"entriesStopped\":true", json);
                writes.Add("atomic stop");
            })
            .ReturnsAsync(true);

        var snapshot = new BacktestJobSnapshot(
            persisted.Id, persisted.RunName, persisted.ConfigPath, "running", persisted.CreatedAt, persisted.StartedAt,
            null, null, "running", 0, 0, [], null);
        var handler = new Mock<IDurableJobHandler>();
        handler.SetupGet(candidate => candidate.JobType).Returns("paper");
        handler.Setup(candidate => candidate.ExecuteAsync(It.IsAny<DurableJobExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (DurableJobExecutionContext context, CancellationToken token) =>
            {
                await PaperJobService.PersistPaperSnapshotAsync(context, snapshot, token);
                var stopped = snapshot with { PaperPipelineHealth = new LivePipelineHealth(3, 3, EntriesStoppedAtUtc: stoppedAt) };
                await PaperJobService.PersistPaperSnapshotAsync(context, stopped, token);
                await PaperJobService.PersistPaperSnapshotAsync(context, stopped with { CompletedTickerCount = 10 }, token);
                return DurableJobCompletion.Completed(context.LatestSnapshotJson, null);
            });

        Assert.True(await new DurableJobProcessor(repository.Object).ProcessNextAsync(
            "test-owner", handler.Object, DurableJobWorkerOptions.Default, default));

        Assert.Equal(["progress", "atomic stop", "atomic stop"], writes);
        Assert.Contains("\"entriesStopped\":true", persisted.SnapshotJson);
    }

    // Keep the worker orchestration real while capturing its durable publications in the fixture.
    private void SetOwnedLease(PersistedJob persisted, Guid token)
    {
        repository.Setup(candidate => candidate.TryAcquireNextAsync(
                persisted.JobType, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DurableJobLease(persisted, token, DateTimeOffset.UtcNow.AddMinutes(1)));
        repository.Setup(candidate => candidate.GetJobAsync(persisted.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(persisted);
        repository.Setup(candidate => candidate.CompleteAsync(
                persisted.Id, token, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string, string?, string?, string?, DateTimeOffset, CancellationToken>(
                (_, _, status, snapshot, _, error, finished, _) =>
                {
                    persisted.Status = status;
                    persisted.SnapshotJson = snapshot;
                    persisted.ErrorMessage = error;
                    persisted.FinishedAt = finished;
                })
            .ReturnsAsync(true);
    }

    [Fact]
    public void ScopeAttemptArtifacts_IsolatesConcurrentAndRecoveredPaperAttempts()
    {
        var logical = new SimpleYamlReader().ReadBacktestRun(configPath);
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        var firstLease = Guid.NewGuid();
        var recoveredLease = Guid.NewGuid();

        var first = PaperJobService.ScopeAttemptArtifacts(logical, jobA, firstLease);
        var recovered = PaperJobService.ScopeAttemptArtifacts(logical, jobA, recoveredLease);
        var concurrent = PaperJobService.ScopeAttemptArtifacts(logical, jobB, firstLease);

        Assert.NotEqual(first.ResultsRoot, recovered.ResultsRoot);
        Assert.NotEqual(first.ResultsRoot, concurrent.ResultsRoot);
        Assert.Equal(logical.ResultsRoot, new SimpleYamlReader().ReadBacktestRun(configPath).ResultsRoot);
        Assert.Contains(jobA.ToString("N"), first.ResultsRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(firstLease.ToString("N"), first.ResultsRoot, StringComparison.OrdinalIgnoreCase);
    }

    // Compose a paper handler using only synthetic recovery and lease persistence.
    private PaperJobService CreateService(
        IPaperJobRecoveryService? recoveryService = null, IPaperRunLifecycleRepository? lifecycle = null)
    {
        var credentials = new AlpacaCredentialProvider(new ConfigurationBuilder().Build());
        var repositoryRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var strategyCatalog = new StrategyArtifactCatalog(
            repositoryRoot,
            Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
            reader);
        var configCatalog = new ConfigCatalogService(
            new ProjectPaths(repositoryRoot),
            reader,
            strategyCatalog,
            new StrategyExperimentArtifactStore(
                Path.Combine(Path.GetTempPath(), "trading-flow-paper-job-experiments", Guid.NewGuid().ToString("N")),
                strategyCatalog,
                reader,
                TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance),
            new StrategyAuthorizationTestRegistry());
        var services = new ServiceCollection().BuildServiceProvider();

        return new PaperJobService(
            reader,
            services.GetRequiredService<IServiceScopeFactory>(),
            credentials,
            new ProjectPaths(repositoryRoot),
            configCatalog: configCatalog,
            durableJobs: repository.Object,
            recoveryService: recoveryService,
            runLifecycle: lifecycle);
    }

    private PersistedJob CreatePersisted(Guid id, string status) => new()
    {
        Id = id,
        JobType = "paper",
        RunName = "paper-test",
        ConfigPath = configPath,
        RequestJson = "{}",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        StartedAt = status == "running" ? DateTimeOffset.UtcNow : null
    };
}
