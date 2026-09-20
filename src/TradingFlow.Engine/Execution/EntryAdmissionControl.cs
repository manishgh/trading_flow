namespace TradingFlow.Engine.Execution;

public sealed record EntryBlock(
    string Source,
    string Code,
    string Detail,
    DateTimeOffset BlockedAtUtc);

public sealed record EntryAdmissionSnapshot(
    bool EntriesAllowed,
    IReadOnlyList<EntryBlock> Blocks);

public static class EntryAdmissionSources
{
    public const string DurableOrderRecovery = "durable_order_recovery";
}

public interface IEntryAdmissionControl
{
    EntryAdmissionSnapshot GetSnapshot();

    void Block(string source, string code, string detail, DateTimeOffset blockedAtUtc);

    bool Clear(string source);

    void EnsureEntriesAllowed();

    IDisposable BeginEntryDispatch(IReadOnlySet<string>? ignoredBlockSources = null);

    Task WaitForEntryDispatchesToDrainAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds independent fail-closed entry blocks. A component may clear only the block it owns.
/// </summary>
public sealed class EntryAdmissionControl : IEntryAdmissionControl
{
    private readonly object sync = new();
    private readonly Dictionary<string, EntryBlock> blocks = new(StringComparer.Ordinal);
    private TaskCompletionSource entryDispatchesDrained = CompletedSource();
    private int activeEntryDispatches;

    public EntryAdmissionSnapshot GetSnapshot()
    {
        lock (sync)
        {
            var snapshot = blocks.Values
                .OrderBy(block => block.Source, StringComparer.Ordinal)
                .ToArray();
            return new EntryAdmissionSnapshot(snapshot.Length == 0, snapshot);
        }
    }

    public void Block(string source, string code, string detail, DateTimeOffset blockedAtUtc)
    {
        var normalizedSource = Require(source, nameof(source));
        var block = new EntryBlock(
            normalizedSource,
            Require(code, nameof(code)),
            Require(detail, nameof(detail)),
            blockedAtUtc.ToUniversalTime());
        lock (sync)
        {
            if (!blocks.TryGetValue(normalizedSource, out var current) ||
                current.Code != block.Code || current.Detail != block.Detail)
            {
                blocks[normalizedSource] = block;
            }
        }
    }

    public bool Clear(string source)
    {
        lock (sync)
        {
            return blocks.Remove(Require(source, nameof(source)));
        }
    }

    public void EnsureEntriesAllowed()
    {
        var snapshot = GetSnapshot();
        if (!snapshot.EntriesAllowed)
        {
            throw new InvalidOperationException(
                $"New entries are blocked: {String.Join("; ", snapshot.Blocks.Select(block => $"{block.Code} ({block.Detail})"))}");
        }
    }

    public IDisposable BeginEntryDispatch(IReadOnlySet<string>? ignoredBlockSources = null)
    {
        lock (sync)
        {
            var activeBlocks = ignoredBlockSources is null || ignoredBlockSources.Count == 0
                ? blocks.Values.ToArray()
                : blocks.Values
                    .Where(block => !ignoredBlockSources.Contains(block.Source))
                    .ToArray();
            if (activeBlocks.Length > 0)
            {
                throw BlockedException(activeBlocks);
            }

            if (activeEntryDispatches == 0)
            {
                entryDispatchesDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            activeEntryDispatches++;
            return new EntryDispatchLease(this);
        }
    }

    public Task WaitForEntryDispatchesToDrainAsync(CancellationToken cancellationToken = default)
    {
        Task drain;
        lock (sync)
        {
            drain = activeEntryDispatches == 0
                ? Task.CompletedTask
                : entryDispatchesDrained.Task;
        }

        return drain.WaitAsync(cancellationToken);
    }

    private void EndEntryDispatch()
    {
        TaskCompletionSource? completed = null;
        lock (sync)
        {
            if (activeEntryDispatches <= 0)
            {
                throw new InvalidOperationException("Entry dispatch accounting underflowed.");
            }

            activeEntryDispatches--;
            if (activeEntryDispatches == 0)
            {
                completed = entryDispatchesDrained;
            }
        }

        completed?.TrySetResult();
    }

    private static InvalidOperationException BlockedException(IEnumerable<EntryBlock> currentBlocks) =>
        new(
            $"New entries are blocked: {String.Join("; ", currentBlocks
                .OrderBy(block => block.Source, StringComparer.Ordinal)
                .Select(block => $"{block.Code} ({block.Detail})"))}");

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class EntryDispatchLease(EntryAdmissionControl owner) : IDisposable
    {
        private EntryAdmissionControl? currentOwner = owner;

        public void Dispose() => Interlocked.Exchange(ref currentOwner, null)?.EndEntryDispatch();
    }

    private static string Require(string value, string parameterName) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", parameterName);
}
