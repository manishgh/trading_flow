using TradingFlow.Domain.Jobs;

namespace TradingFlow.Application.Jobs;

public enum DurableJobRecoveryMode
{
    ReplayFromStart,
    ReconcileBrokerExposure
}

public sealed record DurableJobWorkerOptions(
    int MaximumPendingJobs,
    int MaximumConcurrentWorkers,
    TimeSpan LeaseDuration,
    TimeSpan HeartbeatInterval,
    TimeSpan IdlePollInterval,
    TimeSpan ProjectionRefreshInterval)
{
    public static DurableJobWorkerOptions Default { get; } = new(
        MaximumPendingJobs: 32,
        MaximumConcurrentWorkers: 1,
        LeaseDuration: TimeSpan.FromSeconds(30),
        HeartbeatInterval: TimeSpan.FromSeconds(10),
        IdlePollInterval: TimeSpan.FromMilliseconds(250),
        ProjectionRefreshInterval: TimeSpan.FromSeconds(1));

    public DurableJobWorkerOptions Validate()
    {
        if (MaximumPendingJobs <= 0 || MaximumConcurrentWorkers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingJobs));
        }

        if (LeaseDuration <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero ||
            IdlePollInterval <= TimeSpan.Zero || ProjectionRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }

        if (HeartbeatInterval >= LeaseDuration)
        {
            throw new ArgumentException("HeartbeatInterval must be shorter than LeaseDuration.", nameof(HeartbeatInterval));
        }

        return this;
    }
}

public sealed record DurableJobCompletion(
    string Status,
    string? SnapshotJson = null,
    string? ResultReference = null,
    string? ErrorMessage = null)
{
    public static DurableJobCompletion Completed(string? snapshotJson, string? resultReference) =>
        new("completed", snapshotJson, resultReference);
}

public interface IDurableJobHandler
{
    string JobType { get; }
    DurableJobRecoveryMode RecoveryMode { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task RefreshProjectionAsync(CancellationToken cancellationToken) => InitializeAsync(cancellationToken);

    Task RecoverAsync(PersistedJob job, CancellationToken cancellationToken);

    Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken);

    Task OnTerminalPublishedAsync(PersistedJob job, CancellationToken cancellationToken);
}

public sealed class DurableJobExecutionContext
{
    private readonly IDurableJobRepository repository;
    private string? latestSnapshotJson;

    internal DurableJobExecutionContext(
        PersistedJob job,
        Guid leaseToken,
        IDurableJobRepository repository,
        TimeProvider timeProvider)
    {
        Job = job;
        LeaseToken = leaseToken;
        this.repository = repository;
        TimeProvider = timeProvider;
        latestSnapshotJson = job.SnapshotJson;
    }

    public PersistedJob Job { get; }
    public Guid LeaseToken { get; }
    public TimeProvider TimeProvider { get; }
    public string? LatestSnapshotJson => Volatile.Read(ref latestSnapshotJson);

    public async Task SaveSnapshotAsync(string snapshotJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        var saved = await repository.SaveSnapshotAsync(
            Job.Id,
            LeaseToken,
            snapshotJson,
            TimeProvider.GetUtcNow(),
            cancellationToken);
        if (!saved)
        {
            throw new DurableJobLeaseLostException(Job.Id);
        }

        Volatile.Write(ref latestSnapshotJson, snapshotJson);
    }
}

public sealed class DurableJobLeaseLostException(Guid jobId)
    : InvalidOperationException($"Durable job {jobId:N} lost its execution lease.")
{
}

/// <summary>
/// Claims and executes durable jobs without retaining a second in-memory queue. The
/// repository lease token is the publication fence, and cancellation becomes terminal
/// only after the handler has observed cancellation and returned.
/// </summary>
public sealed class DurableJobProcessor
{
    private readonly IDurableJobRepository repository;
    private readonly TimeProvider timeProvider;

    public DurableJobProcessor(IDurableJobRepository repository, TimeProvider? timeProvider = null)
    {
        this.repository = repository;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunWorkerAsync(
        string ownerId,
        IDurableJobHandler handler,
        DurableJobWorkerOptions options,
        CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(handler);
        options = options.Validate();

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessNextAsync(ownerId, handler, options, stoppingToken);
            if (!processed)
            {
                await Task.Delay(options.IdlePollInterval, stoppingToken);
            }
        }
    }

