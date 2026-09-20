using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public sealed record OrderDispatchRecoveryHealth(
    bool InitialCycleCompleted,
    bool Healthy,
    DateTimeOffset? LastCompletedAtUtc,
    string Detail);

public interface IOrderDispatchRecoveryHealth
{
    OrderDispatchRecoveryHealth GetHealth();
    Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Hosts the engine-owned durable dispatcher. Broker adoption and retry policy
/// remain in TradingFlow.Engine; the web host only provides process lifetime.
/// </summary>
public sealed class OrderDispatchRecoveryHostedService : BackgroundService, IOrderDispatchRecoveryHealth
{
    private readonly IOrderDispatchService dispatcher;
    private readonly IOrderCommandService orderCommands;
    private readonly IBrokerClient broker;
    private readonly IEntryAdmissionControl admission;
    private readonly OrderDispatchOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderDispatchRecoveryHostedService> logger;
    private readonly object healthLock = new();
    private TaskCompletionSource healthyCycleSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private OrderDispatchRecoveryHealth health = new(
        InitialCycleCompleted: false,
        Healthy: false,
        LastCompletedAtUtc: null,
        Detail: "Durable order recovery has not completed its initial cycle.");

    public OrderDispatchRecoveryHostedService(
        IOrderDispatchService dispatcher,
        IOrderCommandService orderCommands,
        IBrokerClient broker,
        IEntryAdmissionControl admission,
        OrderDispatchOptions options,
        TimeProvider timeProvider,
        ILogger<OrderDispatchRecoveryHostedService> logger)
    {
        this.dispatcher = dispatcher;
        this.orderCommands = orderCommands;
        this.broker = broker;
        this.admission = admission;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        admission.Block(
            EntryAdmissionSources.DurableOrderRecovery,
            "DURABLE_ORDER_RECOVERY_NOT_READY",
            health.Detail,
            timeProvider.GetUtcNow());
    }

    public OrderDispatchRecoveryHealth GetHealth()
    {
        lock (healthLock)
        {
            return health;
        }
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = timeProvider.GetUtcNow().Add(timeout);
        while (true)
        {
            Task readiness;
            lock (healthLock)
            {
                if (health.Healthy)
                {
                    return;
                }

                readiness = healthyCycleSignal.Task;
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                var current = GetHealth();
                throw new TimeoutException(
                    $"Durable order recovery was not healthy within {timeout.TotalSeconds:0} seconds. {current.Detail}");
            }

            try
            {
                await readiness.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                var current = GetHealth();
                throw new TimeoutException(
                    $"Durable order recovery was not healthy within {timeout.TotalSeconds:0} seconds. {current.Detail}");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SetRecoveryInProgress();
            try
            {
                await admission.WaitForEntryDispatchesToDrainAsync(stoppingToken);
                var failures = new List<string>();
                var recoveredExitPreparations = await RecoverCategoryAsync(
                    "position-exit preparation",
                    token => orderCommands.RecoverUnpreparedPositionExitsAsync(broker, token),
                    failures,
                    stoppingToken);
                var recovered = await RecoverCategoryAsync(
                    "order dispatch",
                    token => dispatcher.RecoverPendingAsync(broker, token),
                    failures,
                    stoppingToken);
                var recoveredCancellations = await RecoverCategoryAsync(
                    "order cancellation",
                    token => orderCommands.RecoverPendingCancellationsAsync(broker, token),
                    failures,
                    stoppingToken);
                var recoveredReplacements = await RecoverCategoryAsync(
                    "protective-stop replacement",
                    token => orderCommands.RecoverPendingProtectiveStopReplacementsAsync(broker, token),
                    failures,
                    stoppingToken);
                var summary =
                    $"Recovery cycle completed: exits {recoveredExitPreparations}, intents {recovered}, " +
                    $"cancellations {recoveredCancellations}, replacements {recoveredReplacements}.";
                if (failures.Count == 0)
                {
                    SetHealth(true, summary);
                }
                else
                {
                    SetHealth(false, $"{summary} Failures: {String.Join("; ", failures)}");
                }

                logger.LogInformation(
                    "Durable order recovery handled {RecoveredExitPreparationCount} exit preparation(s), {RecoveredIntentCount} intent(s), {RecoveredCancellationCount} cancellation(s), and {RecoveredReplacementCount} protective replacement(s). FailedCategories={FailedCategoryCount}",
                    recoveredExitPreparations,
                    recovered,
                    recoveredCancellations,
                    recoveredReplacements,
                    failures.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetHealth(false, $"Recovery cycle failed: {exception.Message}");
                logger.LogError(exception, "Durable order-dispatch recovery cycle failed.");
            }

            await Task.Delay(options.RecoveryInterval, stoppingToken);
        }
    }

    private async Task<int> RecoverCategoryAsync(
        string category,
        Func<CancellationToken, Task<int>> recover,
        ICollection<string> failures,
        CancellationToken stoppingToken)
    {
        try
        {
            return await recover(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add($"{category}: {exception.Message}");
            logger.LogError(
                exception,
                "Durable {RecoveryCategory} recovery failed; remaining categories will still run.",
                category);
            return 0;
        }
    }

    private void SetRecoveryInProgress()
    {
        const string detail = "Durable order recovery cycle is in progress.";
        admission.Block(
            EntryAdmissionSources.DurableOrderRecovery,
            "DURABLE_ORDER_RECOVERY_IN_PROGRESS",
            detail,
            timeProvider.GetUtcNow());
        lock (healthLock)
        {
            health = health with
            {
                Healthy = false,
                Detail = detail
            };
            if (healthyCycleSignal.Task.IsCompleted)
            {
                healthyCycleSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void SetHealth(bool healthyState, string detail)
    {
        TaskCompletionSource? signal = null;
        lock (healthLock)
        {
            health = new OrderDispatchRecoveryHealth(
                InitialCycleCompleted: true,
                Healthy: healthyState,
                LastCompletedAtUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                Detail: detail);
            if (healthyState)
            {
                signal = healthyCycleSignal;
            }
            else if (healthyCycleSignal.Task.IsCompleted)
            {
                healthyCycleSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (healthyState)
        {
            admission.Clear(EntryAdmissionSources.DurableOrderRecovery);
        }
        else
        {
            admission.Block(
                EntryAdmissionSources.DurableOrderRecovery,
                "DURABLE_ORDER_RECOVERY_FAILED",
                detail,
                timeProvider.GetUtcNow());
        }

        signal?.TrySetResult();
    }
}
