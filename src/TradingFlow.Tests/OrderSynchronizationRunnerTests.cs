using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSynchronizationRunnerTests
{
    [Fact]
    public async Task CancellationRace_FinalFillIsProtectedBeforeAbortUsesFinalQuantity()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runId = Guid.NewGuid();
        var clientOrderId = "SWGA-B-MSFT-20260721-001-12345678";
        var projectedFilledQuantity = 2m;
        var update = new OrderUpdate(
            "broker-1", clientOrderId, "MSFT", "buy",
            OrderStatus.PartiallyFilled, 2m, 100m, 2m, 2m,
            "execution-1", DateTimeOffset.UtcNow, BrokerUpdateSource.TradeStream);
        var streamer = new SingleUpdateStreamer(update);
        var factory = new Mock<ITradeUpdateStreamerFactory>();
        factory.Setup(item => item.Create()).Returns(streamer);
        var broker = new Mock<IBrokerClient>();
        var coordinator = new Mock<IOrderSynchronizationCoordinator>();
        coordinator.Setup(item => item.CrossCheckAsync(broker.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        coordinator.Setup(item => item.ProcessStreamUpdateAsync(update, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sequence = new List<string>();
        var reconciliation = new Mock<IAccountReconciliationService>();
        var reconciliationCalls = 0;
        reconciliation.Setup(item => item.ReconcileAsync(
                broker.Object,
                It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                sequence.Add($"protect:{projectedFilledQuantity}");
                if (Interlocked.Increment(ref reconciliationCalls) == 4)
                {
                    cancellation.Cancel();
                }
            })
            .ReturnsAsync(new AccountReconciliationResult(Guid.NewGuid(), "clean", [], false));
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord
            {
                RunId = runId,
                IntentId = Guid.NewGuid(),
                Kind = OrderIntentKind.StrategyEntry,
                ClientOrderId = clientOrderId,
                AccountId = "paper-account",
                StrategyId = "strategy",
                Symbol = "MSFT",
                Side = "buy",
                RequestedQuantity = 10m,
                RequestJson = "{\"horizon\":\"swing\"}"
            });
        intents.Setup(repository => repository.GetRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductionRun
            {
                RunId = runId,
                Profile = "paper",
                Status = "running",
                StartedAtUtc = DateTimeOffset.UtcNow,
                ConfigHash = "hash",
                CodeVersion = "version"
            });
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(service => service.RequestCancelAsync(
                It.IsAny<OrderCancellationSubmission>(),
                broker.Object,
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                sequence.Add("cancel");
                projectedFilledQuantity = 6m;
            })
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(), clientOrderId, "broker-1", OrderState.Canceled,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 6m, 100m, 1));
        commands.Setup(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(),
                broker.Object,
                It.IsAny<CancellationToken>()))
            .Callback<PositionExitSubmission, IBrokerClient, CancellationToken>(
                (request, _, _) => sequence.Add($"abort:{request.Quantity}"))
            .ReturnsAsync(new OrderSubmissionResult(
                "exit-broker", "exit-client", Guid.NewGuid(), DateTimeOffset.UtcNow));
        var admission = new EntryAdmissionControl();
        var runner = new OrderSynchronizationRunner(
            factory.Object,
            broker.Object,
            coordinator.Object,
            reconciliation.Object,
            new OrderSynchronizationOptions(15),
            TimeProvider.System,
            NullLogger<OrderSynchronizationRunner>.Instance,
            intents.Object,
            commands.Object,
            admission,
            new PartialFillExecutionOptions(70m, swingEntryWindowEnd: new TimeOnly(0, 0)));

        await runner.RunAsync(cancellation.Token);

        Assert.Equal(
            ["protect:2", "protect:2", "cancel", "protect:6", "abort:6", "protect:6"],
            sequence);
        Assert.DoesNotContain(
            admission.GetSnapshot().Blocks,
            block => block.Source.StartsWith("partial_entry_remainder_cancellation:", StringComparison.Ordinal) ||
                     block.Source.StartsWith("partial_entry_final_fill_protection:", StringComparison.Ordinal));
        commands.Verify(service => service.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.Reason == "partial_entry_fill_remainder"),
            broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
        commands.Verify(service => service.SubmitPositionExitAsync(
            It.Is<PositionExitSubmission>(request =>
                request.Symbol == "MSFT" &&
                request.Quantity == 6m &&
                request.Reason == "MIN_FILL_ABORT"),
            broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FillUpdate_TriggersImmediateCrossCheckAndAccountReconciliation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var update = new OrderUpdate(
            "broker-1",
            "SWGA-B-MSFT-20260721-001-12345678",
            "MSFT",
            "buy",
            OrderStatus.Filled,
            10m,
            100m,
            10m,
            10m,
            "execution-1",
            DateTimeOffset.UtcNow,
            BrokerUpdateSource.TradeStream);
        var streamer = new SingleUpdateStreamer(update);
        var factory = new Mock<ITradeUpdateStreamerFactory>();
        factory.Setup(item => item.Create()).Returns(streamer);
        var broker = new Mock<IBrokerClient>();
        var coordinator = new Mock<IOrderSynchronizationCoordinator>();
        coordinator.Setup(item => item.CrossCheckAsync(broker.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        coordinator.Setup(item => item.ProcessStreamUpdateAsync(update, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var reconciliation = new Mock<IAccountReconciliationService>();
        var reconciliationCalls = 0;
        reconciliation.Setup(item => item.ReconcileAsync(
                broker.Object,
                It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref reconciliationCalls) == 2)
                {
                    cancellation.Cancel();
                }
            })
            .ReturnsAsync(new AccountReconciliationResult(Guid.NewGuid(), "clean", [], false));
        var runner = new OrderSynchronizationRunner(
            factory.Object,
            broker.Object,
            coordinator.Object,
            reconciliation.Object,
            new OrderSynchronizationOptions(15),
            TimeProvider.System,
            NullLogger<OrderSynchronizationRunner>.Instance);

        await runner.RunAsync(cancellation.Token);

        coordinator.Verify(item => item.ProcessStreamUpdateAsync(update, It.IsAny<CancellationToken>()), Times.Once);
        coordinator.Verify(item => item.CrossCheckAsync(broker.Object, It.IsAny<CancellationToken>()), Times.Exactly(2));
        reconciliation.Verify(item => item.ReconcileAsync(
            broker.Object,
            It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private sealed class SingleUpdateStreamer(OrderUpdate update) : ITradeUpdateStreamer
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<OrderUpdate> ReadUpdatesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return update;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Dispose() { }
    }
}
