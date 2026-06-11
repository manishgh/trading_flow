using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Backtesting;
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
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly ProjectPaths paths;
    private readonly ILogger<PaperJobService> logger;
    private readonly ILogger<LiveRunner> liveRunnerLogger;
    private readonly ILogger<TradingFlow.Alpaca.AlpacaNewsProvider> alpacaNewsLogger;
    private readonly IArtifactWriter artifactWriter;

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
        TradingFlow.Domain.Locking.ITickerLockService? lockService = null,
        TradingFlow.Domain.Orders.IOrderStateRepository? orderRepo = null,
        TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo = null)
    {
        this.yamlReader = yamlReader;
        _scopeFactory = scopeFactory;
        this.alpacaCredentials = alpacaCredentials;
        this.paths = paths;
        this.logger = logger ?? NullLogger<PaperJobService>.Instance;
        this.liveRunnerLogger = liveRunnerLogger ?? NullLogger<LiveRunner>.Instance;
        this.alpacaNewsLogger = alpacaNewsLogger ?? NullLogger<TradingFlow.Alpaca.AlpacaNewsProvider>.Instance;
        this.artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
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

            // Auto-resume jobs that were running or queued
            if (pJob.Status is "running" or "queued")
            {
                _ = Task.Run(() => RunAsync(job));
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
        var brokerClient = CreateBrokerClient(runConfig);
        
        if (brokerClient == null) return false;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var success = await brokerClient.CancelAllOrdersAsync(cts.Token);
            if (success)
            {
                job.Report(job.CurrentStage, "All open broker orders have been cancelled successfully.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }
            else
            {
                job.Report(job.CurrentStage, "Broker failed to cancel orders.", null, job.CompletedTickerCount, job.TotalTickerCount);
            }
            return success;
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
        var brokerClient = CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.ActiveBrokerOrder>();
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await brokerClient.GetOpenOrdersAsync(cts.Token);
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
        var brokerClient = CreateBrokerClient(runConfig);
        if (brokerClient == null) return Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await brokerClient.GetOpenPositionsAsync(cts.Token);
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
        var brokerClient = CreateBrokerClient(runConfig);
        if (brokerClient == null) return false;
        
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            var runConfig = ResolveRunPaths(yamlReader.ReadBacktestRun(job.ConfigPath));
            var strategies = runConfig.Strategies.Select(yamlReader.ReadStrategy).ToArray();
            ClearLiveChartSnapshots(runConfig);
            
            var provider = CreateProvider(runConfig);
            var newsProvider = CreateNewsProvider(runConfig);
            var brokerClient = CreateBrokerClient(runConfig);
            
            var runner = new LiveRunner(
                provider, 
                newsProvider, 
                brokerClient,
                _lockService,
                _orderRepo,
                _auditRepo,
                liveRunnerLogger,
                artifactWriter);
            
            var progress = new Progress<string>(msg =>
            {
                job.Report("running", msg, null, 0, 0);
            });

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

    private TradingFlow.Engine.Abstractions.ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled) return null;
        return run.News.ProviderName.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
                new HttpClient(), 
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey,
                    MarketDataFeed = run.Providers.Alpaca.DataFeed
                },
                alpacaNewsLogger,
                CreateSentimentAnalyzer()),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };
    }

    private static TradingFlow.Engine.Abstractions.ISentimentAnalyzer CreateSentimentAnalyzer()
    {
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(new HttpClient(), uri);
        }

        return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
    }

    private TradingFlow.Engine.Execution.IBrokerClient? CreateBrokerClient(BacktestRunConfig run)
    {
        return run.Execution.Broker.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaBrokerClient(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey,
                    TimeInForce = "day".Equals(run.Execution.OrderExpiration, StringComparison.OrdinalIgnoreCase) ? "day" : "gtc",
                    EntryOrderType = run.Execution.EntryOrderType ?? "limit",
                    ExtendedHours = run.Execution.ExtendedHours
                }),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported broker client: {run.Execution.Broker}")
        };
    }

    private TradingFlow.Engine.Abstractions.IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        return run.Provider.ToLowerInvariant() switch
        {
            "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey,
                    MarketDataFeed = run.Providers.Alpaca.DataFeed
                }),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };
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

    private BacktestRunConfig ResolveRunPaths(BacktestRunConfig run)
    {
        return run with
        {
            RawRoot = paths.ResolveRepositoryPath(run.RawRoot),
            NormalizedRoot = paths.ResolveRepositoryPath(run.NormalizedRoot),
            ResultsRoot = paths.ResolveRepositoryPath(run.ResultsRoot)
        };
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
