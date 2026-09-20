using TradingFlow.Application.Jobs;
using TradingFlow.Domain.Jobs;

namespace TradingFlow.Web.Services;

public sealed record DurableJobHostOptions(TimeSpan ShutdownDrainTimeout)
{
    public static DurableJobHostOptions Default { get; } = new(TimeSpan.FromSeconds(10));

    public DurableJobHostOptions Validate()
    {
        if (ShutdownDrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownDrainTimeout));
        }

        return this;
    }
}

/// <summary>
/// Owns all durable run workers as host-managed children. Database rows are the
/// waiting queue; this service creates only the configured fixed worker count.
/// </summary>
public sealed class DurableJobWorkerHostedService(
    IDurableJobRepository repository,
    IEnumerable<IDurableJobHandler> handlers,
    IReadOnlyDictionary<string, DurableJobWorkerOptions> optionsByJobType,
    DurableJobHostOptions hostOptions,
    TimeProvider timeProvider,
    ILogger<DurableJobWorkerHostedService> logger) : BackgroundService
{
    private readonly DurableJobHostOptions validatedHostOptions = hostOptions.Validate();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registeredHandlers = handlers.ToArray();
        foreach (var handler in registeredHandlers)
        {
            await handler.InitializeAsync(stoppingToken);
        }

        var processor = new DurableJobProcessor(repository, timeProvider);
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var workers = registeredHandlers
            .SelectMany(handler => CreateWorkers(processor, handler, instanceId, stoppingToken))
            .ToArray();
        var projectionRefreshers = registeredHandlers
            .Select(handler => RefreshProjectionAsync(
                handler,
                ResolveOptions(handler).ProjectionRefreshInterval,
                stoppingToken))
            .ToArray();

        logger.LogInformation(
            "Started {WorkerCount} durable job worker(s) for {JobTypes}.",
            workers.Length,
            String.Join(", ", registeredHandlers.Select(handler => handler.JobType)));
        await Task.WhenAll(workers.Concat(projectionRefreshers));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var drainBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainBudget.CancelAfter(validatedHostOptions.ShutdownDrainTimeout);
        await base.StopAsync(drainBudget.Token);
        if (drainBudget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogCritical(
                "Durable job workers did not drain within {DrainTimeout}; continuing host shutdown so broker protection and persistence can use the remaining shutdown budget.",
                validatedHostOptions.ShutdownDrainTimeout);
        }
    }

    private IEnumerable<Task> CreateWorkers(
        DurableJobProcessor processor,
        IDurableJobHandler handler,
        string instanceId,
        CancellationToken stoppingToken)
    {
        var options = ResolveOptions(handler);
        for (var worker = 0; worker < options.MaximumConcurrentWorkers; worker++)
        {
            var ownerId = $"{instanceId}:{handler.JobType}:{worker + 1}";
            yield return RunWorkerWithLoggingAsync(processor, ownerId, handler, options, stoppingToken);
        }
    }

    private DurableJobWorkerOptions ResolveOptions(IDurableJobHandler handler)
    {
        if (!optionsByJobType.TryGetValue(handler.JobType, out var options))
        {
            throw new InvalidOperationException(
                $"No durable worker options are registered for job type '{handler.JobType}'.");
        }

        return options.Validate();
    }

    private async Task RefreshProjectionAsync(
        IDurableJobHandler handler,
        TimeSpan interval,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await handler.RefreshProjectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Durable {JobType} projection refresh failed; the next scheduled cycle will retry it.",
                    handler.JobType);
            }
        }
    }

    private async Task RunWorkerWithLoggingAsync(
        DurableJobProcessor processor,
        string ownerId,
        IDurableJobHandler handler,
        DurableJobWorkerOptions options,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.RunWorkerAsync(ownerId, handler, options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Durable worker {OwnerId} stopped after draining its handler.", ownerId);
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Durable worker {OwnerId} failed; restarting after {RetryDelay}.",
                    ownerId,
                    options.IdlePollInterval);
                await Task.Delay(options.IdlePollInterval, stoppingToken);
            }
        }
    }
}
