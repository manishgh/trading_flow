using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Engine.Execution;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class OrderDispatchRecoveryHealthTests
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(50);

    // Hold the second recovery cycle open and drive retry and readiness timers explicitly.
    [Fact]
    public async Task WaitUntilReadyAsync_FailsClosedWhileANewRecoveryCycleIsInProgress()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
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
            new BrokerAccountBindingOptions(String.Empty, enforcementEnabled: false),
            time,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        Assert.False(admission.GetSnapshot().EntriesAllowed);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.WaitUntilReadyAsync(TimeSpan.FromMinutes(1), watchdog.Token);
            var startSecondCycle = await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
            startSecondCycle();
            await secondCycleStarted.Task.WaitAsync(watchdog.Token);

            var inProgress = service.GetHealth();
            Assert.True(inProgress.InitialCycleCompleted);
            Assert.False(inProgress.Healthy);
            Assert.False(admission.GetSnapshot().EntriesAllowed);
            Assert.Contains("in progress", inProgress.Detail, StringComparison.OrdinalIgnoreCase);
            await RequireReadinessTimeoutAsync(service, time, watchdog.Token);

            releaseSecondCycle.TrySetResult(0);
            await service.WaitUntilReadyAsync(TimeSpan.FromMinutes(1), watchdog.Token);
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
        }
        finally
        {
            releaseSecondCycle.TrySetResult(0);
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private sealed class RecoveryTestTimeProvider : TimeProvider
    {
        private readonly Channel<ManualTimer> registered = Channel.CreateUnbounded<ManualTimer>();
        private long utcTicks = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero).UtcTicks;

        // The test advances UTC only when releasing a registered timer.
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        // Task.Delay and WaitAsync schedule one-shot timers, which stay pending until the test advances them.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            registered.Writer.TryWrite(timer);
            return timer;
        }

        // Observe registration first, since the hosted service signals health before scheduling its next recovery.
        public async Task<Action> WaitForTimerAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            while (true)
            {
                var timer = await registered.Reader.ReadAsync(cancellationToken);
                if (timer.DueTime == delay)
                {
                    return () =>
                    {
                        Interlocked.Add(ref utcTicks, delay.Ticks);
                        timer.Fire();
                    };
                }
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private int finished;
            public TimeSpan DueTime { get; } = dueTime;

            // Deliver at most one callback, respecting disposal by a completed or canceled task.
            public void Fire()
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                    callback(state);
            }

            // Task timers in this test are one-shot and never rescheduled.
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            // Prevent a canceled task from receiving a later timer callback.
            public void Dispose() => Interlocked.Exchange(ref finished, 1);

            // The timer has no asynchronous resources to drain.
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // Wait for the failed cycle's retry registration so its final health and admission state are observable.
    [Fact]
    public async Task WaitUntilReadyAsync_FailsClosedAfterAHealthyCycleBecomesUnhealthy()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
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
            new BrokerAccountBindingOptions(String.Empty, enforcementEnabled: false),
            time,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.WaitUntilReadyAsync(TimeSpan.FromMinutes(1), watchdog.Token);
            var startSecondCycle = await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
            startSecondCycle();
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);

            var error = await RequireReadinessTimeoutAsync(service, time, watchdog.Token);
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

    // Inspect the failed first cycle after all recovery categories and the health update have finished.
    [Fact]
    public async Task FailedRecoveryCategory_DoesNotStarveLaterSafetyCriticalCategories()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(value => value.RecoverUnpreparedPositionExitsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("exit recovery failed"));
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
            new BrokerAccountBindingOptions(String.Empty, enforcementEnabled: false),
            time,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);

            commands.Verify(value => value.RecoverPendingCancellationsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), Times.Once);
            commands.Verify(value => value.RecoverPendingProtectiveStopReplacementsAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), Times.Once);

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

    // After one healthy full pass, an empty local journal must skip every broker-reading recovery yet stay healthy.
    [Fact]
    public async Task IdleCycle_AfterAHealthyFullPass_DoesNotRunBrokerRecoveries()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
        var (commands, dispatcher) = RecoveryMocks();
        var service = CreateService(commands, dispatcher, time, out var admission);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var startSecond = await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            VerifyFullPasses(commands, dispatcher, Times.Once());
            startSecond();
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);

            VerifyFullPasses(commands, dispatcher, Times.Once());
            dispatcher.Verify(value => value.HasRecoverableWorkAsync("bound-account", It.IsAny<CancellationToken>()), Times.Once);
            commands.Verify(value => value.HasRecoverableWorkAsync("bound-account", It.IsAny<CancellationToken>()), Times.Once);
            Assert.True(service.GetHealth().Healthy);
            Assert.Contains("broker not contacted. Last full pass 2026-07-21 15:00:00 UTC.", service.GetHealth().Detail, StringComparison.Ordinal);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // Local work in either service must bring back the full pass, which validates the account before acting.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CycleWithLocalWork_RunsTheFullPass(bool dispatchWork, bool commandWork)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
        var (commands, dispatcher) = RecoveryMocks();
        dispatcher.Setup(value => value.HasRecoverableWorkAsync("bound-account", It.IsAny<CancellationToken>())).ReturnsAsync(dispatchWork);
        commands.Setup(value => value.HasRecoverableWorkAsync("bound-account", It.IsAny<CancellationToken>())).ReturnsAsync(commandWork);
        var service = CreateService(commands, dispatcher, time, out _);
        await service.StartAsync(CancellationToken.None);
        try
        {
            (await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token))();
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            VerifyFullPasses(commands, dispatcher, Times.Exactly(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // A refused account must be retried in full, and stay blocked, even when the local journal looks empty.
    [Fact]
    public async Task CycleAfterARefusedAccount_RunsTheFullPassAgain()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
        var (commands, dispatcher) = RecoveryMocks();
        dispatcher.SetupSequence(value => value.RecoverPendingAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BrokerAccountMismatchException("bound-account", "other-account"))
            .ReturnsAsync(0);
        var service = CreateService(commands, dispatcher, time, out var admission);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var startSecond = await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            Assert.False(service.GetHealth().Healthy);
            Assert.False(admission.GetSnapshot().EntriesAllowed);
            startSecond();
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            VerifyFullPasses(commands, dispatcher, Times.Exactly(2));
            Assert.True(service.GetHealth().Healthy);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // A local journal that cannot be read must block entries, as a failed full pass does.
    [Fact]
    public async Task UnreadableLocalJournal_FailsClosed()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var time = new RecoveryTestTimeProvider();
        var (commands, dispatcher) = RecoveryMocks();
        commands.SetupSequence(value => value.HasRecoverableWorkAsync("bound-account", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("journal unreadable"))
            .ReturnsAsync(false);
        var service = CreateService(commands, dispatcher, time, out var admission);
        await service.StartAsync(CancellationToken.None);
        try
        {
            (await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token))();
            var startThird = await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            Assert.False(service.GetHealth().Healthy);
            Assert.Contains("journal unreadable", service.GetHealth().Detail, StringComparison.Ordinal);
            Assert.False(admission.GetSnapshot().EntriesAllowed);
            VerifyFullPasses(commands, dispatcher, Times.Once());

            // The cycle after the failed check runs the full pass without trusting the local journal.
            startThird();
            await time.WaitForTimerAsync(RecoveryInterval, watchdog.Token);
            VerifyFullPasses(commands, dispatcher, Times.Exactly(2));
            Assert.True(service.GetHealth().Healthy);
            Assert.True(admission.GetSnapshot().EntriesAllowed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // The label follows the async flow below its scope, restores the outer label, and is absent outside any scope.
    [Fact]
    public async Task BrokerCallOrigin_LabelsOnlyItsOwnAsyncFlow()
    {
        Assert.Null(BrokerCallOrigin.Current);
        using (BrokerCallOrigin.Begin(BrokerCallOrigin.OrderRecovery))
        {
            await Task.Yield();
            Assert.Equal(BrokerCallOrigin.OrderRecovery, BrokerCallOrigin.Current);
            using (BrokerCallOrigin.Begin(BrokerCallOrigin.OrderSynchronization))
            {
                Assert.Equal(BrokerCallOrigin.OrderSynchronization, await Task.Run(() => BrokerCallOrigin.Current));
            }

            Assert.Equal(BrokerCallOrigin.OrderRecovery, BrokerCallOrigin.Current);
        }

        Assert.Null(BrokerCallOrigin.Current);
    }

    // Recoveries that succeed with nothing to do, and an empty local journal, unless a test overrides them.
    private static (Mock<IOrderCommandService> Commands, Mock<IOrderDispatchService> Dispatcher) RecoveryMocks()
    {
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(value => value.RecoverUnpreparedPositionExitsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingCancellationsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        commands.Setup(value => value.RecoverPendingProtectiveStopReplacementsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var dispatcher = new Mock<IOrderDispatchService>();
        dispatcher.Setup(value => value.RecoverPendingAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return (commands, dispatcher);
    }

    // The hosted service bound to one enforced account, on the manual clock.
    private static OrderDispatchRecoveryHostedService CreateService(Mock<IOrderCommandService> commands,
        Mock<IOrderDispatchService> dispatcher, RecoveryTestTimeProvider time, out EntryAdmissionControl admission)
    {
        admission = new EntryAdmissionControl();
        return new OrderDispatchRecoveryHostedService(dispatcher.Object, commands.Object, Mock.Of<IBrokerClient>(), admission,
            new OrderDispatchOptions(10, 10, 1, 1), new BrokerAccountBindingOptions("bound-account"), time,
            NullLogger<OrderDispatchRecoveryHostedService>.Instance);
    }

    // Every broker-reading recovery ran the given number of times.
    private static void VerifyFullPasses(Mock<IOrderCommandService> commands, Mock<IOrderDispatchService> dispatcher, Times times)
    {
        commands.Verify(value => value.RecoverUnpreparedPositionExitsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), times);
        commands.Verify(value => value.RecoverPendingCancellationsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), times);
        commands.Verify(value => value.RecoverPendingProtectiveStopReplacementsAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), times);
        dispatcher.Verify(value => value.RecoverPendingAsync(It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), times);
    }

    // Prove readiness remains pending until its own test-controlled timeout fires.
    private static async Task<TimeoutException> RequireReadinessTimeoutAsync(
        OrderDispatchRecoveryHostedService service, RecoveryTestTimeProvider time, CancellationToken cancellationToken)
    {
        var awaitingReadiness = service.WaitUntilReadyAsync(ReadinessTimeout, cancellationToken);
        var expireReadiness = await time.WaitForTimerAsync(ReadinessTimeout, cancellationToken);
        Assert.False(awaitingReadiness.IsCompleted);
        expireReadiness();
        return await Assert.ThrowsAsync<TimeoutException>(() => awaitingReadiness);
    }
}
