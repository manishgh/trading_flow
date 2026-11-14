using System.Text.Json;
using TradingFlow.Backtesting;

namespace TradingFlow.Tests;

public sealed class LivePipelineHealthTests
{
    // A cycle with multiple failures contributes one streak step, and a successful cycle resets it.
    [Fact]
    public void Polls_DeterministicallyCountFailuresAndKeepTheFirstFailure()
    {
        var first = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        Dictionary<string, string> failures = new() { ["ZZZ"] = "timeout", ["AAA"] = "failed read" };
        var health = new LivePipelineHealth().RecordPoll(failures, first);
        Assert.Equal(2, health.FailureCount);
        Assert.Equal(1, health.ConsecutiveFailingPolls);
        Assert.Equal("AAA: failed read", health.LastFailure);
        Assert.Equal(first, health.FailingSinceUtc);

        health = health.RecordPoll(new Dictionary<string, string>(), first.AddMinutes(1));
        Assert.Equal(0, health.ConsecutiveFailingPolls);
        Assert.Null(health.FailingSinceUtc);
        Assert.Equal(first, health.FirstFailureAtUtc);
        Assert.Equal(first, health.LastFailureAtUtc);
        Assert.False(health.EntriesStopped);
    }

    // A replacement worker restores the latch and keeps the cause after the provider recovers.
    [Fact]
    public void Polls_ThirdConsecutiveFailurePersistsTheEntryStopAcrossRestart()
    {
        var first = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        Dictionary<string, string> failures = new() { ["AAA"] = "timeout" };
        var health = new LivePipelineHealth()
            .RecordPoll(failures, first)
            .RecordPoll(failures, first.AddMinutes(1))
            .RecordPoll(failures, first.AddMinutes(2));
        Assert.True(health.EntriesStopped);

        var restarted = JsonSerializer.Deserialize<LivePipelineHealth>(JsonSerializer.Serialize(health));
        Assert.NotNull(restarted);
        var recovered = restarted.RecordPoll(new Dictionary<string, string>(), first.AddMinutes(3));
        Assert.True(recovered.EntriesStopped);
        Assert.Equal("AAA: timeout", recovered.EntryStopReason);
        Assert.Equal(first.AddMinutes(2), recovered.EntriesStoppedAtUtc);
    }
}
