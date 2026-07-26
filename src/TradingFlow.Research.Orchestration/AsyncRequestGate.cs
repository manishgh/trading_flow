using System.Collections.Concurrent;

namespace TradingFlow.Research.Orchestration;

internal static class AsyncRequestGate
{
    private static readonly ConcurrentDictionary<string, Gate> Gates =
        new(StringComparer.Ordinal);

    public static async Task<IDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        while (true)
        {
            var gate = Gates.GetOrAdd(key, _ => new Gate());
            lock (gate)
            {
                if (gate.Retired)
                {
                    continue;
                }

                gate.ReferenceCount++;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                return new Lease(key, gate);
            }
            catch
            {
                Release(key, gate, releaseSemaphore: false);
                throw;
            }
        }
    }

    private static void Release(string key, Gate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        var remove = false;
        lock (gate)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                gate.Retired = true;
                remove = true;
            }
        }

        if (remove)
        {
            Gates.TryRemove(new KeyValuePair<string, Gate>(key, gate));
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Lease(string key, Gate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Release(key, gate, releaseSemaphore: true);
            }
        }
    }
}
