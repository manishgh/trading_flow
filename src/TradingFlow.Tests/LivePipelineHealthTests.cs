using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Discovery;

namespace TradingFlow.Tests;

public sealed class LivePipelineHealthTests
{
    // Ticker failures count separately and never advance the shared failure streak.
    [Fact]
    public void Polls_DeterministicallyCountFailuresAndKeepTheFirstFailure()
    {
        var first = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        Dictionary<string, string> failures = new() { ["ZZZ"] = "timeout", ["AAA"] = "failed read" };
        var health = new LivePipelineHealth().RecordPoll(failures, [], first)
            .RecordPoll(failures, [], first.AddSeconds(1))
            .RecordPoll(failures, [], first.AddSeconds(2));
        Assert.Equal(6, health.FailureCount);
        Assert.Equal(0, health.ConsecutiveFailingPolls);
        Assert.Equal("AAA: failed read", health.LastFailure);
        Assert.Null(health.FailingSinceUtc);

        health = health.RecordPoll(new Dictionary<string, string>(), [], first.AddMinutes(1));
        Assert.Equal(0, health.ConsecutiveFailingPolls);
        Assert.Null(health.FailingSinceUtc);
        Assert.Equal(first, health.FirstFailureAtUtc);
        Assert.Equal(first.AddSeconds(2), health.LastFailureAtUtc);
        Assert.False(health.EntriesStopped);
    }

    // A replacement worker restores the latch and keeps the cause after the provider recovers.
    [Fact]
    public void Polls_ThirdConsecutiveFailurePersistsTheEntryStopAcrossRestart()
    {
        var first = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        var failures = new Dictionary<string, string>();
        string[] sharedFailures = ["Shared store unavailable"];
        var legacy = new LivePipelineHealth(10, 2, FailingSinceUtc: first.AddMinutes(-2));
        var firstScopedPoll = legacy.RecordPoll(failures, sharedFailures, first);
        Assert.Equal(1, firstScopedPoll.ConsecutiveFailingPolls);
        Assert.False(firstScopedPoll.EntriesStopped);

        var health = firstScopedPoll.RecordPoll(failures, sharedFailures, first.AddMinutes(1))
            .RecordPoll(failures, sharedFailures, first.AddMinutes(2));
        Assert.True(health.EntriesStopped);

        var restarted = JsonSerializer.Deserialize<LivePipelineHealth>(JsonSerializer.Serialize(health));
        Assert.NotNull(restarted);
        var recovered = restarted.RecordPoll(new Dictionary<string, string>(), [], first.AddMinutes(3));
        Assert.True(recovered.EntriesStopped);
        Assert.Equal("Shared store unavailable", recovered.EntryStopReason);
        Assert.Equal(first.AddMinutes(2), recovered.EntriesStoppedAtUtc);
        Assert.Equal(3, recovered.RunWideFailureCount);
        Assert.Equal(0, recovered.ConsecutiveFailingPolls);
    }

    // No due refresh and an old receipt cannot lift a persisted source failure.
    [Fact]
    public void Refresh_OnlyFreshObservationClearsSourceFailureAndDoesNotClearRunStop()
    {
        var first = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        var outcome = new DiscoverySourceRefreshOutcome("finviz", "test", first, null, "timeout");
        var degraded = new LivePipelineHealth(EntriesStoppedAtUtc: first.AddMinutes(-1))
            .RecordDiscoveryRefresh(Snapshot(first, [outcome]))
            .RecordDiscoveryRefreshFailure(first.AddSeconds(1), "projection unavailable");
        var resumed = JsonSerializer.Deserialize<LivePipelineHealth>(JsonSerializer.Serialize(degraded));
        Assert.NotNull(resumed?.Discovery?.SessionFailure);

        var noDue = resumed!.RecordDiscoveryRefresh(Snapshot(first.AddMinutes(1), []));
        Assert.Null(noDue.Discovery?.SessionFailure);
        Assert.Single(noDue.Discovery!.FailedSources);

        var stale = outcome with
        {
            AttemptedAtUtc = first.AddMinutes(2),
            CommittedObservationAtUtc = first.AddMinutes(-1),
            Failure = null
        };
        Assert.Equal(noDue, noDue.RecordDiscoveryRefresh(Snapshot(first.AddMinutes(2), [stale])));
        var fresh = stale with { CommittedObservationAtUtc = first.AddMinutes(2) };
        var recovered = noDue.RecordDiscoveryRefresh(Snapshot(first.AddMinutes(2), [fresh]));
        Assert.Empty(recovered.Discovery!.FailedSources);
        Assert.True(recovered.EntriesStopped);
        Assert.Equal(0, recovered.FailureCount);
    }

    // Operators see a source-only outage even when no ticker or shared pipeline exception was counted.
    [Fact]
    public void FormatPipelineHealth_ShowsFailedSourceAndItsRecovery()
    {
        var now = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        var model = new TradingFlow.Web.Pages.PaperJobModel(null!, null!, null!, null!);
        var outcome = new DiscoverySourceRefreshOutcome("finviz", "screen", now, null, "fixture timeout");
        var failed = new LivePipelineHealth().RecordDiscoveryRefresh(Snapshot(now, [outcome]));
        Assert.Contains("finviz/screen refresh failed", model.FormatPipelineHealth(failed), StringComparison.Ordinal);
        var recovered = failed.RecordDiscoveryRefresh(Snapshot(now.AddMinutes(1),
            [outcome with { AttemptedAtUtc = now.AddMinutes(1), CommittedObservationAtUtc = now.AddMinutes(1), Failure = null }]));
        Assert.Null(model.FormatPipelineHealth(recovered));
    }

    // An older snapshot recorded an unscoped streak that cannot be labeled run-wide.
    [Fact]
    public void FormatPipelineHealth_ReportsLegacyFailureWithoutInventingScope()
    {
        var now = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        var model = new TradingFlow.Web.Pages.PaperJobModel(null!, null!, null!, null!);
        var legacy = new LivePipelineHealth(1, 1, now, LastFailureAtUtc: now, LastFailure: "Ticker warning");
        var status = model.FormatPipelineHealth(legacy);

        Assert.Contains("Ticker warning", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Run-wide pipeline failures since", status, StringComparison.Ordinal);
    }

    // A successful empty universe is still a complete source projection.
    private static DiscoveryUniverseSnapshot Snapshot(
        DateTimeOffset resolvedAtUtc, IReadOnlyList<DiscoverySourceRefreshOutcome> outcomes) =>
        new(Guid.Empty, resolvedAtUtc, [], [], []) { SourceRefreshOutcomes = outcomes };
}
