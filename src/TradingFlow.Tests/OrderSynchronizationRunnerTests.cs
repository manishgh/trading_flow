using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSynchronizationRunnerTests
{
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
            new AccountReconciliationOptions(60, 30),
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
