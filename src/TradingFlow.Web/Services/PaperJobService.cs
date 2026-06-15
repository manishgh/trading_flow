using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class PaperJobService
{
    private readonly ConcurrentDictionary<Guid, MutablePaperJob> jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellationTokens = new();
    private readonly SimpleYamlReader yamlReader;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService;
    private readonly TradingFlow.Domain.Orders.IOrderStateRepository? _orderRepo;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo;
    private readonly PaperRuntimeFactory runtimeFactory;
    private readonly ILogger<PaperJobService> logger;
    private readonly ILogger<LiveRunner> liveRunnerLogger;
    private readonly IArtifactWriter artifactWriter;
    private readonly ICandleStore candleStore;
    private readonly bool autoResumeJobs;

    private readonly IServiceScopeFactory _scopeFactory;

    public PaperJobService(
        SimpleYamlReader yamlReader,
        IServiceScopeFactory scopeFactory,
        AlpacaCredentialProvider alpacaCredentials,
        ProjectPaths paths,
        ILogger<PaperJobService>? logger = null,
        ILogger<LiveRunner>? liveRunnerLogger = null,
        ILogger<TradingFlow.Alpaca.AlpacaNewsProvider>? alpacaNewsLogger = null,
        IArtifactWriter? artifactWriter = null,
        ICandleStore? candleStore = null,
        IConfiguration? configuration = null,
        TradingFlow.Domain.Locking.ITickerLockService? lockService = null,
        TradingFlow.Domain.Orders.IOrderStateRepository? orderRepo = null,
        TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo = null,
        PaperRuntimeFactory? runtimeFactory = null)
    {
        this.yamlReader = yamlReader;
        _scopeFactory = scopeFactory;
        _ = alpacaCredentials;
        _ = paths;
        this.runtimeFactory = runtimeFactory ?? new PaperRuntimeFactory(alpacaCredentials, paths, alpacaNewsLogger);
        this.logger = logger ?? NullLogger<PaperJobService>.Instance;
        this.liveRunnerLogger = liveRunnerLogger ?? NullLogger<LiveRunner>.Instance;
        _ = alpacaNewsLogger ?? NullLogger<TradingFlow.Alpaca.AlpacaNewsProvider>.Instance;
        this.artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
        this.candleStore = candleStore ?? NullCandleStore.Instance;
        autoResumeJobs = configuration?.GetValue<bool>("TradingFlow:Paper:AutoResumeJobs") ?? false;
        _lockService = lockService;
        _orderRepo = orderRepo;
        _auditRepo = auditRepo;
    }

    public async Task InitializeAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<TradingFlow.Domain.Jobs.IJobRepository>();
        var allJobs = await jobRepo.GetAllJobsAsync(default);

        foreach (var pJob in allJobs)
        {
            if (!File.Exists(pJob.ConfigPath))
            {
                continue;
            }

            var job = new MutablePaperJob(pJob.Id, pJob.RunName, pJob.ConfigPath, pJob.CreatedAt)
            {
                Status = pJob.Status,
                StartedAt = pJob.StartedAt,
                FinishedAt = pJob.FinishedAt,
                ErrorMessage = pJob.ErrorMessage
            };
            jobs[job.JobId] = job;

            if (pJob.Status is "running" or "queued")
            {
                if (autoResumeJobs)
                {
                    _ = Task.Run(() => RunAsync(job));
                }
                else
                {
                    job.Status = "interrupted";
                    job.FinishedAt = DateTimeOffset.UtcNow;
                    job.ErrorMessage = "Previous process ended before the job completed. Auto-resume is disabled.";
                    await jobRepo.UpdateJobStatusAsync(job.JobId, "interrupted", job.ErrorMessage, default);
                }
            }
        }
    }

    public Guid StartJob(string configPath, string runName)
    {
        var job = new MutablePaperJob(Guid.NewGuid(), runName, configPath, DateTimeOffset.UtcNow);
        jobs[job.JobId] = job;

        Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobRepo = scope.ServiceProvider.GetRequiredService<TradingFlow.Domain.Jobs.IJobRepository>();
                await jobRepo.SaveJobAsync(new TradingFlow.Domain.Jobs.PersistedJob
                {
                    Id = job.JobId,
                    RunName = job.RunName,
                    ConfigPath = job.ConfigPath,
                    Status = job.Status,
                    CreatedAt = job.CreatedAt
                }, default);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to persist paper job {JobId} for run {RunName} before execution.",
                    job.JobId,
                    job.RunName);
            }
            await RunAsync(job);
        });

        return job.JobId;
    }

    public BacktestJobSnapshot Start(string runName, string configPath)
    {
        var jobId = StartJob(configPath, runName);
        return jobs[jobId].ToSnapshot();
    }

    public void CancelJob(Guid jobId)
    {
        if (jobs.TryGetValue(jobId, out var job) && _cancellationTokens.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            job.Status = "cancelled";
            job.Report("cancelled", "User cancelled the run.", null, 0, 0);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var jobRepo = scope.ServiceProvider.GetRequiredService<TradingFlow.Domain.Jobs.IJobRepository>();
                    await jobRepo.UpdateJobStatusAsync(job.JobId, "cancelled", null, default);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to persist cancelled status for paper job {JobId}.",
                        job.JobId);
                }
            });
        }
    }

    public async Task<bool> CancelAllBrokerOrdersAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return false;

        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);

        if (brokerClient == null) return false;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var openOrders = await brokerClient.GetOpenOrdersAsync(cts.Token);
            var runOrders = openOrders
                .Where(order => BelongsToRun(order, job.RunName))
                .ToArray();

            if (runOrders.Length == 0)
            {
                job.Report(job.CurrentStage, "No open broker orders were found for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
                return true;
            }

            var cancelled = 0;
            foreach (var order in runOrders)
            {
                if (await brokerClient.CancelOrderAsync(order.OrderId, cts.Token))
                {
                    cancelled++;
                }
            }

            if (cancelled == runOrders.Length)
            {
                job.Report(job.CurrentStage, $"Cancelled {cancelled} open broker order(s) for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }
            else
            {
                job.Report(job.CurrentStage, $"Cancelled {cancelled}/{runOrders.Length} open broker order(s) for this run.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }

            return cancelled == runOrders.Length;
        }
        catch (Exception ex)
        {
            job.Report(job.CurrentStage, $"Broker failed to cancel orders: {ex.Message}", null, job.CompletedTickerCount, job.TotalTickerCount);
            return false;
        }
        finally
        {
            if (brokerClient is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.ActiveBrokerOrder>> GetOpenOrdersAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var orders = await brokerClient.GetOpenOrdersAsync(cts.Token);
            return orders
                .Where(order => BelongsToRun(order, job.RunName))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to load open broker orders for paper job {JobId}.",
                jobId);
            return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.BrokerPosition>> GetOpenPositionsAsync(Guid jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();

        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var positions = await brokerClient.GetOpenPositionsAsync(cts.Token);
            var scopedTickers = await GetRunScopedActiveTickersAsync(job, cts.Token);
            if (scopedTickers.Count == 0)
            {
                return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();
            }

            return positions
                .Where(position => scopedTickers.Contains(position.Ticker))
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to load open broker positions for paper job {JobId}.",
                jobId);
            return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public async Task<bool> ClosePositionAsync(Guid jobId, string ticker)
    {
        if (!jobs.TryGetValue(jobId, out var job)) return false;
        var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);
        if (brokerClient == null) return false;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(job.RunName);
            var success = await brokerClient.ClosePositionAsync(ticker, cts.Token);
            if (success)
            {
                job.Report(job.CurrentStage, $"Successfully closed position for {ticker}.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }
            return success;
        }
        catch (Exception ex)
        {
            job.Report(job.CurrentStage, $"Failed to close position for {ticker}: {ex.Message}", null, job.CompletedTickerCount, job.TotalTickerCount);
            return false;
        }
        finally
        {
            if (brokerClient is IDisposable disposable) disposable.Dispose();
        }
    }

    public IReadOnlyList<BacktestJobSnapshot> List()
    {
        return jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Select(job => job.ToSnapshot())
            .ToArray();
    }

    public BacktestJobSnapshot? Get(Guid jobId)
    {
        return jobs.TryGetValue(jobId, out var job) ? job.ToSnapshot() : null;
    }

    private async Task RunAsync(MutablePaperJob job)
    {
        async Task UpdateStatusAsync(string status, string? error = null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobRepo = scope.ServiceProvider.GetRequiredService<TradingFlow.Domain.Jobs.IJobRepository>();
                await jobRepo.UpdateJobStatusAsync(job.JobId, status, error, default);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to update persisted status {Status} for paper job {JobId}.",
                    status,
                    job.JobId);
            }
        }

        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Report("starting", "Paper LiveRunner started.", null, 0, 0);
        await UpdateStatusAsync("running");

        var cts = new CancellationTokenSource();
        _cancellationTokens[job.JobId] = cts;
        try
        {
            var runConfig = runtimeFactory.ResolveRunPaths(yamlReader.ReadBacktestRun(job.ConfigPath));
            var strategies = runConfig.Strategies.Select(yamlReader.ReadStrategy).ToArray();
            ClearLiveChartSnapshots(runConfig);

            var provider = runtimeFactory.CreateProvider(runConfig);
            var newsProvider = runtimeFactory.CreateNewsProvider(runConfig);
            var brokerClient = runtimeFactory.CreateBrokerClient(runConfig);

            var runner = new LiveRunner(
                provider,
                newsProvider,
                brokerClient,
                _lockService,
                _orderRepo,
                _auditRepo,
                liveRunnerLogger,
                artifactWriter,
                candleStore);

            var progress = new Progress<string>(msg =>
            {
                job.Report("running", msg, null, 0, 0);
            });

            using var profilerScope = TradingFlow.Domain.Logging.ApiProfiler.BeginScope(runConfig.RunName);
            await runner.RunAsync(runConfig, strategies, cts.Token, progress);
            if (job.Status != "cancelled")
            {
                job.Status = "completed";
                await UpdateStatusAsync("completed");
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            await UpdateStatusAsync("cancelled");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Paper job {JobId} for run {RunName} failed.",
                job.JobId,
                job.RunName);
            job.ErrorMessage = exception.Message;
            job.Status = "failed";
            await UpdateStatusAsync("failed", exception.Message);
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            _cancellationTokens.TryRemove(job.JobId, out _);
        }
    }

    private void ClearLiveChartSnapshots(BacktestRunConfig run)
    {
        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName);
        if (!Directory.Exists(resultsDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(resultsDir, "*_chart.json", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to delete stale chart snapshot {ChartSnapshotPath}; the next writer may replace it.",
                    file);
            }
        }
    }

    private async Task<HashSet<string>> GetRunScopedActiveTickersAsync(MutablePaperJob job, CancellationToken cancellationToken)
    {
        var tickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_orderRepo is null)
        {
            return tickers;
        }

        BacktestRunConfig runConfig;
        try
        {
            runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
        }
        catch
        {
            return tickers;
        }

        foreach (var ticker in runConfig.Tickers)
        {
            var orders = await _orderRepo.GetActiveOrdersByTickerAsync(ticker, cancellationToken);
            if (orders.Any(order => order.RunName.Equals(job.RunName, StringComparison.OrdinalIgnoreCase)))
            {
                tickers.Add(ticker);
            }
        }

        return tickers;
    }

    private static bool BelongsToRun(TradingFlow.Domain.Orders.ActiveBrokerOrder order, string runName)
    {
        return order.ClientOrderId.StartsWith(ClientOrderIdFactory.CreatePrefix(runName), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MutablePaperJob
    {
        public MutablePaperJob(Guid jobId, string runName, string configPath, DateTimeOffset createdAt)
        {
            JobId = jobId; RunName = runName; ConfigPath = configPath; CreatedAt = createdAt; Status = "queued";
        }
        public Guid JobId { get; }
        public string RunName { get; }
        public string ConfigPath { get; }
        public string Status { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string CurrentStage { get; private set; } = "queued";
        public int CompletedTickerCount { get; private set; }
        public int TotalTickerCount { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();

        public void Report(string stage, string message, string? ticker, int completedTickerCount, int totalTickerCount)
        {
            CurrentStage = stage; CompletedTickerCount = completedTickerCount; TotalTickerCount = totalTickerCount;
            var tickerText = String.IsNullOrWhiteSpace(ticker) ? String.Empty : $" [{ticker}]";
            Events.Enqueue($"{UiDisplayFormatter.FormatLocalTime(DateTimeOffset.UtcNow)} {stage}{tickerText}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _)) { }
        }

        public BacktestJobSnapshot ToSnapshot() => new(JobId, RunName, ConfigPath, Status, CreatedAt, StartedAt, FinishedAt, ErrorMessage, CurrentStage, CompletedTickerCount, TotalTickerCount, Events.ToArray(), null);
    }
}
