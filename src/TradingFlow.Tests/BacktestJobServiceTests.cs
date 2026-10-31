using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using TradingFlow.Data.Evidence.Historical;

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

    /// <summary>Builds a resolved synthetic stock request in an isolated copied profile so ordinary admission checks remain enabled.</summary>
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
        configText = configText.Replace("mode: unresolved_wishlist", "mode: resolved_snapshot", StringComparison.Ordinal)
            + Environment.NewLine + "tickers:" + Environment.NewLine + "  - FIXTURE" + Environment.NewLine;
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

    /// <summary>Exercise the real backtest handler and lease queue under the paused allow-list without activating other handlers.</summary>
    [Fact]
    public async Task PausedHost_CompletesHistoricalBacktest_AndLeavesOtherJobTypesQueued()
    {
        var executor = new ImmediateBacktestExecutor();
        var backtests = CreateService(executor);
        var started = await backtests.StartAsync("paused-historical-backtest", configPath);
        var excludedHandlers = new List<Mock<IDurableJobHandler>>();
        var excludedJobIds = new List<Guid>();
        foreach (var jobType in new[] { "paper", "optimization", "preparation" })
        {
            var job = new PersistedJob
            {
                Id = Guid.NewGuid(), JobType = jobType, RunName = $"paused-{jobType}",
                ConfigPath = "unused.yaml", RequestJson = "{}", Status = "queued", CreatedAt = DateTimeOffset.UtcNow
            };
            Assert.True(await repository.TryEnqueueAsync(job, 16, CancellationToken.None));
            excludedJobIds.Add(job.Id);
            var handler = new Mock<IDurableJobHandler>(MockBehavior.Strict);
            handler.SetupGet(item => item.JobType).Returns(jobType);
            excludedHandlers.Add(handler);
        }

        var workerOptions = DurableJobWorkerOptions.Default with
        {
            IdlePollInterval = TimeSpan.FromMilliseconds(10),
            ProjectionRefreshInterval = TimeSpan.FromSeconds(1)
        };
        using var worker = new DurableJobWorkerHostedService(
            repository,
            new IDurableJobHandler[] { backtests }.Concat(excludedHandlers.Select(item => item.Object)),
            new Dictionary<string, DurableJobWorkerOptions> { ["backtest"] = workerOptions },
            new DurableJobHostOptions(TimeSpan.FromSeconds(2), new HashSet<string>(
                [TradingFlow.Web.Services.Desk.CurrentStockStrategyAssessmentService.Type, "backtest"], StringComparer.Ordinal)),
            TimeProvider.System,
            NullLogger<DurableJobWorkerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            PersistedJob? completed;
            do
            {
                completed = await repository.GetJobAsync(started.JobId, timeout.Token);
                if (completed?.Status is "completed" or "failed") break;
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            } while (true);

            Assert.NotNull(completed);
            Assert.Equal("completed", completed.Status);
            Assert.Equal(1, completed.AttemptCount);
            Assert.Equal(1, executor.ExecutionCount);
            foreach (var id in excludedJobIds)
            {
                var queued = await repository.GetJobAsync(id, timeout.Token);
                Assert.Equal("queued", queued!.Status);
                Assert.Equal(0, queued.AttemptCount);
                Assert.Null(queued.LeaseToken);
            }
            foreach (var handler in excludedHandlers)
            {
                handler.Verify(item => item.InitializeAsync(It.IsAny<CancellationToken>()), Times.Never);
                handler.Verify(item => item.ExecuteAsync(It.IsAny<DurableJobExecutionContext>(), It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
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

    /// <summary>Cancellation keeps the durable lease nonterminal while the runner drains, even after cancellation bookkeeping settles.</summary>
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

        try
        {
            await Task.WhenAny(processing, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(BacktestCancellationOutcome.Accepted, outcome);
            Assert.Equal("cancelling", (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.Status);
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            executor.AllowDrain.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal("cancelled", service.Get(started.JobId)!.Status);
        Assert.Equal("cancelled", (await repository.GetJobAsync(started.JobId, CancellationToken.None))!.Status);
    }

    /// <summary>The actual diagnostic worker scope keeps its guard and original-source lease alive until a cancelled child drains.</summary>
    [Fact]
    public async Task DiagnosticCancellation_HoldsVerificationScopeUntilRunnerDrainsAndThenDisposes()
    {
        using var news = new HistoricalDiagnosticNewsTests.Archive();
        var publication = await HistoricalDiagnosticNewsTests.PublishAssessmentDiagnosticAsync(news);
        var provider = HistoricalDiagnosticNewsProvider.LoadVerified(news.Root, publication.Path, publication.Sha256);
        var text = await File.ReadAllTextAsync(configPath);
        text = text.Replace("engine:", $"engine:\n  evidence_research_root: {news.Root.Replace('\\', '/')}\n" +
            $"  evidence_manifest_path: {publication.Path.Replace('\\', '/')}\n  evidence_manifest_sha256: {publication.Sha256}\n" +
            $"  evidence_dependency_graph_sha256: {provider.DependencyGraphSha256}", StringComparison.Ordinal);
        await File.WriteAllTextAsync(configPath, text);
        var executor = new BlockingBacktestExecutor();
        var service = CreateService(executor);
        var started = await service.StartAsync("diagnostic-cancel-drain", configPath);
        var processing = ProcessOneAsync(service);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var verified = Assert.IsType<FrozenBacktestEvidence>(executor.ScopedEvidence);
            var guard = Assert.IsType<Action<EngineConfig>>(executor.ScopeGuard);
            var engine = new SimpleYamlReader().ReadBacktestRun(configPath).Engine;
            Assert.Same(verified, FrozenBacktestEvidence.FromConfig(engine, verified, guard));
            Assert.Equal(BacktestCancellationOutcome.Accepted, await service.CancelJobAsync(started.JobId));
            await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            guard(engine);
            Assert.False(processing.IsCompleted);
            Assert.Equal("cancelling", (await repository.GetJobAsync(started.JobId, default))!.Status);
            var source = Path.Combine(news.Root, "datasets", "scope-company.json");
            if (OperatingSystem.IsWindows())
                Assert.ThrowsAny<IOException>(() => { using var attemptedWrite = File.Open(source, FileMode.Open, FileAccess.Write, FileShare.None); });
            executor.AllowDrain.TrySetResult();
            Assert.True(await processing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("cancelled", (await repository.GetJobAsync(started.JobId, default))!.Status);
            Assert.Throws<ObjectDisposedException>(() => FrozenBacktestEvidence.FromConfig(engine, verified, guard));
            using var released = File.Open(source, FileMode.Open, FileAccess.Write, FileShare.None);
            Assert.True(released.CanWrite);
        }
        finally
        {
            // An earlier assertion may precede cancellation; always stop the owned worker before releasing fixture files.
            try { await service.CancelJobAsync(started.JobId); }
            finally
            {
                executor.AllowDrain.TrySetResult();
                await processing.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>A rejected legacy admission removes its unique parameter file while retaining the first accepted job's inputs.</summary>
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
        var retained = Assert.Single(await repository.GetJobsAsync("backtest", CancellationToken.None));
        var reference = DurableFileJobRequest.Deserialize(retained.RequestJson).PreparedMarketData!;
        Assert.Equal(reference.Path, Assert.Single(Directory.GetFiles(Path.Combine(testRoot, "results", "_prepared-requests"),
            "request.json", SearchOption.AllDirectories)));
    }

    /// <summary>Cancellation after request publication releases rejected staging even though the caller's cancellation token is already set.</summary>
    [Fact]
    public async Task CanceledAdmission_DiscardsOnlyUnacceptedPreparedRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var durable = new Mock<IDurableJobRepository>();
        durable.Setup(candidate => candidate.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PersistedJob?)null);
        durable.Setup(candidate => candidate.GetAllJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PersistedJob>());
        durable.Setup(candidate => candidate.TryEnqueueAsync(It.IsAny<PersistedJob>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((PersistedJob _, int _, CancellationToken token) => {
                cancellation.Cancel();
                return Task.FromCanceled<bool>(token);
            });
        var service = CreateService(new ImmediateBacktestExecutor(), durableRepository: durable.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync("canceled-admission", configPath, cancellation.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(testRoot, "results", "_prepared-requests"), "request.json", SearchOption.AllDirectories));
        Assert.True(File.Exists(configPath));
        durable.Verify(candidate => candidate.GetAllJobsAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>An uncertain enqueue error cannot delete a request that the durable repository already accepted.</summary>
    [Fact]
    public async Task EnqueueErrorAfterDurableCommit_PreservesPreparedRequest()
    {
        PersistedJob? stored = null;
        var durable = new Mock<IDurableJobRepository>();
        durable.Setup(candidate => candidate.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        durable.Setup(candidate => candidate.TryEnqueueAsync(It.IsAny<PersistedJob>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((PersistedJob job, int _, CancellationToken _) => {
                stored = job;
                return Task.FromException<bool>(new IOException("Synthetic acknowledgement failure after durable commit."));
            });
        var service = CreateService(new ImmediateBacktestExecutor(), durableRepository: durable.Object);
        await Assert.ThrowsAsync<IOException>(() => service.StartAsync("uncertain-admission", configPath));
        var reference = DurableFileJobRequest.Deserialize(stored!.RequestJson).PreparedMarketData!;
        Assert.True(File.Exists(reference.Path));
        Assert.True(File.Exists(configPath));
    }

    /// <summary>The legacy entry point rejects malformed exact query shapes before creating parameter files or durable jobs.</summary>
    [Fact]
    public async Task InvalidSourceQuery_IsRejectedBeforePreparedRequestPublication()
    {
        var text = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(configPath, text.Replace("  - FIXTURE", "  - " + new string('X', 33), StringComparison.Ordinal));
        var service = CreateService(new ImmediateBacktestExecutor());
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync("invalid-source", configPath));
        Assert.Empty(await repository.GetAllJobsAsync(default));
        Assert.False(Directory.Exists(Path.Combine(testRoot, "results", "_prepared-requests")));
    }

    /// <summary>Ordinary local CSV inputs remain admitted with their declared file evidence and no verified provider snapshot claim.</summary>
    [Fact]
    public async Task CsvAdmission_DoesNotCreatePreparedRequestOrSnapshotClaim()
    {
        var text = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(configPath, text.Replace("  provider: alpaca", "  provider: csv", StringComparison.Ordinal));
        var started = await CreateService(new ImmediateBacktestExecutor()).StartAsync("local-csv", configPath);
        var stored = await repository.GetJobAsync(started.JobId, default);
        Assert.Null(DurableFileJobRequest.Deserialize(stored!.RequestJson).PreparedMarketData);
        Assert.False(Directory.Exists(Path.Combine(testRoot, "results", "_prepared-requests")));
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
    // Compact monthly evidence must survive trade trimming and durable service restart.
    public async Task Completion_PersistsMonthlyResultsBeforeTrimmingAndReloadsThem()
    {
        var fixture = MonthlyRunResultWriterTests.CreateFixture();
        var text = (await File.ReadAllTextAsync(configPath)).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("  type: rolling", "  type: fixed", StringComparison.Ordinal)
            .Replace("  start:", "  start: 2026-01-07T05:00:00Z", StringComparison.Ordinal)
            .Replace("  end:", "  end: 2026-02-03T05:00:00Z", StringComparison.Ordinal)
            .Replace("  starting_capital: 10000", "  starting_capital: 1000", StringComparison.Ordinal)
            .Replace("  - strategies/swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml",
                "  - " + fixture.Config.Strategies[0].Replace('\\', '/'), StringComparison.Ordinal);
        await File.WriteAllTextAsync(configPath, text);
        var executor = new ResultBacktestExecutor(fixture.Result);
        var service = CreateService(executor);
        var started = await service.StartAsync("Volatile group 1", configPath);

        await ProcessOneAsync(service);

        var completed = service.Get(started.JobId)!;
        Assert.Equal("completed", completed.Status);
        Assert.NotNull(completed.MonthlyResults);
        Assert.Null(completed.MonthlyUnavailableReason);
        Assert.Equal(2, completed.MonthlyResults.Count);
        Assert.All(completed.Result!.StrategyResults, strategy => Assert.Empty(strategy.CompletedTrades));
        Assert.Equal(10m, completed.MonthlyResults[0].ReturnPct);
        Assert.Equal(-10m, completed.MonthlyResults[1].ReturnPct);
        Assert.All(completed.MonthlyResults, row => Assert.Equal("Volatile group 1", row.CohortName));
        Assert.True(File.Exists(Path.ChangeExtension(completed.Result.ResultPath, ".monthly.json")));

        var reloaded = CreateService(executor);
        await reloaded.InitializeAsync(default);
        Assert.Equal(completed.MonthlyResults, reloaded.Get(started.JobId)!.MonthlyResults);
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

    // Reuse the source catalog to validate exact strategy identities before monthly publication.
    private BacktestJobService CreateService(
        IBacktestRunExecutor executor,
        BacktestJobServiceOptions? options = null,
        IDurableJobRepository? durableRepository = null) =>
        new(executor, durableRepository ?? repository, new SimpleYamlReader(), options ?? BacktestJobServiceOptions.Default,
            new StrategyArtifactCatalog(TestRepository.FindRoot(),
                Path.Combine(TestRepository.FindRoot(), "configs", "strategy-catalog.json"), new SimpleYamlReader()));

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
        public FrozenBacktestEvidence? ScopedEvidence { get; private set; }
        public Action<EngineConfig>? ScopeGuard { get; private set; }

        /// <summary>Records the actual worker scope, checks its binding, and keeps the same cancellation/drain behavior as the existing executor.</summary>
        public Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
            CancellationToken cancellationToken, FrozenBacktestEvidence verified, Action<EngineConfig> requireLiveScope,
            IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null)
        {
            ScopedEvidence = verified;
            ScopeGuard = requireLiveScope;
            Assert.Same(verified, FrozenBacktestEvidence.FromConfig(new SimpleYamlReader().ReadBacktestRun(configPath).Engine,
                verified, requireLiveScope));
            return RunAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, progress, preparedDataReference);
        }

        public async Task<BacktestResult> RunAsync(
            string configPath,
            string resultPath,
            DateTimeOffset evaluationCutoffUtc,
            CancellationToken cancellationToken,
            IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null)
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

    private sealed class ResultBacktestExecutor(BacktestResult result) : IBacktestRunExecutor
    {
        // Return deterministic completed trades while writing only inside the durable lease's assigned attempt directory.
        public Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
            CancellationToken cancellationToken, IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, "{}");
            return Task.FromResult(result with { ResultPath = resultPath });
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
            IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null)
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
