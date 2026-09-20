using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderDispatchFailureTests
{
    [Fact]
    public async Task RecoverUnpreparedPositionExitsAsync_PerIntentFailureMakesRecoveryIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new OrderIntentRecord
        {
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.PositionExit,
            AccountId = "paper-account",
            Symbol = "MSFT",
            ClientOrderId = "exit-msft",
            RequestedQuantity = 10m,
            CreatedAtUtc = now
        };
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.ListUnpreparedPositionExitsAsync(
                "paper-account", 1_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([intent]);
        intents.Setup(repository => repository.ListUnpreparedPositionExitsAsync(
                "paper-account", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([intent]);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var service = new OrderSubmissionService(
            intents.Object,
            Mock.Of<IOrderDispatchService>(),
            Mock.Of<IEntryGateChain>(),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
            service.RecoverUnpreparedPositionExitsAsync(broker.Object, CancellationToken.None));

        Assert.Equal("Position-exit preparation", error.Operation);
        Assert.Equal(0, error.RecoveredCount);
        Assert.Equal(1, error.FailedCount);
    }

    [Fact]
    public async Task RecoverProtectiveStopReplacementsAsync_UnavailableLeaseMakesRecoveryIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var replacements = new Mock<IProtectiveStopReplacementRepository>();
        replacements.Setup(repository => repository.ListRecoverableCommandIdsAsync(
                "paper-account", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([commandId]);
        replacements.Setup(repository => repository.ListRecoverableCommandIdsAsync(
                "paper-account", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([commandId]);
        replacements.Setup(repository => repository.TryAcquireLeaseAsync(
                commandId,
                "paper-account",
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtectiveStopReplacementLease?)null);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var service = new OrderSubmissionService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IOrderDispatchService>(),
            Mock.Of<IEntryGateChain>(),
            NullLogger<OrderSubmissionService>.Instance,
            stopReplacements: replacements.Object);

        var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
            service.RecoverPendingProtectiveStopReplacementsAsync(
                broker.Object,
                CancellationToken.None));

        Assert.Equal("Protective-stop replacement", error.Operation);
        Assert.Equal(0, error.RecoveredCount);
        Assert.Equal(1, error.PendingCount);
    }

    [Fact]
    public async Task RecoverUnpreparedPositionExitsAsync_RemainingPageKeepsRecoveryIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var remainingIntent = new OrderIntentRecord
        {
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.PositionExit,
            AccountId = "paper-account",
            Symbol = "MSFT",
            ClientOrderId = "remaining-exit-msft",
            CreatedAtUtc = now
        };
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.ListUnpreparedPositionExitsAsync(
                "paper-account", 1_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        intents.Setup(repository => repository.ListUnpreparedPositionExitsAsync(
                "paper-account", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([remainingIntent]);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var service = new OrderSubmissionService(
            intents.Object,
            Mock.Of<IOrderDispatchService>(),
            Mock.Of<IEntryGateChain>(),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
            service.RecoverUnpreparedPositionExitsAsync(broker.Object, CancellationToken.None));

        Assert.Equal(1, error.PendingCount);
        Assert.Equal(0, error.FailedCount);
    }

    [Fact]
    public async Task RecoverProtectiveStopReplacementsAsync_RemainingPageKeepsRecoveryIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var replacements = new Mock<IProtectiveStopReplacementRepository>();
        replacements.Setup(repository => repository.ListRecoverableCommandIdsAsync(
                "paper-account", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        replacements.Setup(repository => repository.ListRecoverableCommandIdsAsync(
                "paper-account", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guid.NewGuid()]);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var service = new OrderSubmissionService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IOrderDispatchService>(),
            Mock.Of<IEntryGateChain>(),
            NullLogger<OrderSubmissionService>.Instance,
            stopReplacements: replacements.Object);

        var error = await Assert.ThrowsAsync<BrokerCommandRecoveryIncompleteException>(() =>
            service.RecoverPendingProtectiveStopReplacementsAsync(
                broker.Object,
                CancellationToken.None));

        Assert.Equal(1, error.PendingCount);
        Assert.Equal(0, error.FailedCount);
    }

    [Fact]
    public async Task RecoverPendingAsync_PerIntentFailureMakesRecoveryCycleIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(intentId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("intent store read failed"));
        var dispatches = new Mock<IOrderDispatchRepository>();
        dispatches.Setup(repository => repository.ListRecoverableIntentIdsAsync(
                "paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([intentId]);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var service = new OrderDispatchService(
            intents.Object,
            dispatches.Object,
            Mock.Of<IOrderEventRepository>(),
            Mock.Of<IPositionLedgerRepository>(),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions("paper-account")),
            Mock.Of<IOrderLifecycleService>(),
            new OrderDispatchOptions(30, 30, 5, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchService>.Instance);

        var error = await Assert.ThrowsAsync<OrderDispatchRecoveryIncompleteException>(() =>
            service.RecoverPendingAsync(broker.Object));

        Assert.Equal(0, error.PendingCount);
        Assert.Equal(1, error.FailedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerAcceptedButAcknowledgementJournalFailed_BlocksAllNewEntries(
        bool cancellationFailure)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new OrderIntentRecord
        {
            RunId = Guid.NewGuid(),
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.StrategyEntry,
            ClientOrderId = "SWGA-B-MSFT-20260721-001-12345678",
            AccountId = "paper-account",
            StrategyId = "SWGA",
            Symbol = "MSFT",
            Side = "buy",
            OrderType = "limit",
            TimeInForce = "day",
            RequestedQuantity = 10m,
            LimitPrice = 100m,
            StopPrice = 98m,
            CreatedAtUtc = now,
            RequestJson = "{\"takeProfitPrice\":104,\"submitOutsideRegularHours\":false}"
        };
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(intent.IntentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var dispatches = new Mock<IOrderDispatchRepository>();
        var lease = new OrderDispatchLease(intent, OrderState.Intent, Guid.NewGuid(), now.AddSeconds(30));
        dispatches.Setup(repository => repository.TryAcquireDispatchLeaseAsync(
                intent.IntentId, intent.AccountId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease);
        dispatches.Setup(repository => repository.RecordDispatchAttemptAsync(
                intent.IntentId, lease.LeaseToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                intent.DispatchAttemptCount = 1;
                intent.LastDispatchAttemptAtUtc = now;
                return intent;
            });
        dispatches.Setup(repository => repository.ReleaseDispatchLeaseAsync(
                intent.IntentId, lease.LeaseToken, CancellationToken.None))
            .Returns(Task.CompletedTask);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                intent.RunId, intent.ClientOrderId, null, OrderState.Intent,
                now, null, 0m, null, 1));
        var transitionCount = 0;
        events.Setup(repository => repository.TransitionAsync(
                It.IsAny<OrderTransitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrderTransitionRequest, CancellationToken>((request, _) =>
            {
                transitionCount++;
                if (transitionCount == 2)
                {
                    throw cancellationFailure
                        ? new OperationCanceledException("journal canceled")
                        : new InvalidOperationException("disk full");
                }

                return Task.FromResult(new OrderTransitionResult(
                    new OrderStateSnapshot(
                        intent.RunId, intent.ClientOrderId, null, request.NewState,
                        request.LocalTimestampUtc, request.BrokerTimestampUtc,
                        request.FilledQuantity, request.FillPrice, 2),
                    true));
            });
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId, "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        broker.Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt("broker-1", now));
        var admission = new EntryAdmissionControl();
        var service = new OrderDispatchService(
            intents.Object,
            dispatches.Object,
            events.Object,
            Mock.Of<IPositionLedgerRepository>(),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions(intent.AccountId)),
            Mock.Of<IOrderLifecycleService>(),
            new OrderDispatchOptions(30, 30, 5, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchService>.Instance,
            admission);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.DispatchAsync(intent.IntentId, broker.Object));

        Assert.Equal(cancellationFailure ? "journal canceled" : "disk full", error.Message);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Once);
        var block = Assert.Single(admission.GetSnapshot().Blocks);
        Assert.Equal("BROKER_ACK_JOURNAL_FAILED", block.Code);
        Assert.Contains(intent.ClientOrderId, block.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryAdmittedByEarlierGateButNotYetDispatched_IsRejectedAtAtomicDispatchFence()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new OrderIntentRecord
        {
            RunId = Guid.NewGuid(),
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.StrategyEntry,
            ClientOrderId = "SWGA-B-MSFT-20260906-001-12345678",
            AccountId = "paper-account",
            StrategyId = "SWGA",
            Symbol = "MSFT",
            Side = "buy",
            OrderType = "limit",
            TimeInForce = "day",
            RequestedQuantity = 10m,
            LimitPrice = 100m,
            StopPrice = 98m,
            CreatedAtUtc = now,
            RequestJson = "{\"takeProfitPrice\":104,\"submitOutsideRegularHours\":false}"
        };
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(intent.IntentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var dispatches = new Mock<IOrderDispatchRepository>();
        var lease = new OrderDispatchLease(intent, OrderState.Intent, Guid.NewGuid(), now.AddSeconds(30));
        dispatches.Setup(repository => repository.TryAcquireDispatchLeaseAsync(
                intent.IntentId, intent.AccountId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease);
        dispatches.Setup(repository => repository.ReleaseDispatchLeaseAsync(
                intent.IntentId, lease.LeaseToken, CancellationToken.None))
            .Returns(Task.CompletedTask);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                intent.RunId, intent.ClientOrderId, null, OrderState.Intent,
                now, null, 0m, null, 1));
        events.Setup(repository => repository.TransitionAsync(
                It.IsAny<OrderTransitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrderTransitionRequest, CancellationToken>((request, _) => Task.FromResult(
                new OrderTransitionResult(
                    new OrderStateSnapshot(
                        intent.RunId, intent.ClientOrderId, null, request.NewState,
                        request.LocalTimestampUtc, request.BrokerTimestampUtc,
                        request.FilledQuantity, request.FillPrice, 2),
                    true)));
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId, "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var brokerMutations = new BrokerMutationCoordinator();
        var service = new OrderDispatchService(
            intents.Object,
            dispatches.Object,
            events.Object,
            Mock.Of<IPositionLedgerRepository>(),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions(intent.AccountId)),
            Mock.Of<IOrderLifecycleService>(),
            new OrderDispatchOptions(30, 30, 5, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchService>.Instance,
            new EntryAdmissionControl(),
            brokerMutations);

        await brokerMutations.StopAcceptingAndExecuteAsync(
            _ => Task.FromResult(0),
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<BrokerMutationRejectedException>(() =>
            service.DispatchAsync(intent.IntentId, broker.Object));
        dispatches.Verify(repository => repository.TryAcquireDispatchLeaseAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        dispatches.Verify(repository => repository.RecordDispatchAttemptAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PersistedEntryRetry_IsRejectedAtFinalAdmissionFence()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = CreatePendingEntry(now, "SWGA-B-MSFT-20260916-001-12345678");
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                intent.IntentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                intent.RunId, intent.ClientOrderId, null, OrderState.Intent,
                now, null, 0m, null, 1));
        var lease = new OrderDispatchLease(
            intent,
            OrderState.Intent,
            Guid.NewGuid(),
            now.AddSeconds(30));
        var dispatches = new Mock<IOrderDispatchRepository>();
        dispatches.Setup(repository => repository.TryAcquireDispatchLeaseAsync(
                intent.IntentId, intent.AccountId, It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease);
        dispatches.Setup(repository => repository.ReleaseDispatchLeaseAsync(
                intent.IntentId, lease.LeaseToken, CancellationToken.None))
            .Returns(Task.CompletedTask);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId, "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var admission = new EntryAdmissionControl();
        admission.Block(
            "durable_order_recovery",
            "DURABLE_ORDER_RECOVERY_IN_PROGRESS",
            "Recovery is active.",
            now);
        var service = CreateDispatcher(
            intent.AccountId,
            intents.Object,
            dispatches.Object,
            events.Object,
            admission);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DispatchAsync(intent.IntentId, broker.Object));

        Assert.Contains("DURABLE_ORDER_RECOVERY_IN_PROGRESS", error.Message, StringComparison.Ordinal);
        dispatches.Verify(repository => repository.RecordDispatchAttemptAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoveryDispatch_BypassesEntryFenceButRetainsDurableLeaseFence()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = CreatePendingEntry(now, "SWGA-B-MSFT-20260916-002-12345678");
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                intent.IntentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                intent.RunId, intent.ClientOrderId, null, OrderState.Intent,
                now, null, 0m, null, 1));
        var dispatches = new Mock<IOrderDispatchRepository>();
        dispatches.Setup(repository => repository.ListRecoverableIntentIdsAsync(
                intent.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([intent.IntentId]);
        dispatches.Setup(repository => repository.TryAcquireDispatchLeaseAsync(
                intent.IntentId, intent.AccountId, It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderDispatchLease?)null);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId, "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        var admission = new EntryAdmissionControl();
        admission.Block(
            "durable_order_recovery",
            "DURABLE_ORDER_RECOVERY_IN_PROGRESS",
            "Recovery is active.",
            now);
        var service = CreateDispatcher(
            intent.AccountId,
            intents.Object,
            dispatches.Object,
            events.Object,
            admission);

        var error = await Assert.ThrowsAsync<OrderDispatchRecoveryIncompleteException>(() =>
            service.RecoverPendingAsync(broker.Object));

        Assert.Equal(1, error.PendingCount);
        dispatches.Verify(repository => repository.TryAcquireDispatchLeaseAsync(
            intent.IntentId, intent.AccountId, It.IsAny<string>(),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecoveryDispatch_IndependentSafetyBlockPreventsFreshEntryPost()
    {
        var now = DateTimeOffset.UtcNow;
        var intent = CreatePendingEntry(now, "SWGA-B-MSFT-20260916-003-12345678");
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                intent.IntentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(intent);
        var events = new Mock<IOrderEventRepository>();
        events.Setup(repository => repository.GetCurrentAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                intent.RunId, intent.ClientOrderId, null, OrderState.Intent,
                now, null, 0m, null, 1));
        var lease = new OrderDispatchLease(
            intent,
            OrderState.Intent,
            Guid.NewGuid(),
            now.AddSeconds(30));
        var dispatches = new Mock<IOrderDispatchRepository>();
        dispatches.Setup(repository => repository.ListRecoverableIntentIdsAsync(
                intent.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([intent.IntentId]);
        dispatches.Setup(repository => repository.TryAcquireDispatchLeaseAsync(
                intent.IntentId, intent.AccountId, It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lease);
        dispatches.Setup(repository => repository.ReleaseDispatchLeaseAsync(
                intent.IntentId, lease.LeaseToken, CancellationToken.None))
            .Returns(Task.CompletedTask);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                intent.AccountId, "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now));
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveBrokerOrder?)null);
        var admission = new EntryAdmissionControl();
        admission.Block(
            EntryAdmissionSources.DurableOrderRecovery,
            "DURABLE_ORDER_RECOVERY_IN_PROGRESS",
            "Recovery is active.",
            now);
        admission.Block(
            "account_reconciliation",
            "RECONCILE_MISMATCH",
            "Account mismatch requires acknowledgement.",
            now);
        var service = CreateDispatcher(
            intent.AccountId,
            intents.Object,
            dispatches.Object,
            events.Object,
            admission);

        var error = await Assert.ThrowsAsync<OrderDispatchRecoveryIncompleteException>(() =>
            service.RecoverPendingAsync(broker.Object));

        Assert.Equal(1, error.FailedCount);
        dispatches.Verify(repository => repository.RecordDispatchAttemptAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        events.Verify(repository => repository.TransitionAsync(
            It.IsAny<OrderTransitionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        broker.Verify(client => client.SubmitOrderAsync(
            It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static OrderIntentRecord CreatePendingEntry(DateTimeOffset now, string clientOrderId) => new()
    {
        RunId = Guid.NewGuid(),
        IntentId = Guid.NewGuid(),
        Kind = OrderIntentKind.StrategyEntry,
        ClientOrderId = clientOrderId,
        AccountId = "paper-account",
        StrategyId = "SWGA",
        Symbol = "MSFT",
        Side = "buy",
        OrderType = "limit",
        TimeInForce = "day",
        RequestedQuantity = 10m,
        LimitPrice = 100m,
        StopPrice = 98m,
        CreatedAtUtc = now,
        RequestJson = "{\"takeProfitPrice\":104,\"submitOutsideRegularHours\":false}"
    };

    private static OrderDispatchService CreateDispatcher(
        string accountId,
        IOrderIntentRepository intents,
        IOrderDispatchRepository dispatches,
        IOrderEventRepository events,
        IEntryAdmissionControl admission) => new(
            intents,
            dispatches,
            events,
            Mock.Of<IPositionLedgerRepository>(),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions(accountId)),
            Mock.Of<IOrderLifecycleService>(),
            new OrderDispatchOptions(30, 30, 5, 1),
            TimeProvider.System,
            NullLogger<OrderDispatchService>.Instance,
            admission);
}
