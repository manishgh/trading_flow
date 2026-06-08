using System.Collections.Concurrent;

namespace TradingFlow.Etoro.Trading;

public sealed class EtoroWriteSafetyGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> acceptedWrites = new(StringComparer.Ordinal);
    private readonly TimeSpan dedupeWindow;
    private readonly TimeProvider timeProvider;

    public EtoroWriteSafetyGuard(TimeSpan? dedupeWindow = null, TimeProvider? timeProvider = null)
    {
        this.dedupeWindow = dedupeWindow ?? TimeSpan.FromMinutes(10);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunOnceAsync(string operationKey, Func<Task> operation)
    {
        var now = timeProvider.GetUtcNow();
        RemoveExpired(now);
        if (!acceptedWrites.TryAdd(operationKey, now))
        {
            throw new InvalidOperationException($"Duplicate eToro write blocked for operation key {operationKey}.");
        }

        try
        {
            await operation();
        }
        catch
        {
            acceptedWrites.TryRemove(operationKey, out _);
            throw;
        }
    }

    public async Task<T> RunOnceAsync<T>(string operationKey, Func<Task<T>> operation)
    {
        T result = default!;
        await RunOnceAsync(operationKey, async () =>
        {
            result = await operation();
        });
        return result;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var pair in acceptedWrites)
        {
            if (now - pair.Value >= dedupeWindow)
            {
                acceptedWrites.TryRemove(pair.Key, out _);
            }
        }
    }
}
