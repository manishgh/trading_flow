namespace TradingFlow.Engine.Execution;

public sealed class BrokerMutationRejectedException(string operation) : InvalidOperationException(
    $"Broker mutation '{operation}' was rejected because execution shutdown has started.")
{
    public string Operation { get; } = operation;
}

public interface IBrokerMutationCoordinator
{
    IDisposable Enter(string operation);

    Task<T> StopAcceptingAndExecuteAsync<T>(
        Func<CancellationToken, Task<T>> shutdownWork,
        TimeSpan drainTimeout,
        CancellationToken cancellationToken = default);

    Task WaitForQuiescenceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides the process-wide atomic boundary around broker writes. Shutdown closes
/// admission and waits for every mutation that crossed the boundary to finish before
/// it receives exclusive authority for its own cancellation and protection work.
/// </summary>
public sealed class BrokerMutationCoordinator : IBrokerMutationCoordinator
{
    private readonly object sync = new();
    private readonly AsyncLocal<Guid?> currentShutdownAuthority = new();
    private TaskCompletionSource quiescence = CompletedSource();
    private Guid? shutdownAuthority;
    private int activeMutations;
    private bool accepting = true;

    public IDisposable Enter(string operation)
    {
        if (String.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("A broker mutation operation name is required.", nameof(operation));
        }

        lock (sync)
        {
            var hasShutdownAuthority = shutdownAuthority is { } authority &&
                currentShutdownAuthority.Value == authority;
            if (!accepting && !hasShutdownAuthority)
            {
                throw new BrokerMutationRejectedException(operation.Trim());
            }

            if (activeMutations == 0)
            {
                quiescence = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activeMutations++;
            return new MutationLease(this);
        }
    }

    public async Task<T> StopAcceptingAndExecuteAsync<T>(
        Func<CancellationToken, Task<T>> shutdownWork,
        TimeSpan drainTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shutdownWork);
        ValidateTimeout(drainTimeout, nameof(drainTimeout));

        Guid authority;
        lock (sync)
        {
            if (shutdownAuthority is not null)
            {
                throw new InvalidOperationException("Execution shutdown has already acquired broker mutation ownership.");
            }

            accepting = false;
            authority = Guid.NewGuid();
            shutdownAuthority = authority;
        }

        await WaitForQuiescenceAsync(drainTimeout, cancellationToken);
        var previousAuthority = currentShutdownAuthority.Value;
        currentShutdownAuthority.Value = authority;
        try
        {
            return await shutdownWork(cancellationToken);
        }
        finally
        {
            currentShutdownAuthority.Value = previousAuthority;
        }
    }

    public async Task WaitForQuiescenceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout, nameof(timeout));
        Task wait;
        lock (sync)
        {
            wait = activeMutations == 0 ? Task.CompletedTask : quiescence.Task;
        }

        await wait.WaitAsync(timeout, cancellationToken);
    }

    private void Exit()
    {
        TaskCompletionSource? completed = null;
        lock (sync)
        {
            if (activeMutations <= 0)
            {
                throw new InvalidOperationException("Broker mutation accounting underflowed.");
            }

            activeMutations--;
            if (activeMutations == 0)
            {
                completed = quiescence;
            }
        }

        completed?.TrySetResult();
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class MutationLease(BrokerMutationCoordinator owner) : IDisposable
    {
        private BrokerMutationCoordinator? currentOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref currentOwner, null)?.Exit();
    }
}
