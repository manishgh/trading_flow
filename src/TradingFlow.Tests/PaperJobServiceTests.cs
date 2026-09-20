using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TradingFlow.Application.Jobs;
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

    private PaperJobService CreateService(IPaperJobRecoveryService? recoveryService = null)
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
            recoveryService: recoveryService);
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
