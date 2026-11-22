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
    private readonly BrokerAccountBindingOptions accountBinding;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderDispatchRecoveryHostedService> logger;
    private readonly object healthLock = new();
    private DateTimeOffset lastFullPassAtUtc;
    private TaskCompletionSource healthyCycleSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private OrderDispatchRecoveryHealth health = new(
        InitialCycleCompleted: false,
        Healthy: false,
        LastCompletedAtUtc: null,
        Detail: "Durable order recovery has not completed its initial cycle.");

    // Starts with entries blocked until the first full recovery pass succeeds.
    public OrderDispatchRecoveryHostedService(
        IOrderDispatchService dispatcher,
        IOrderCommandService orderCommands,
        IBrokerClient broker,
        IEntryAdmissionControl admission,
        OrderDispatchOptions options,
        BrokerAccountBindingOptions accountBinding,
        TimeProvider timeProvider,
        ILogger<OrderDispatchRecoveryHostedService> logger)
    {
        this.dispatcher = dispatcher;
        this.orderCommands = orderCommands;
        this.broker = broker;
        this.admission = admission;
        this.options = options;
        this.accountBinding = accountBinding;
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

    // Wait for a healthy cycle using the same clock that measures the readiness deadline.
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
                await readiness.WaitAsync(remaining, timeProvider, cancellationToken);
            }
            catch (TimeoutException)
            {
                var current = GetHealth();
                throw new TimeoutException(
                    $"Durable order recovery was not healthy within {timeout.TotalSeconds:0} seconds. {current.Detail}");
            }
        }
    }

    // Keep recovery retries and readiness deadlines on the host's configured clock. A full pass reads the broker account
    // four times, so once a full pass has succeeded, later cycles skip it while the local journal has nothing to recover.
    // Such an idle cycle clears the entry block without proving the broker reachable or re-reading its account; order
    // synchronization watches the connection, and each dispatch still validates the bound account before it posts.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var origin = BrokerCallOrigin.Begin(BrokerCallOrigin.OrderRecovery);
        var lastCycleHealthy = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            SetRecoveryInProgress();
            try
            {
                await admission.WaitForEntryDispatchesToDrainAsync(stoppingToken);
                lastCycleHealthy = lastCycleHealthy && !await HasRecoverableWorkAsync(stoppingToken)
                    ? ReportIdleCycle()
                    : await RunFullPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The next cycle must be a full pass, never an idle one.
                lastCycleHealthy = false;
                SetHealth(false, $"Recovery cycle failed: {exception.Message}");
                logger.LogError(exception, "Durable order-dispatch recovery cycle failed.");
            }

            await Task.Delay(options.RecoveryInterval, timeProvider, stoppingToken);
        }
    }

    // Reports a healthy cycle that had nothing to recover and so made no broker call.
    private bool ReportIdleCycle()
    {
        SetHealth(true, FormattableString.Invariant(
            $"Nothing to recover in the local journal; broker not contacted. Last full pass {lastFullPassAtUtc:yyyy-MM-dd HH:mm:ss} UTC."));
        logger.LogDebug("Durable order recovery found no local work; broker not contacted.");
        return true;
    }

    // Runs all four recoveries, each reading and validating the broker account, and reports whether every one succeeded.
    private async Task<bool> RunFullPassAsync(CancellationToken stoppingToken)
    {
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
            lastFullPassAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
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
        return failures.Count == 0;
    }

    // Asks the local journal whether any of the four recoveries has work for the bound account. Without an enforced
    // account binding the account is unknown locally, so the full pass must run and read it from the broker.
    private async Task<bool> HasRecoverableWorkAsync(CancellationToken stoppingToken) =>
        !accountBinding.EnforcementEnabled ||
        await dispatcher.HasRecoverableWorkAsync(accountBinding.ExpectedAccountId, stoppingToken) ||
        await orderCommands.HasRecoverableWorkAsync(accountBinding.ExpectedAccountId, stoppingToken);

    // Runs one recovery; a failure is logged and recorded so the remaining recoveries still run.
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
