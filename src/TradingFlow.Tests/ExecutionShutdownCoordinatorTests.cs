using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class ExecutionShutdownCoordinatorTests
{
    [Fact]
    public async Task Shutdown_CancelsOpeningOrdersButPreservesProtectiveOrders()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(true, false));
        var entry = Intent(OrderIntentKind.StrategyEntry, "entry-client", "MSFT");
        var stop = Intent(OrderIntentKind.ProtectiveStop, "stop-client", "MSFT");
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync("entry-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync("stop-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stop);
        fixture.Broker.SetupSequence(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Order("entry-broker", "entry-client", "buy", "limit")])
            .ReturnsAsync([Order("stop-broker", "stop-client", "sell", "stop")]);
        fixture.Commands.Setup(service => service.RequestCancelAsync(
                It.IsAny<OrderCancellationSubmission>(),
                fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("entry-client", "entry-broker", OrderState.Canceled));

        var result = await fixture.Service.ShutdownAsync(fixture.Broker.Object);

        Assert.Equal(1, result.NonProtectiveOrdersCanceled);
        fixture.Commands.Verify(service => service.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "entry-client"),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Commands.Verify(service => service.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "stop-client"),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
    }

    [Fact]
    public async Task Shutdown_PreservesBrokerGeneratedBracketChildrenAndCompletesProtectionCheck()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(true, false));
        var entry = Intent(OrderIntentKind.StrategyEntry, "entry-client", "MSFT");
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync(
                "entry-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        var child = new ActiveBrokerOrder(
            "stop-leg-broker",
            "MSFT",
            "sell",
            "new",
            "stop",
            null,
            95m,
            10m,
            DateTimeOffset.UtcNow,
            "broker-generated-stop-client",
            0m,
            null,
            DateTimeOffset.UtcNow,
            ParentClientOrderId: "entry-client",
            TimeInForce: "gtc",
            OrderClass: "bracket",
            ExtendedHours: false);
        fixture.Broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([child]);
        fixture.Synchronization.Setup(service => service.CrossCheckAsync(
                fixture.Broker.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync([child]);

        var result = await fixture.Service.ShutdownAsync(fixture.Broker.Object);

        Assert.Equal(0, result.NonProtectiveOrdersCanceled);
        fixture.Commands.Verify(service => service.RequestCancelAsync(
            It.IsAny<OrderCancellationSubmission>(),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Protection.Verify(service => service.EnsureAsync(
            fixture.Broker.Object,
            "paper-account",
            It.IsAny<IReadOnlyList<BrokerPosition>>(),
            It.Is<IReadOnlyList<ActiveBrokerOrder>>(orders => orders.Single() == child),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_FlattensSwingPositionButRetainsUnsupportedLegacyPosition()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(true, false));
        var dayPosition = Position("MSFT", "day-client", 11, 10m);
        var swingPosition = Position("NVDA", "swing-client", 12, 4m);
        fixture.Positions.Setup(repository => repository.ListCurrentAsync("paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([dayPosition, swingPosition]);
        var dayIntent = Intent(OrderIntentKind.StrategyEntry, "day-client", "MSFT", "day");
        var swingIntent = Intent(OrderIntentKind.StrategyEntry, "swing-client", "NVDA", "swing");
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync("day-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dayIntent);
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync("swing-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(swingIntent);
        fixture.Intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fixture.Intents.Setup(repository => repository.GetRunAsync(swingIntent.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run(swingIntent.RunId));
        fixture.Commands.Setup(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(),
                fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult("exit", "exit-client", Guid.NewGuid(), DateTimeOffset.UtcNow));
        fixture.OrderEvents.Setup(repository => repository.GetCurrentAsync(
                "exit-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("exit-client", "exit", OrderState.Filled));
        fixture.Positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "NVDA", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);

        var result = await fixture.Service.ShutdownAsync(fixture.Broker.Object);

        Assert.Equal(1, result.PositionsFlattened);
        Assert.Equal(1, result.PositionsRetained);
        fixture.Commands.Verify(service => service.SubmitPositionExitAsync(
            It.Is<PositionExitSubmission>(request => request.Symbol == "NVDA" && request.Quantity == 4m),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Commands.Verify(service => service.SubmitPositionExitAsync(
            It.Is<PositionExitSubmission>(request => request.Symbol == "MSFT"),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_FlushesJournalWhenBrokerDrainFails()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(true, false));
        fixture.Broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ShutdownAsync(fixture.Broker.Object));

        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_CancelsUnfilledFlattenAndRestoresProtection()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(
            true, false, timeoutSeconds: 10,
            exitFillConfirmationTimeoutSeconds: 1,
            exitFillPollIntervalMilliseconds: 25));
        var position = Position("MSFT", "day-client", 11, 10m);
        var entry = Intent(OrderIntentKind.StrategyEntry, "day-client", "MSFT", "swing");
        fixture.Positions.Setup(repository => repository.ListCurrentAsync(
                "paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([position]);
        fixture.Positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(position);
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync(
                "day-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        fixture.Intents.Setup(repository => repository.GetRunAsync(
                entry.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run(entry.RunId));
        fixture.Intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", "MSFT", 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fixture.Commands.Setup(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(), fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult(
                "exit-broker", "exit-client", Guid.NewGuid(), DateTimeOffset.UtcNow));
        fixture.Commands.Setup(service => service.RequestCancelAsync(
                It.Is<OrderCancellationSubmission>(request =>
                    request.ClientOrderId == "exit-client" &&
                    request.Reason == "process_shutdown_exit_not_filled"),
                fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("exit-client", "exit-broker", OrderState.Canceled));
        fixture.OrderEvents.Setup(repository => repository.GetCurrentAsync(
                "exit-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("exit-client", "exit-broker", OrderState.Acked));
        fixture.Broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);

        var result = await fixture.Service.ShutdownAsync(fixture.Broker.Object);

        Assert.Equal(0, result.PositionsFlattened);
        Assert.Equal(1, result.PositionsRetained);
        fixture.Commands.Verify(service => service.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "exit-client"),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Protection.Verify(service => service.EnsureAsync(
            fixture.Broker.Object,
            "paper-account",
            It.IsAny<IReadOnlyList<BrokerPosition>>(),
            It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Shutdown_DoesNotClaimFlatAfterOneMissingBrokerPositionObservation()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(
            true, false, timeoutSeconds: 10,
            exitFillConfirmationTimeoutSeconds: 1,
            exitFillPollIntervalMilliseconds: 25));
        var position = Position("MSFT", "day-client", 11, 10m);
        var entry = Intent(OrderIntentKind.StrategyEntry, "day-client", "MSFT", "swing");
        fixture.Positions.Setup(repository => repository.ListCurrentAsync(
                "paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([position]);
        fixture.Positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync(
                "day-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        fixture.Intents.Setup(repository => repository.GetRunAsync(
                entry.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run(entry.RunId));
        fixture.Intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", "MSFT", 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fixture.Commands.Setup(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(), fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult(
                "exit-broker", "exit-client", Guid.NewGuid(), DateTimeOffset.UtcNow));
        fixture.Commands.Setup(service => service.RequestCancelAsync(
                It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "exit-client"),
                fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("exit-client", "exit-broker", OrderState.Canceled));
        fixture.OrderEvents.Setup(repository => repository.GetCurrentAsync(
                "exit-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("exit-client", "exit-broker", OrderState.Filled));
        var observations = 0;
        fixture.Broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref observations) == 1
                ? []
                : [new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);

        var result = await fixture.Service.ShutdownAsync(fixture.Broker.Object);

        Assert.Equal(0, result.PositionsFlattened);
        Assert.Equal(1, result.PositionsRetained);
        Assert.True(observations > 2);
        fixture.Commands.Verify(service => service.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "exit-client"),
            fixture.Broker.Object,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_ExitHandoffFailureRestoresProtectionWithIndependentBoundedToken()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(
            true, false, timeoutSeconds: 10,
            protectionRestorationTimeoutSeconds: 2));
        var position = Position("MSFT", "day-client", 11, 10m);
        var entry = Intent(OrderIntentKind.StrategyEntry, "day-client", "MSFT", "swing");
        fixture.Positions.Setup(repository => repository.ListCurrentAsync(
                "paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([position]);
        fixture.Intents.Setup(repository => repository.GetByClientOrderIdAsync(
                "day-client", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);
        fixture.Intents.Setup(repository => repository.GetRunAsync(
                entry.RunId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run(entry.RunId));
        fixture.Intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", "MSFT", 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fixture.Broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)]);
        using var hostCancellation = new CancellationTokenSource();
        fixture.Commands.Setup(service => service.SubmitPositionExitAsync(
                It.IsAny<PositionExitSubmission>(), fixture.Broker.Object,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                hostCancellation.Cancel();
                return Task.FromException<OrderSubmissionResult>(
                    new OperationCanceledException("host stopping token expired"));
            });
        var restorationUsedIndependentToken = false;
        fixture.Protection.Setup(service => service.EnsureAsync(
                fixture.Broker.Object,
                "paper-account",
                It.IsAny<IReadOnlyList<BrokerPosition>>(),
                It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IBrokerClient, string, IReadOnlyList<BrokerPosition>, IReadOnlyList<ActiveBrokerOrder>, CancellationToken>(
                (_, _, _, _, token) =>
                {
                    restorationUsedIndependentToken = !token.IsCancellationRequested;
                    return Task.FromResult<IReadOnlyList<ProtectiveOrderRepair>>([]);
                });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.ShutdownAsync(fixture.Broker.Object, hostCancellation.Token));

        Assert.True(restorationUsedIndependentToken);
        fixture.Protection.Verify(service => service.EnsureAsync(
            fixture.Broker.Object,
            "paper-account",
            It.IsAny<IReadOnlyList<BrokerPosition>>(),
            It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_PreservesBrokerFailureWhenJournalFlushAlsoFails()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(
            true, false,
            journalFlushTimeoutSeconds: 1,
            journalFlushRetryIntervalMilliseconds: 25));
        fixture.Broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unavailable"));
        fixture.Journal.Setup(service => service.FlushAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database busy"));

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Service.ShutdownAsync(fixture.Broker.Object));

        Assert.Equal("broker unavailable", error.InnerExceptions[0].Message);
        Assert.IsType<TimeoutException>(error.InnerExceptions[1]);
        Assert.Equal("database busy", error.InnerExceptions[1].InnerException?.Message);
        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task Shutdown_CheckpointsOnlyAfterActiveBrokerMutationDrains()
    {
        var fixture = CreateFixture(new ExecutionShutdownOptions(true, false));
        var activeMutation = fixture.BrokerMutations.Enter("entry broker post and acknowledgement journal");

        var shutdown = fixture.Service.ShutdownAsync(fixture.Broker.Object);
        await Task.Delay(50);
        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Never);

        activeMutation.Dispose();
        await shutdown;

        fixture.Journal.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Fixture CreateFixture(ExecutionShutdownOptions options)
    {
        var intents = new Mock<IOrderIntentRepository>();
        var orderEvents = new Mock<IOrderEventRepository>();
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.ListCurrentAsync("paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var commands = new Mock<IOrderCommandService>();
        var protection = new Mock<IProtectiveOrderInvariantService>();
        protection.Setup(service => service.EnsureAsync(
                It.IsAny<IBrokerClient>(),
                "paper-account",
                It.IsAny<IReadOnlyList<BrokerPosition>>(),
                It.IsAny<IReadOnlyList<ActiveBrokerOrder>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var journal = new Mock<IExecutionJournalFlushService>();
        var synchronization = new Mock<IOrderSynchronizationCoordinator>();
        synchronization.Setup(service => service.CrossCheckAsync(
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var broker = new Mock<IBrokerClient>();
        broker.Setup(client => client.GetAccountSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        broker.Setup(client => client.GetOpenOrdersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var admission = new EntryAdmissionControl();
        var brokerMutations = new BrokerMutationCoordinator();
        var service = new ExecutionShutdownCoordinator(
            admission,
            brokerMutations,
            intents.Object,
            orderEvents.Object,
            positions.Object,
            commands.Object,
            synchronization.Object,
            protection.Object,
            journal.Object,
            options,
            TimeProvider.System,
            NullLogger<ExecutionShutdownCoordinator>.Instance);
        return new Fixture(
            service,
            admission,
            brokerMutations,
            intents,
            orderEvents,
            positions,
            commands,
            synchronization,
            protection,
            journal,
            broker);
    }

    private static OrderIntentRecord Intent(
        OrderIntentKind kind,
        string clientOrderId,
        string symbol,
        string horizon = "swing") => new()
    {
        RunId = Guid.NewGuid(),
        IntentId = Guid.NewGuid(),
        Kind = kind,
        ClientOrderId = clientOrderId,
        AccountId = "paper-account",
        StrategyId = "strategy",
        Symbol = symbol,
        Side = kind == OrderIntentKind.ProtectiveStop ? "sell" : "buy",
        OrderType = kind == OrderIntentKind.ProtectiveStop ? "stop" : "limit",
        TimeInForce = "day",
        RequestedQuantity = 10m,
        RequestJson = $"{{\"horizon\":\"{horizon}\"}}"
    };

    private static PositionLedgerSnapshot Position(
        string symbol,
        string clientOrderId,
        long generation,
        decimal quantity) => new(
            "paper-account", symbol, quantity, "strategy",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            generation, generation, clientOrderId, clientOrderId, 100m, "buy");

    private static ProductionRun Run(Guid id) => new()
    {
        RunId = id,
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.UtcNow,
        ConfigHash = "hash",
        CodeVersion = "version"
    };

    private static ActiveBrokerOrder Order(
        string brokerOrderId,
        string clientOrderId,
        string side,
        string type) => new(
            brokerOrderId, "MSFT", side, "new", type,
            type == "limit" ? 100m : null,
            type == "stop" ? 95m : null,
            10m, DateTimeOffset.UtcNow, clientOrderId, 0m, null,
            DateTimeOffset.UtcNow, TimeInForce: type == "stop" ? "gtc" : "day",
            OrderClass: "simple", ExtendedHours: false);

    private static OrderStateSnapshot Snapshot(
        string clientOrderId,
        string brokerOrderId,
        OrderState state) => new(
            Guid.NewGuid(), clientOrderId, brokerOrderId, state,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0m, null, 1);

    private sealed record Fixture(
        ExecutionShutdownCoordinator Service,
        EntryAdmissionControl Admission,
        BrokerMutationCoordinator BrokerMutations,
        Mock<IOrderIntentRepository> Intents,
        Mock<IOrderEventRepository> OrderEvents,
        Mock<IPositionLedgerRepository> Positions,
        Mock<IOrderCommandService> Commands,
        Mock<IOrderSynchronizationCoordinator> Synchronization,
        Mock<IProtectiveOrderInvariantService> Protection,
        Mock<IExecutionJournalFlushService> Journal,
        Mock<IBrokerClient> Broker);
}
