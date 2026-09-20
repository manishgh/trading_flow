using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class BrokerMutationCoordinatorTests
{
    [Fact]
    public async Task Shutdown_WaitsForActiveMutationAndRejectsEveryLaterMutation()
    {
        var coordinator = new BrokerMutationCoordinator();
        var active = coordinator.Enter("submit existing entry");
        var shutdownEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var shutdown = coordinator.StopAcceptingAndExecuteAsync(
            _ =>
            {
                shutdownEntered.SetResult();
                return Task.FromResult(42);
            },
            TimeSpan.FromSeconds(2));

        await Task.Delay(50);
        Assert.False(shutdownEntered.Task.IsCompleted);
        active.Dispose();

        Assert.Equal(42, await shutdown);
        Assert.True(shutdownEntered.Task.IsCompleted);
        Assert.Throws<BrokerMutationRejectedException>(() =>
            coordinator.Enter("late entry"));
    }

    [Fact]
    public async Task ShutdownAuthority_CanPerformNestedMutationsWhileExternalCallersRemainBlocked()
    {
        var coordinator = new BrokerMutationCoordinator();
        var nestedEntered = false;

        await coordinator.StopAcceptingAndExecuteAsync(
            _ =>
            {
                using var nested = coordinator.Enter("shutdown cancellation");
                nestedEntered = true;
                return Task.FromResult(0);
            },
            TimeSpan.FromSeconds(2));

        Assert.True(nestedEntered);
        Assert.Throws<BrokerMutationRejectedException>(() =>
            coordinator.Enter("external cancellation"));
    }
}
