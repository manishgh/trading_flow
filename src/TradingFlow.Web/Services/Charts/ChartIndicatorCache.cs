using TradingFlow.Contracts.V1;

namespace TradingFlow.Web.Services.Charts;

internal readonly record struct ChartHistoryKey(string Symbol, bool Daily, DateOnly Anchor, bool PartialCalendar = false);
internal readonly record struct ChartCalculationKey(ChartHistoryKey History, string Profile, string Revision);
internal readonly record struct ChartIndicatorCacheMetrics(int Running, int Queued, int Profiles, int Points, int Bytes, long Started);

/// <summary>Coalesces exact public market-data calculations and accounts for retained derived results.</summary>
internal sealed class ChartIndicatorCache
{
    private const int MaximumRunning = 2;
    private const int MaximumQueued = 32;
    private const int MaximumProfiles = 4;
    private const int MaximumPoints = 100_000;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<(ChartHistoryKey History, string Profile), Stored> entries = [];
    private readonly Dictionary<ChartCalculationKey, Work> pending = [];
    private readonly LinkedList<Work> queue = [];
    private int running;
    private int points;
    private int bytes;
    private long sequence;
    private long started;

    private sealed record Stored(string Revision, IReadOnlyList<ChartIndicatorSeries> Series,
        long Generation, long Used, int Points, int Bytes);

    private sealed class Work(ChartCalculationKey key, long generation,
        Func<CancellationToken, IReadOnlyList<ChartIndicatorSeries>> calculate, Func<ChartHistoryKey, bool> isActive)
    {
        public ChartCalculationKey Key { get; } = key;
        public long Generation { get; } = generation;
        public Func<CancellationToken, IReadOnlyList<ChartIndicatorSeries>> Calculate { get; } = calculate;
        public Func<ChartHistoryKey, bool> IsActive { get; } = isActive;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<IReadOnlyList<ChartIndicatorSeries>?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Work>? Node { get; set; }
        public int Viewers { get; set; } = 1;
    }

    // A null result is an explicit retry condition. The viewer's token governs only its interest in shared work.
    public Task<IReadOnlyList<ChartIndicatorSeries>?> ReadAsync(ChartCalculationKey key,
        Func<CancellationToken, IReadOnlyList<ChartIndicatorSeries>> calculate,
        Func<ChartHistoryKey, bool> isActive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Work work;
        lock (gate)
        {
            foreach (var historySlot in entries.Keys.Where(slot => !isActive(slot.History)).ToArray())
                Remove(historySlot);
            var slot = (key.History, key.Profile);
            if (entries.TryGetValue(slot, out var hit) && hit.Revision == key.Revision)
            {
                entries[slot] = hit with { Used = ++sequence };
                return Task.FromResult<IReadOnlyList<ChartIndicatorSeries>?>(hit.Series);
            }
            if (pending.TryGetValue(key, out var shared))
            {
                work = shared;
                work.Viewers++;
            }
            else
            {
                if (running == MaximumRunning && queue.Count == MaximumQueued)
                    return Task.FromResult<IReadOnlyList<ChartIndicatorSeries>?>(null);
                work = new Work(key, ++sequence, calculate, isActive);
                pending.Add(key, work);
                if (running < MaximumRunning) Start(work);
                else work.Node = queue.AddLast(work);
            }
        }
        return WaitAsync(work, token);
    }

    // Deregister canceled viewers promptly and leave a shared calculation running while anyone needs it.
    private async Task<IReadOnlyList<ChartIndicatorSeries>?> WaitAsync(Work work, CancellationToken token)
    {
        try { return await work.Completion.Task.WaitAsync(token); }
        finally
        {
            lock (gate)
            {
                work.Viewers--;
                if (work.Viewers == 0 && !work.Completion.Task.IsCompleted)
                {
                    pending.Remove(work.Key);
                    work.Cancellation.Cancel();
                    if (work.Node is { } node)
                    {
                        queue.Remove(node);
                        work.Completion.TrySetCanceled(work.Cancellation.Token);
                        work.Cancellation.Dispose();
                    }
                }
            }
        }
    }

    // Reserve a numerical slot before scheduling work so parallel callers cannot grow the queue unboundedly.
    private void Start(Work work)
    {
        running++;
        started++;
        work.Node = null;
        _ = Task.Run(() => Execute(work));
    }

    // Publish only completed results, then hand the released numerical slot to the next distinct request.
    private void Execute(Work work)
    {
        try
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            var output = work.Calculate(work.Cancellation.Token);
            lock (gate)
            {
                if (!work.Cancellation.IsCancellationRequested && work.IsActive(work.Key.History))
                    Store(work, output);
                work.Completion.TrySetResult(output);
            }
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        {
            work.Completion.TrySetCanceled(work.Cancellation.Token);
        }
        catch (Exception exception)
        {
            work.Completion.TrySetException(exception);
        }
        finally
        {
            lock (gate)
            {
                if (pending.TryGetValue(work.Key, out var current) && ReferenceEquals(work, current))
                    pending.Remove(work.Key);
                running--;
                if (queue.First is { } next)
                {
                    queue.RemoveFirst();
                    Start(next.Value);
                }
                work.Cancellation.Dispose();
            }
        }
    }

    // Keep at most one revision per profile and deterministically give up the least recently used slots.
    private void Store(Work work, IReadOnlyList<ChartIndicatorSeries> output)
    {
        var slot = (work.Key.History, work.Key.Profile);
        if (entries.TryGetValue(slot, out var current) && current.Generation > work.Generation ||
            pending.Values.Any(newer => newer.Generation > work.Generation &&
                newer.Key.History == slot.History && newer.Key.Profile == slot.Profile))
            return;

        var count = output.Sum(item => item.Samples.Sum(column => column.Length));
        // Nullable doubles occupy 16 bytes. The fixed allowance covers metadata, keys, tasks and array headers.
        var size = 2048 + output.Sum(item => 4096 +
            16 * item.Samples.Sum(column => column.Length) + 64 * item.AvailabilityRuns.Count + 8 * item.SegmentStarts.Count);
        if (count > MaximumPoints || size > MaximumBytes) return;

        Remove(slot);
        var related = entries.Where(pair => pair.Key.History == slot.History).OrderBy(pair => pair.Value.Used).ToArray();
        if (related.Length >= MaximumProfiles) Remove(related[0].Key);
        while (points + count > MaximumPoints || bytes + size > MaximumBytes)
            Remove(entries.MinBy(pair => pair.Value.Used).Key);

        entries.Add(slot, new Stored(work.Key.Revision, output, work.Generation, ++sequence, count, size));
        points += count;
        bytes += size;
    }

    // Accounting changes only when the corresponding retained data is actually removed.
    private void Remove((ChartHistoryKey History, string Profile) slot)
    {
        if (!entries.Remove(slot, out var old)) return;
        points -= old.Points;
        bytes -= old.Bytes;
    }

    // Report bounded-cache state for deterministic admission and eviction tests.
    internal ChartIndicatorCacheMetrics Measure()
    {
        lock (gate) return new ChartIndicatorCacheMetrics(running, queue.Count, entries.Count, points, bytes, started);
    }
}
