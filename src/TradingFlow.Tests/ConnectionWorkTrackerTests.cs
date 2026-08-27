using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConnectionWorkTrackerTests
{
    [Fact]
    public async Task DrainSymbol_WaitsForAcceptedWork_AndDoesNotWaitForOtherSymbols()
    {
        var failures = new List<Exception>();
        var tracker = new ConnectionWorkTracker((_, exception) => failures.Add(exception));
        var aapl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var msft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.TryRun("AAPL", "bar", () => aapl.Task));
        Assert.True(tracker.TryRun("MSFT", "bar", () => msft.Task));
        var drain = tracker.DrainSymbolAsync("AAPL", TimeSpan.FromSeconds(2));

        Assert.False(drain.IsCompleted);
        aapl.SetResult();
        await drain;
        Assert.False(msft.Task.IsCompleted);
        Assert.Empty(failures);

        msft.SetResult();
        tracker.StopAccepting();
        await tracker.DrainAllAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StopAccepting_RejectsNewWork_AndDrainObservesFailures()
    {
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new ConnectionWorkTracker((_, exception) => observed.SetResult(exception));

        Assert.True(tracker.TryRun("AAPL", "repair", () => throw new InvalidOperationException("failed")));
        tracker.StopAccepting();
        Assert.False(tracker.TryRun("AAPL", "bar", () => Task.CompletedTask));
        await tracker.DrainAllAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<InvalidOperationException>(await observed.Task);
    }
}
