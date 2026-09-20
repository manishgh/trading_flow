using Microsoft.EntityFrameworkCore;
using TradingFlow.Backtesting;
using TradingFlow.Data.Context;
using TradingFlow.Data.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OptimizationJobServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-optimization-service-{Guid.NewGuid():N}.db");
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"trading-flow-optimization-service-files-{Guid.NewGuid():N}");
    private SqliteJobRepository repository = null!;
    private string optimizationPath = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestContextFactory(options);
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        repository = new SqliteJobRepository(factory);

        var sourceConfigs = Path.Combine(TestRepository.FindRoot(), "configs");
        var copiedConfigs = Path.Combine(testRoot, "configs");
        CopyDirectory(sourceConfigs, copiedConfigs);
        optimizationPath = Path.Combine(copiedConfigs, "optimization.yaml");
        await File.WriteAllTextAsync(optimizationPath, """
run_name: durable-optimization-test
base_strategy: strategies/minervini-trend-template-vcp.v4-trend-rider.yaml
backtest_config: backtest/swing-backtest-profile.yaml
metric: TotalReturnPct
top_n_results: 1
parameters:
  exit_rules.target_r_multiple:
    - 2.0
""");
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PersistsImmutableQueuedRequestWithoutRunningOptimization()
    {
        var service = CreateService(OptimizationJobServiceOptions.Default);

        var started = await service.StartAsync("durable-optimization", optimizationPath);

        Assert.Equal("queued", started.Status);
        var persisted = await repository.GetJobAsync(started.JobId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("optimization", persisted.JobType);
        Assert.Equal(Path.GetFullPath(optimizationPath), persisted.ConfigPath);
        Assert.Contains("capturedAtUtc", persisted.RequestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_EnforcesDurableQueueCapacity()
    {
        var service = CreateService(OptimizationJobServiceOptions.Default with { MaximumPendingJobs = 1 });
        await service.StartAsync("first-optimization", optimizationPath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync("second-optimization", optimizationPath));

        Assert.Contains("maximum of 1", error.Message, StringComparison.Ordinal);
    }

    private OptimizationJobService CreateService(OptimizationJobServiceOptions options)
    {
        var reader = new SimpleYamlReader();
        var repositoryRoot = TestRepository.FindRoot();
        var runner = new BacktestRunner(
            reader,
            new StrategyArtifactCatalog(
                repositoryRoot,
                Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
                reader));
        return new OptimizationJobService(reader, runner, repository, options);
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

    private sealed class TestContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
        public Task<TradingFlowDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
