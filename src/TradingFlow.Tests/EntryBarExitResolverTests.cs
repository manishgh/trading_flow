using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class EntryBarExitResolverTests
{
    [Fact]
    public void Resolve_LongStopOnly_ReturnsStop()
    {
        var result = EntryBarExitResolver.Resolve("long", 101m, 98m, 99m, 102m);

        Assert.Equal(EntryBarExitKind.StopLoss, result.Kind);
        Assert.Equal(99m, result.ExitPrice);
        Assert.False(result.WasAmbiguous);
    }

    [Fact]
    public void Resolve_LongTargetOnly_ReturnsTarget()
    {
        var result = EntryBarExitResolver.Resolve("long", 103m, 100m, 99m, 102m);

        Assert.Equal(EntryBarExitKind.TakeProfit, result.Kind);
        Assert.Equal(102m, result.ExitPrice);
        Assert.False(result.WasAmbiguous);
    }

    [Fact]
    public void Resolve_LongBothTouched_FailsClosedToStop()
    {
        var result = EntryBarExitResolver.Resolve("long", 103m, 98m, 99m, 102m);

        Assert.Equal(EntryBarExitKind.StopLoss, result.Kind);
        Assert.Equal(99m, result.ExitPrice);
        Assert.True(result.WasAmbiguous);
    }

    [Fact]
    public void Resolve_ShortBothTouched_FailsClosedToStop()
    {
        var result = EntryBarExitResolver.Resolve("short", 103m, 97m, 102m, 98m);

        Assert.Equal(EntryBarExitKind.StopLoss, result.Kind);
        Assert.Equal(102m, result.ExitPrice);
        Assert.True(result.WasAmbiguous);
    }

    [Fact]
    public void Resolve_NoLevelTouched_ReturnsNone()
    {
        var result = EntryBarExitResolver.Resolve("short", 101m, 99m, 102m, 98m);

        Assert.Equal(EntryBarExitKind.None, result.Kind);
        Assert.Null(result.ExitPrice);
        Assert.False(result.WasAmbiguous);
    }
}
