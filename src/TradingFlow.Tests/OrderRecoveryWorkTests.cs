using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

/// <summary>The local "is there anything to recover" checks, each run through the real service and its own query.</summary>
public sealed class OrderRecoveryWorkTests
{
    private const string Account = "bound-account";
    private readonly Mock<IOrderIntentRepository> intents = new();
    private readonly Mock<IOrderEventRepository> events = new();
    private readonly Mock<IProtectiveStopReplacementRepository> replacements = new();
    private readonly Mock<IOrderDispatchRepository> dispatches = new();
    private readonly BrokerMutationCoordinator mutations = new();

    // Starts every test from an empty local journal.
    public OrderRecoveryWorkTests()
    {
        intents.Setup(value => value.ListUnpreparedPositionExitsAsync(Account, 1, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        replacements.Setup(value => value.ListRecoverableCommandIdsAsync(Account, 1, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        dispatches.Setup(value => value.ListRecoverableIntentIdsAsync(Account, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        SetEvents();
    }

    [Fact]
    public async Task EmptyJournal_HasNoWork()
    {
        Assert.False(await Commands().HasRecoverableWorkAsync(Account, default));
        Assert.False(await Dispatcher().HasRecoverableWorkAsync(Account));
    }

    [Fact]
    public async Task PendingDispatch_IsWork()
    {
        dispatches.Setup(value => value.ListRecoverableIntentIdsAsync(Account, It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid()]);
        Assert.True(await Dispatcher().HasRecoverableWorkAsync(Account));
    }

    [Fact]
    public async Task UnpreparedExit_IsWork()
    {
        intents.Setup(value => value.ListUnpreparedPositionExitsAsync(Account, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OrderIntentRecord { IntentId = Guid.NewGuid(), Kind = OrderIntentKind.PositionExit, AccountId = Account, Symbol = "MSFT" }]);
        Assert.True(await Commands().HasRecoverableWorkAsync(Account, default));
    }

    [Fact]
    public async Task PendingStopReplacement_IsWork()
    {
        replacements.Setup(value => value.ListRecoverableCommandIdsAsync(Account, 1, It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid()]);
        Assert.True(await Commands().HasRecoverableWorkAsync(Account, default));
    }

    // Only a cancel request that reached the broker counts; a working order or an unsent cancel does not.
    [Theory]
    [InlineData(OrderState.CancelPending, "broker-1", true)]
    [InlineData(OrderState.CancelPending, null, false)]
    [InlineData(OrderState.Submitted, "broker-1", false)]
    public async Task PendingCancellation_IsWorkOnlyWhenItReachedTheBroker(OrderState state, string? brokerOrderId, bool expected)
    {
        SetEvents(new OrderStateSnapshot(Guid.NewGuid(), "client-1", brokerOrderId, state, DateTimeOffset.UnixEpoch, null, 0m, null, 1));
        Assert.Equal(expected, await Commands().HasRecoverableWorkAsync(Account, default));
    }

    // A missing store reports work, so the full pass runs and raises its own configuration error.
    [Fact]
    public async Task MissingStores_ReportWork()
    {
        var service = new OrderSubmissionService(intents.Object, Mock.Of<IOrderDispatchService>(), Mock.Of<IEntryGateChain>(),
            NullLogger<OrderSubmissionService>.Instance);
        Assert.True(await service.HasRecoverableWorkAsync(Account, default));
    }

    // A shutdown rejects the check itself, as it rejects the full recovery pass.
    [Fact]
    public async Task Shutdown_RejectsTheCheck()
    {
        var service = Commands();
        await mutations.StopAcceptingAndExecuteAsync(_ => Task.FromResult(0), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<BrokerMutationRejectedException>(() => service.HasRecoverableWorkAsync(Account, default));
    }

    // The full pass still refuses an account other than the bound one before it lists any work.
    [Fact]
    public async Task FullPass_RefusesAnotherAccount()
    {
        var now = DateTimeOffset.UtcNow;
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BrokerAccountSnapshot(
            "other-account", "ACTIVE", false, false, false, true, 100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        await Assert.ThrowsAsync<BrokerAccountMismatchException>(() => Dispatcher().RecoverPendingAsync(broker.Object));
        dispatches.Verify(value => value.ListRecoverableIntentIdsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Makes the journal's open order states exactly the given ones.
    private void SetEvents(params OrderStateSnapshot[] snapshots) =>
        events.Setup(value => value.ListReconcilableAsync(Account, It.IsAny<CancellationToken>())).ReturnsAsync(snapshots);

    // The real command service over this test's repositories.
    private OrderSubmissionService Commands() => new(intents.Object, Mock.Of<IOrderDispatchService>(), Mock.Of<IEntryGateChain>(),
        NullLogger<OrderSubmissionService>.Instance, events: events.Object, lifecycle: Mock.Of<IOrderLifecycleService>(),
        stopReplacements: replacements.Object, brokerMutations: mutations);

    // The real dispatcher bound to the test account.
    private OrderDispatchService Dispatcher() => new(intents.Object, dispatches.Object, events.Object,
        Mock.Of<IPositionLedgerRepository>(), new BrokerAccountBindingService(new BrokerAccountBindingOptions(Account)),
        Mock.Of<IOrderLifecycleService>(), new OrderDispatchOptions(30, 30, 5, 1), TimeProvider.System,
        NullLogger<OrderDispatchService>.Instance, new EntryAdmissionControl());
}
