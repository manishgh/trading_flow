using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class WorkspaceStatusTests
{
    // A global pause denies both sides; the independent entry stop cannot disable authorized protection or exits.
    [Fact]
    public void GlobalPauseAndEntryStop_HaveDifferentExitEffects()
    {
        var entryBlock = new EntryAdmissionSnapshot(false,
            [new("fixture", "daily_loss", "The account reached its daily loss limit.", DateTimeOffset.UnixEpoch)]);
        var stopped = WorkspaceStatusService.DescribeRestrictions(false, entryBlock);
        Assert.Contains("daily loss limit", stopped.Opening);
        Assert.Null(stopped.Exit);
        var paused = WorkspaceStatusService.DescribeRestrictions(true, entryBlock);
        Assert.Equal(paused.Opening, paused.Exit);
        Assert.Contains("including exits and protection changes", paused.Exit);
        Assert.Equal((null, null), WorkspaceStatusService.DescribeRestrictions(false, new(true, [])));
    }

    // A configured host may still be starting; only the actual durable worker can mark preparation available.
    [Fact]
    public void PreparationStatus_RequiresStartedWorkerAndEnabledRuntime()
    {
        var paused = new PreparationWorkerCapability(new(false, true, true));
        paused.SetStarted(true);
        Assert.Equal("paused", paused.Read().State);
        var started = new PreparationWorkerCapability(new(true, true));
        Assert.False(started.Read().CanCollect);
        started.SetStarted(true);
        Assert.True(started.Read().CanCollect);
        started.SetStarted(false);
        Assert.Equal("unknown", started.Read().State);
    }
}
