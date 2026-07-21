using System.Collections.Concurrent;

namespace TradingFlow.Engine.Execution;

public sealed record EntryBlock(
    string Source,
    string Code,
    string Detail,
    DateTimeOffset BlockedAtUtc);

public sealed record EntryAdmissionSnapshot(
    bool EntriesAllowed,
    IReadOnlyList<EntryBlock> Blocks);

public interface IEntryAdmissionControl
{
    EntryAdmissionSnapshot GetSnapshot();

    void Block(string source, string code, string detail, DateTimeOffset blockedAtUtc);

    bool Clear(string source);

    void EnsureEntriesAllowed();
}

/// <summary>
/// Holds independent fail-closed entry blocks. A component may clear only the block it owns.
/// </summary>
public sealed class EntryAdmissionControl : IEntryAdmissionControl
{
    private readonly ConcurrentDictionary<string, EntryBlock> blocks =
        new(StringComparer.Ordinal);

    public EntryAdmissionSnapshot GetSnapshot()
    {
        var snapshot = blocks.Values
            .OrderBy(block => block.Source, StringComparer.Ordinal)
            .ToArray();
        return new EntryAdmissionSnapshot(snapshot.Length == 0, snapshot);
    }

    public void Block(string source, string code, string detail, DateTimeOffset blockedAtUtc)
    {
        var normalizedSource = Require(source, nameof(source));
        var block = new EntryBlock(
            normalizedSource,
            Require(code, nameof(code)),
            Require(detail, nameof(detail)),
            blockedAtUtc.ToUniversalTime());
        blocks.AddOrUpdate(normalizedSource, block, (_, current) =>
            current.Code == block.Code && current.Detail == block.Detail
                ? current
                : block);
    }

    public bool Clear(string source) => blocks.TryRemove(Require(source, nameof(source)), out _);

    public void EnsureEntriesAllowed()
    {
        var snapshot = GetSnapshot();
        if (!snapshot.EntriesAllowed)
        {
            throw new InvalidOperationException(
                $"New entries are blocked: {String.Join("; ", snapshot.Blocks.Select(block => $"{block.Code} ({block.Detail})"))}");
        }
    }

    private static string Require(string value, string parameterName) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", parameterName);
}
