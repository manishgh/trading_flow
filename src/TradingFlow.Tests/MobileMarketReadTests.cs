using TradingFlow.Mobile.Services;

namespace TradingFlow.Tests;

public sealed class MobileMarketReadTests
{
    [Fact]
    public void RapidFilterEdits_CancelOlderReadAndRejectItsLateResponse()
    {
        var reads = new LatestMarketRead(() => 1);
        reads.Activate();
        var first = reads.Begin();
        var latest = reads.Begin();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(reads.IsCurrent(first));
        Assert.True(reads.IsCurrent(latest));
    }

    [Fact]
    public void SignoutOrBackendSwitch_RejectsResponseEvenBeforeUiSessionEvent()
    {
        long session = 1;
        var reads = new LatestMarketRead(() => session);
        reads.Activate();
        var original = reads.Begin();
        session++;
        Assert.False(reads.IsCurrent(original));
        var changed = reads.Begin();
        Assert.True(reads.IsCurrent(changed));
    }

    [Fact]
    public void NavigationAwayAndBack_RejectsOldRowsErrorsAndBusyCompletion()
    {
        var reads = new LatestMarketRead(() => 1);
        reads.Activate();
        var original = reads.Begin();
        reads.Deactivate();
        Assert.True(original.Token.IsCancellationRequested);
        Assert.False(reads.IsCurrent(original));
        Assert.Throws<InvalidOperationException>(() => reads.Begin());
        reads.Activate();
        var returned = reads.Begin();
        Assert.False(reads.IsCurrent(original));
        Assert.True(reads.IsCurrent(returned));
        reads.Deactivate();
    }
}
