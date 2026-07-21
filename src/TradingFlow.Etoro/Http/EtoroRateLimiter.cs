using TradingFlow.Etoro.Configuration;

namespace TradingFlow.Etoro.Http;

public sealed class EtoroRateLimiter
{
    private readonly SlidingWindowLimiter readLimiter;
    private readonly SlidingWindowLimiter writeLimiter;

    public EtoroRateLimiter(EtoroRateLimitOptions options, TimeProvider? timeProvider = null)
    {
        var provider = timeProvider ?? TimeProvider.System;
        readLimiter = new SlidingWindowLimiter(Math.Max(1, options.ReadPerMinute), TimeSpan.FromMinutes(1), provider);
        writeLimiter = new SlidingWindowLimiter(Math.Max(1, options.WritePerMinute), TimeSpan.FromMinutes(1), provider);
    }

    public Task WaitAsync(EtoroApiOperation operation, CancellationToken cancellationToken)
    {
        return operation == EtoroApiOperation.Read
            ? readLimiter.WaitAsync(cancellationToken)
            : writeLimiter.WaitAsync(cancellationToken);
    }

    private sealed class SlidingWindowLimiter
    {
        private readonly int limit;
        private readonly TimeSpan window;
        private readonly TimeProvider timeProvider;
        private readonly Queue<DateTimeOffset> timestamps = new();
        private readonly SemaphoreSlim gate = new(1, 1);

        public SlidingWindowLimiter(int limit, TimeSpan window, TimeProvider timeProvider)
        {
            this.limit = limit;
            this.window = window;
            this.timeProvider = timeProvider;
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                TimeSpan delay;
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var now = timeProvider.GetUtcNow();
                    while (timestamps.Count > 0 && now - timestamps.Peek() >= window)
                    {
                        timestamps.Dequeue();
                    }

                    if (timestamps.Count < limit)
                    {
                        timestamps.Enqueue(now);
                        return;
                    }

                    delay = window - (now - timestamps.Peek());
                }
                finally
                {
                    gate.Release();
                }

                await Task.Delay(delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, cancellationToken);
            }
        }
    }
}
