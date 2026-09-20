using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OrderDispatchRecoveryHealthTests
{
    [Fact]
    public async Task WaitUntilReadyAsync_FailsClosedWhileANewRecoveryCycleIsInProgress()
    {
        var secondCycleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCycle = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryCycleCount = 0;
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(value => value.RecoverUnpreparedPositionExitsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Returns((IBrokerClient _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref recoveryCycleCount) == 1)
                {
                    return Task.FromResult(0);
                }

                secondCycleStarted.TrySetResult();
                return releaseSecondCycle.Task.WaitAsync(token);
            });
        commands.Setup(value => value.RecoverPendingCancellationsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingProtectiveStopReplacementsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var dispatcher = new Mock<IOrderDispatchService>();
        dispatcher.Setup(value => value.RecoverPendingAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var admission = new EntryAdmissionControl();
        var service = new OrderDispatchRecoveryHostedService(
            dispatcher.Object,
            commands.Object,
            Mock.Of<IBrokerClient>(),
            admission,
            new OrderDispatchOptions(10, 10, 1, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        Assert.False(admission.GetSnapshot().EntriesAllowed);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.WaitUntilReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
            await secondCycleStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var inProgress = service.GetHealth();
            Assert.True(inProgress.InitialCycleCompleted);
            Assert.False(inProgress.Healthy);
            Assert.False(admission.GetSnapshot().EntriesAllowed);
            Assert.Contains("in progress", inProgress.Detail, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                service.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

            releaseSecondCycle.TrySetResult(0);
            await service.WaitUntilReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
        }
        finally
        {
            releaseSecondCycle.TrySetResult(0);
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task WaitUntilReadyAsync_FailsClosedAfterAHealthyCycleBecomesUnhealthy()
    {
        var dispatcher = new Mock<IOrderDispatchService>();
        dispatcher
            .SetupSequence(value => value.RecoverPendingAsync(
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0)
            .ThrowsAsync(new IOException("recovery store unavailable"));
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(value => value.RecoverUnpreparedPositionExitsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingCancellationsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingProtectiveStopReplacementsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var admission = new EntryAdmissionControl();
        var service = new OrderDispatchRecoveryHostedService(
            dispatcher.Object,
            commands.Object,
            Mock.Of<IBrokerClient>(),
            admission,
            new OrderDispatchOptions(10, 10, 1, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.WaitUntilReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            await WaitUntilAsync(
                () => service.GetHealth() is { InitialCycleCompleted: true, Healthy: false },
                TimeSpan.FromSeconds(3));

            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                service.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Contains("recovery store unavailable", error.Message, StringComparison.Ordinal);
            Assert.False(admission.GetSnapshot().EntriesAllowed);
            Assert.Contains(
                admission.GetSnapshot().Blocks,
                block => block.Code == "DURABLE_ORDER_RECOVERY_FAILED");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task FailedRecoveryCategory_DoesNotStarveLaterSafetyCriticalCategories()
    {
        var cancellationRecoveryRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementRecoveryRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(value => value.RecoverUnpreparedPositionExitsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("exit recovery failed"));
        commands.Setup(value => value.RecoverPendingCancellationsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback(() => cancellationRecoveryRan.TrySetResult())
            .ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingProtectiveStopReplacementsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback(() => replacementRecoveryRan.TrySetResult())
            .ReturnsAsync(0);
        var dispatcher = new Mock<IOrderDispatchService>();
        dispatcher.Setup(value => value.RecoverPendingAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var admission = new EntryAdmissionControl();
        var service = new OrderDispatchRecoveryHostedService(
            dispatcher.Object,
            commands.Object,
            Mock.Of<IBrokerClient>(),
            admission,
            new OrderDispatchOptions(10, 10, 1, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await cancellationRecoveryRan.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await replacementRecoveryRan.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var health = service.GetHealth();
            Assert.True(health.InitialCycleCompleted);
            Assert.False(health.Healthy);
            Assert.Contains("exit recovery failed", health.Detail, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected recovery health state was not observed.");
            }

            await Task.Delay(20);
        }
    }
}
