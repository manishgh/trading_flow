using System.Collections.Concurrent;
using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;
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

    private readonly IServiceScopeFactory _scopeFactory;

    public PaperJobService(
        SimpleYamlReader yamlReader,
        IServiceScopeFactory scopeFactory,
        TradingFlow.Domain.Locking.ITickerLockService? lockService = null,
        TradingFlow.Domain.Orders.IOrderStateRepository? orderRepo = null,
        TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo = null)
    {
        this.yamlReader = yamlReader;
        _scopeFactory = scopeFactory;
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
            catch { }
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
                } catch { }
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
        catch
        {
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
        catch
        {
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
            catch { }
        }

        job.Status = "running";
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Report("starting", "Paper LiveRunner started.", null, 0, 0);
        await UpdateStatusAsync("running");
        
        var cts = new CancellationTokenSource();
        _cancellationTokens[job.JobId] = cts;
        try
        {
            var runConfig = yamlReader.ReadBacktestRun(job.ConfigPath);
            var strategies = runConfig.Strategies.Select(yamlReader.ReadStrategy).ToArray();
            
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
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveRunner>.Instance);
            
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
            Console.WriteLine($"JOB FAILED: {exception}");
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
                    KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                    SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? ""
                }),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };
    }

    private TradingFlow.Engine.Execution.IBrokerClient? CreateBrokerClient(BacktestRunConfig run)
    {
        return run.Execution.Broker.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaBrokerClient(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                    SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? "",
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
        var etoroOptions = TradingFlow.Etoro.Configuration.EtoroOptions.CreateDefault(TradingFlow.Etoro.Configuration.EtoroEnvironment.Demo) with
        {
            Demo = new TradingFlow.Etoro.Configuration.EtoroCredentialProfile("ETORO_DEMO_API_KEY", "ETORO_DEMO_USER_KEY", false)
        };
        var etoroCreds = new TradingFlow.Etoro.Authentication.EtoroCredentialsProvider(etoroOptions);
        var etoroRateLimiter = new TradingFlow.Etoro.Http.EtoroRateLimiter(etoroOptions.RateLimits);

        return run.Provider.ToLowerInvariant() switch
        {
            "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
            "yahoo" => new TradingFlow.Data.Yahoo.YahooFinanceProvider(
                TradingFlow.Data.Yahoo.YahooFinanceProvider.CreateBrowserLikeClient(run.Providers.Yahoo),
                run.Providers.Yahoo, run.RawRoot, run.NormalizedRoot, run.CachePolicy),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                    SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? ""
                }),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "etoro" => new TradingFlow.Etoro.MarketData.EtoroMarketDataProvider(
                new TradingFlow.Etoro.Http.EtoroApiClient(new HttpClient(), etoroOptions, etoroCreds, etoroRateLimiter),
                new TradingFlow.Etoro.MarketData.EtoroInstrumentResolver(new TradingFlow.Etoro.Http.EtoroApiClient(new HttpClient(), etoroOptions, etoroCreds, etoroRateLimiter))
            ),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
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
            Events.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {stage}{tickerText}: {message}");
            while (Events.Count > 80 && Events.TryDequeue(out _)) { }
        }

        public BacktestJobSnapshot ToSnapshot() => new(JobId, RunName, ConfigPath, Status, CreatedAt, StartedAt, FinishedAt, ErrorMessage, CurrentStage, CompletedTickerCount, TotalTickerCount, Events.ToArray(), null);
    }
}
