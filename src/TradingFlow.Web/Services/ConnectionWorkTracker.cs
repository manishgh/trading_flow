using System.Collections.Concurrent;

namespace TradingFlow.Web.Services;

/// <summary>
/// Owns asynchronous work accepted from one provider connection. Stopping the
/// tracker prevents new work; draining proves no accepted task can outlive the
/// connection lease.
/// </summary>
internal sealed class ConnectionWorkTracker(Action<string, Exception> onFailure)
{
    private readonly ConcurrentDictionary<long, TrackedWork> active = [];
    private long sequence;
    private int accepting = 1;

    public bool TryRun(string symbol, string operation, Func<Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);
        if (Volatile.Read(ref accepting) == 0)
        {
            return false;
        }

        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = new TrackedWork(symbol.Trim().ToUpperInvariant(), completion.Task);
        if (!active.TryAdd(id, tracked))
        {
            throw new InvalidOperationException("Could not register provider-connection work.");
        }

        if (Volatile.Read(ref accepting) == 0)
        {
            active.TryRemove(id, out _);
            return false;
        }

        _ = ExecuteAsync(id, operation, work, completion);
        return true;
    }

    public void StopAccepting() => Interlocked.Exchange(ref accepting, 0);

    public Task DrainSymbolAsync(
        string symbol,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        DrainAsync(symbol.Trim().ToUpperInvariant(), timeout, cancellationToken);

    public Task DrainAllAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        DrainAsync(null, timeout, cancellationToken);

    private async Task ExecuteAsync(
        long id,
        string operation,
        Func<Task> work,
        TaskCompletionSource completion)
    {
        try
        {
            await work();
        }
        catch (Exception exception)
        {
            onFailure(operation, exception);
        }
        finally
        {
            completion.TrySetResult();
            active.TryRemove(id, out _);
        }
    }

    private async Task DrainAsync(
        string? symbol,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            var pending = active.Values
                .Where(value => symbol is null || value.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Completion)
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    symbol is null
                        ? "Provider-connection work did not drain before the timeout."
                        : $"Provider-connection work for {symbol} did not drain before the timeout.");
            }
        }
    }

    private sealed record TrackedWork(string Symbol, Task Completion);
}
