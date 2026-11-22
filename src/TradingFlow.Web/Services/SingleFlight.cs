namespace TradingFlow.Web.Services;

/// <summary>
/// Runs at most one task per key at a time: callers that ask for a key while its task is running join that task instead
/// of starting another. The task starts on the thread pool (outside the lock) and its entry is removed as soon as it
/// finishes, whether it succeeded or failed, so a failure is never reused and finished entries never pile up.
/// </summary>
public sealed class SingleFlight<TKey, TValue>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, Task<TValue>> running = new(comparer);

    /// <summary>Returns the running task for <paramref name="key"/>, or starts <paramref name="start"/> for it.</summary>
    public Task<TValue> RunAsync(TKey key, Func<Task<TValue>> start)
    {
        Task<TValue> task;
        lock (gate)
        {
            if (running.TryGetValue(key, out var existing)) return existing;
            task = Task.Run(start);
            running[key] = task;
        }

        _ = task.ContinueWith(_ => Remove(key, task), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>How many keys currently have a running task (for tests and diagnostics).</summary>
    public int RunningCount
    {
        get { lock (gate) return running.Count; }
    }

    // Drops the finished task's entry unless a newer task already replaced it.
    private void Remove(TKey key, Task<TValue> finished)
    {
        lock (gate)
        {
            if (running.TryGetValue(key, out var current) && current == finished) running.Remove(key);
        }
    }
}
