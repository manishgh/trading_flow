namespace TradingFlow.Backtesting;

/// <summary>Persisted paper polling health. An entry stop stays latched for this run through worker restarts.</summary>
public sealed record LivePipelineHealth(
    int FailureCount = 0,
    int ConsecutiveFailingPolls = 0,
    DateTimeOffset? FailingSinceUtc = null,
    DateTimeOffset? FirstFailureAtUtc = null,
    DateTimeOffset? LastFailureAtUtc = null,
    string? LastFailure = null,
    DateTimeOffset? EntriesStoppedAtUtc = null,
    string? EntryStopReason = null)
{
    internal const int EntryStopThreshold = 3;
    public bool EntriesStopped => EntriesStoppedAtUtc is not null;

    // Each completed, nonempty poll contributes at most one streak step. Sorting
    // failure keys keeps the reported cause independent of ticker completion order.
    internal LivePipelineHealth RecordPoll(
        IReadOnlyDictionary<string, string> failures,
        DateTimeOffset pollStartedAtUtc)
    {
        if (failures.Count == 0)
        {
            return this with { ConsecutiveFailingPolls = 0, FailingSinceUtc = null };
        }

        var first = failures.OrderBy(pair => pair.Key, StringComparer.Ordinal).First();
        var streak = ConsecutiveFailingPolls + 1;
        var failure = first.Key.Length == 0 ? first.Value : $"{first.Key}: {first.Value}";
        var stopsEntries = streak >= EntryStopThreshold && !EntriesStopped;
        return this with
        {
            FailureCount = FailureCount + failures.Count,
            ConsecutiveFailingPolls = streak,
            FailingSinceUtc = FailingSinceUtc ?? pollStartedAtUtc,
            FirstFailureAtUtc = FirstFailureAtUtc ?? pollStartedAtUtc,
            LastFailureAtUtc = pollStartedAtUtc,
            LastFailure = failure,
            EntriesStoppedAtUtc = stopsEntries ? pollStartedAtUtc : EntriesStoppedAtUtc,
            EntryStopReason = stopsEntries ? failure : EntryStopReason
        };
    }
}