    public async Task<bool> ProcessNextAsync(
        string ownerId,
        IDurableJobHandler handler,
        DurableJobWorkerOptions options,
        CancellationToken stoppingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(handler);
        options = options.Validate();

        var lease = await repository.TryAcquireNextAsync(
            handler.JobType,
            ownerId,
            timeProvider.GetUtcNow(),
            options.LeaseDuration,
            stoppingToken);
        if (lease is null)
        {
            return false;
        }

        var context = new DurableJobExecutionContext(
            lease.Job,
            lease.LeaseToken,
            repository,
            timeProvider);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var monitorCancellation = new CancellationTokenSource();
        var leaseOwnershipUncertain = 0;
        var monitor = MonitorLeaseAndCancellationAsync(
            lease,
            options,
            executionCancellation,
            monitorCancellation.Token,
            () => Interlocked.Exchange(ref leaseOwnershipUncertain, 1));

        async Task<bool> StopMonitorAndConfirmOwnershipAsync()
        {
            monitorCancellation.Cancel();
            try
            {
                await monitor;
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
            }

            return Volatile.Read(ref leaseOwnershipUncertain) == 0;
        }

        try
        {
            if (lease.Job.AttemptCount > 1 && handler.RecoveryMode == DurableJobRecoveryMode.ReconcileBrokerExposure)
            {
                try
                {
                    await handler.RecoverAsync(lease.Job, executionCancellation.Token);
                }
                catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (!await StopMonitorAndConfirmOwnershipAsync())
                    {
                        return true;
                    }

                    await PublishTerminalAsync(
                        lease,
                        handler,
                        new DurableJobCompletion(
                            "interrupted",
                            context.LatestSnapshotJson,
                            ErrorMessage: $"Broker-exposure recovery failed: {exception.Message}"),
                        CancellationToken.None);
                    return true;
                }
            }

            if (lease.Job.CancellationRequestedAtUtc is not null)
            {
                if (!await StopMonitorAndConfirmOwnershipAsync())
                {
                    return true;
                }

                await PublishTerminalAsync(
                    lease,
                    handler,
                    new DurableJobCompletion("cancelled", context.LatestSnapshotJson),
                    CancellationToken.None);
                return true;
            }

            var completion = await handler.ExecuteAsync(context, executionCancellation.Token);
            var stored = await repository.GetJobAsync(lease.Job.Id, CancellationToken.None);
            if (stored?.CancellationRequestedAtUtc is not null)
            {
                completion = new DurableJobCompletion("cancelled", context.LatestSnapshotJson);
            }

            if (!await StopMonitorAndConfirmOwnershipAsync())
            {
                return true;
            }

            await PublishTerminalAsync(lease, handler, completion, CancellationToken.None);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            if (!await StopMonitorAndConfirmOwnershipAsync())
            {
                return true;
            }

            var stored = await repository.GetJobAsync(lease.Job.Id, CancellationToken.None);
            if (stored?.CancellationRequestedAtUtc is not null)
            {
                await PublishTerminalAsync(
                    lease,
                    handler,
                    new DurableJobCompletion("cancelled", context.LatestSnapshotJson),
                    CancellationToken.None);
            }
            else
            {
                await repository.ReleaseLeaseAsync(lease.Job.Id, lease.LeaseToken, CancellationToken.None);
            }
        }
        catch (DurableJobLeaseLostException)
        {
            // A newer owner holds the publication fence. This worker must remain silent.
        }
        catch (Exception exception)
        {
            if (!await StopMonitorAndConfirmOwnershipAsync())
            {
                // The handler did not necessarily surface cancellation cooperatively,
                // but store/lease uncertainty still forbids this attempt from publishing.
                return true;
            }

            await PublishTerminalAsync(
                lease,
                handler,
                new DurableJobCompletion("failed", context.LatestSnapshotJson, ErrorMessage: exception.Message),
                CancellationToken.None);
        }
        finally
        {
            monitorCancellation.Cancel();
            try
            {
                await monitor;
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
            }
        }

        return true;
    }

    private async Task MonitorLeaseAndCancellationAsync(
        DurableJobLease lease,
        DurableJobWorkerOptions options,
        CancellationTokenSource executionCancellation,
        CancellationToken monitorCancellation,
        Action onLeaseLost)
    {
        try
        {
            var nextHeartbeatAtUtc = timeProvider.GetUtcNow().Add(options.HeartbeatInterval);
            while (!monitorCancellation.IsCancellationRequested)
            {
                await Task.Delay(options.IdlePollInterval, monitorCancellation);
                var now = timeProvider.GetUtcNow();
                if (now >= nextHeartbeatAtUtc)
                {
                    var renewed = await repository.RenewLeaseAsync(
                        lease.Job.Id,
                        lease.LeaseToken,
                        now,
                        options.LeaseDuration,
                        monitorCancellation);
                    if (!renewed)
                    {
                        onLeaseLost();
                        executionCancellation.Cancel();
                        return;
                    }

                    nextHeartbeatAtUtc = now.Add(options.HeartbeatInterval);
                }

                var stored = await repository.GetJobAsync(lease.Job.Id, monitorCancellation);
                if (stored?.CancellationRequestedAtUtc is not null)
                {
                    executionCancellation.Cancel();
                }
            }
        }
        catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ownership cannot be proven while the durable store is unavailable.
            // Cancel the handler and suppress publication from this attempt.
            onLeaseLost();
            executionCancellation.Cancel();
        }
    }

    private async Task PublishTerminalAsync(
        DurableJobLease lease,
        IDurableJobHandler handler,
        DurableJobCompletion completion,
        CancellationToken cancellationToken)
    {
        var published = await repository.CompleteAsync(
            lease.Job.Id,
            lease.LeaseToken,
            completion.Status,
            completion.SnapshotJson,
            completion.ResultReference,
            completion.ErrorMessage,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (!published)
        {
            var current = await repository.GetJobAsync(lease.Job.Id, cancellationToken);
            if (completion.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
                current?.LeaseToken == lease.LeaseToken &&
                current.CancellationRequestedAtUtc is not null)
            {
                published = await repository.CompleteAsync(
                    lease.Job.Id,
                    lease.LeaseToken,
                    "cancelled",
                    completion.SnapshotJson,
                    null,
                    null,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }

        if (!published)
        {
            throw new DurableJobLeaseLostException(lease.Job.Id);
        }

        try
        {
            var terminal = await repository.GetJobAsync(lease.Job.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Published durable job {lease.Job.Id:N} disappeared.");
            await handler.OnTerminalPublishedAsync(terminal, cancellationToken);
        }
        catch
        {
            // The durable terminal row is authoritative and has already released
            // its lease. Projection/marker publication is retried by the handler's
            // periodic RefreshProjectionAsync cycle; it must never re-run the job.
        }
    }
}
