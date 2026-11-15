using TradingFlow.Domain.Discovery;

namespace TradingFlow.Backtesting;

/// <summary>Persisted paper polling health and the durable run-wide entry stop.</summary>
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
    public int RunWideFailureCount { get; init; }
    public DiscoveryRefreshHealth? Discovery { get; init; }

    // Count scoped exceptions for diagnosis. Only a shared failure can advance the durable stop.
    internal LivePipelineHealth RecordPoll(
        IReadOnlyDictionary<string, string> tickerFailures,
        IReadOnlyList<string> runWideFailures,
        DateTimeOffset pollStartedAtUtc)
    {
        var sharedFailure = runWideFailures.FirstOrDefault();
        // Snapshots from before this split can contain an unscoped failure streak.
        var previousStreak = RunWideFailureCount == 0 ? 0 : ConsecutiveFailingPolls;
        var streak = sharedFailure is not null ? previousStreak + 1 : 0;
        var updated = this with
        {
            RunWideFailureCount = RunWideFailureCount + runWideFailures.Count,
            ConsecutiveFailingPolls = streak,
            FailingSinceUtc = streak switch
            {
                0 => null,
                1 => pollStartedAtUtc,
                _ => FailingSinceUtc ?? pollStartedAtUtc
            }
        };

        var failureCount = tickerFailures.Count + runWideFailures.Count;
        if (failureCount == 0)
        {
            return updated;
        }

        var failure = sharedFailure ?? FormatTickerFailure(tickerFailures);
        var stopsEntries = streak >= EntryStopThreshold && !EntriesStopped;
        return updated with
        {
            FailureCount = FailureCount + failureCount,
            FirstFailureAtUtc = FirstFailureAtUtc ?? pollStartedAtUtc,
            LastFailureAtUtc = pollStartedAtUtc,
            LastFailure = failure,
            EntriesStoppedAtUtc = stopsEntries ? pollStartedAtUtc : EntriesStoppedAtUtc,
            EntryStopReason = stopsEntries ? failure : EntryStopReason
        };
    }

    // Keep failure reasons deterministic when ticker tasks finish in a different order.
    private static string FormatTickerFailure(IReadOnlyDictionary<string, string> failures)
    {
        var first = failures.OrderBy(pair => pair.Key, StringComparer.Ordinal).First();
        return $"{first.Key}: {first.Value}";
    }

    // Apply the latest discovery projection. An unchanged source or replayed observation
    // leaves a previous failure in force, including after a worker restart.
    internal LivePipelineHealth RecordDiscoveryRefresh(DiscoveryUniverseSnapshot snapshot)
    {
        var failedSources = (Discovery?.FailedSources ?? []).ToDictionary(source => (source.SourceKind, source.SourceKey));
        foreach (var outcome in snapshot.SourceRefreshOutcomes)
        {
            var key = (outcome.SourceKind, outcome.SourceKey);
            if (failedSources.TryGetValue(key, out var previous) && previous.AttemptedAtUtc > outcome.AttemptedAtUtc)
            {
                continue;
            }

            if (outcome.Failure is { } failure)
            {
                failedSources[key] = new DiscoverySourceRefreshFailure(
                    outcome.SourceKind, outcome.SourceKey, outcome.AttemptedAtUtc, failure);
            }
            else if (previous is not null && outcome.CommittedObservationAtUtc is { } observed &&
                     observed >= previous.AttemptedAtUtc && observed <= snapshot.ResolvedAtUtc)
            {
                failedSources.Remove(key);
            }
        }

        var failures = failedSources.Values.OrderBy(source => source.SourceKind, StringComparer.Ordinal)
            .ThenBy(source => source.SourceKey, StringComparer.Ordinal).ToArray();
        if (Discovery?.SessionFailure is null && failures.SequenceEqual(Discovery?.FailedSources ?? []))
        {
            return this;
        }

        return this with { Discovery = new DiscoveryRefreshHealth(null, failures) };
    }

    // A failed aggregate read or publication makes all source membership unavailable for entries.
    internal LivePipelineHealth RecordDiscoveryRefreshFailure(DateTimeOffset attemptedAtUtc, string reason) =>
        this with
        {
            Discovery = new DiscoveryRefreshHealth(
                new DiscoveryRefreshFailure(attemptedAtUtc, reason), Discovery?.FailedSources ?? [])
        };

    // Limit persisted diagnostics while full exceptions remain in structured logs.
    internal static string DescribeFailure(Exception exception)
    {
        const int maximumLength = 512;
        var message = exception.Message.Length == 0 ? exception.GetType().Name : exception.Message;
        return message.Length <= maximumLength ? message : message[..maximumLength];
    }
}
