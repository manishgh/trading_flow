using System.Collections.Concurrent;

namespace TradingFlow.Engine.Signals;

public sealed class TradingViewSignalDeduper
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> acceptedEventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan retentionWindow;

    public TradingViewSignalDeduper(TimeSpan retentionWindow)
    {
        if (retentionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionWindow), "Retention window must be positive.");
        }

        this.retentionWindow = retentionWindow;
    }

    public bool TryAccept(string eventId, DateTimeOffset receivedAt)
    {
        if (String.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        Prune(receivedAt);
        return acceptedEventIds.TryAdd(eventId.Trim(), receivedAt);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in acceptedEventIds)
        {
            if (now - pair.Value > retentionWindow)
            {
                acceptedEventIds.TryRemove(pair.Key, out _);
            }
        }
    }
}
