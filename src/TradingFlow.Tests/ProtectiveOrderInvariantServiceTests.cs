using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class ProtectiveOrderInvariantServiceTests
{
    [Fact]
    public async Task MissingStop_UsesPersistedStructuralStop_AndSubmitsOppositeSideGtcOrder()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "SWGA-B-MSFT-20260721-001-12345678", 100m, "buy", now);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-1", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(intents.Object, positions.Object, submissions.Object, new EmptyMarketDataProvider(), now);

        var repairs = await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            [],
            CancellationToken.None);

        Assert.True(Assert.Single(repairs).Succeeded);
        Assert.NotNull(captured);
        Assert.Equal("sell", captured.Side);
        Assert.Equal(10m, captured.Quantity);
        Assert.Equal(98m, captured.StopPrice);
        Assert.Equal("ledger:1:SWGA-B-MSFT-20260721-001-12345678", captured.PositionGenerationIdentity);
        Assert.Equal(
            ProtectiveOrderIntentIdFactory.Create(
                "paper-account",
                "MSFT",
                "sell",
                captured.PositionGenerationIdentity,
                captured.ProtectionRevision),
            captured.IntentId);
    }

    [Fact]
    public async Task RepeatedMissingCoverage_UsesSameProtectiveIntentIdentity()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        var submitted = new List<ProtectiveStopSubmission>();
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => submitted.Add(submission))
            .ReturnsAsync(new OrderSubmissionResult("stop-1", "client-stop-1", Guid.NewGuid(), now));
        var service = CreateService(intents.Object, positions.Object, submissions.Object, new EmptyMarketDataProvider(), now);
        var brokerPosition = new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m);

        await service.EnsureAsync(Mock.Of<IBrokerClient>(), "paper-account", [brokerPosition], []);
        await service.EnsureAsync(Mock.Of<IBrokerClient>(), "paper-account", [brokerPosition], []);

        Assert.Equal(2, submitted.Count);
        Assert.Equal(submitted[0].IntentId, submitted[1].IntentId);
        Assert.Equal(submitted[0].PositionGenerationIdentity, submitted[1].PositionGenerationIdentity);
        Assert.Equal(submitted[0].SessionDate, submitted[1].SessionDate);
    }

    [Fact]
    public async Task TerminalProtectiveOwner_CreatesDeterministicReplacementRevision()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now);
        const string generation = "ledger:1:entry-client-id";
        var firstIntentId = ProtectiveOrderIntentIdFactory.Create(
            "paper-account", "MSFT", "sell", generation, protectionRevision: 0);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        intents.Setup(repository => repository.GetByIntentIdAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid intentId, CancellationToken _) =>
                intentId == firstIntentId
                    ? new OrderIntentRecord
                    {
                        IntentId = firstIntentId,
                        AccountId = "paper-account",
                        Kind = OrderIntentKind.ProtectiveStop,
                        ClientOrderId = "BACKSTOP-S-MSFT-20260721-001-11111111",
                        Symbol = "MSFT",
                        Side = "sell",
                        RequestedQuantity = 10m,
                        StopPrice = 98m,
                        SessionDate = new DateOnly(2026, 7, 21),
                        CreatedAtUtc = now
                    }
                    : null);
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                "BACKSTOP-S-MSFT-20260721-001-11111111",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(),
                "BACKSTOP-S-MSFT-20260721-001-11111111",
                "broker-stop-1",
                OrderState.Canceled,
                now,
                now,
                null,
                null,
                2));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>(
                (submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult(
                "broker-stop-2",
                "BACKSTOP-S-MSFT-20260721-002-22222222",
                Guid.NewGuid(),
                now));
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal(1, captured.ProtectionRevision);
        Assert.Equal(
            ProtectiveOrderIntentIdFactory.Create(
                "paper-account", "MSFT", "sell", generation, protectionRevision: 1),
            captured.IntentId);
    }

    [Fact]
    public async Task FilledProtectiveOwner_WithoutBrokerConfirmation_DoesNotAdvance()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now);
        const string generation = "ledger:1:entry-client-id";
        const string clientOrderId = "BACKSTOP-S-MSFT-20260721-001-11111111";
        var ownerId = ProtectiveOrderIntentIdFactory.Create(
            "paper-account", "MSFT", "sell", generation, protectionRevision: 0);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                ownerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord
            {
                IntentId = ownerId,
                AccountId = "paper-account",
                Kind = OrderIntentKind.ProtectiveStop,
                ClientOrderId = clientOrderId,
                Symbol = "MSFT",
                Side = "sell",
                RequestedQuantity = 10m,
                StopPrice = 98m,
                SessionDate = new DateOnly(2026, 7, 21),
                CreatedAtUtc = now
            });
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(),
                clientOrderId,
                "broker-stop-1",
                OrderState.Filled,
                now,
                now,
                10m,
                98m,
                2));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            []));

        Assert.False(repair.Succeeded);
        Assert.Contains("requires broker-confirmed fill and exact remaining position", repair.Detail, StringComparison.Ordinal);
        submissions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FilledProtectiveOwner_WithBrokerConfirmedRemainingPosition_AdvancesRevision()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 4m, "entry-client-id", 100m, "buy", now);
        const string generation = "ledger:1:entry-client-id";
        const string clientOrderId = "BACKSTOP-S-MSFT-20260721-001-11111111";
        var ownerId = ProtectiveOrderIntentIdFactory.Create(
            "paper-account", "MSFT", "sell", generation, protectionRevision: 0);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid intentId, CancellationToken _) =>
                intentId == ownerId
                    ? new OrderIntentRecord
                    {
                        IntentId = ownerId,
                        AccountId = "paper-account",
                        Kind = OrderIntentKind.ProtectiveStop,
                        ClientOrderId = clientOrderId,
                        Symbol = "MSFT",
                        Side = "sell",
                        RequestedQuantity = 6m,
                        StopPrice = 98m,
                        SessionDate = new DateOnly(2026, 7, 21),
                        CreatedAtUtc = now,
                        RequestJson = "{\"PositionGenerationIdentity\":\"ledger:1:entry-client-id\"}"
                    }
                    : null);
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(),
                clientOrderId,
                "broker-stop-1",
                OrderState.Filled,
                now,
                now,
                6m,
                98m,
                2));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>(
                (submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult(
                "broker-stop-2",
                "BACKSTOP-S-MSFT-20260721-002-22222222",
                Guid.NewGuid(),
                now));
        var brokerPosition = new BrokerPosition("MSFT", "long", 4m, 100m, 101m, 4m);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.GetOrderByClientOrderIdAsync(
                clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveBrokerOrder(
                "broker-stop-1",
                "MSFT",
                "sell",
                "filled",
                "stop",
                null,
                98m,
                6m,
                now,
                clientOrderId,
                6m,
                98m,
                now));
        broker.Setup(client => client.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([brokerPosition]);
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);

        var repair = Assert.Single(await service.EnsureAsync(
            broker.Object,
            "paper-account",
            [brokerPosition],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal(4m, captured.Quantity);
        Assert.Equal(1, captured.ProtectionRevision);
        broker.VerifyAll();
    }

    [Fact]
    public async Task VisibleActiveOwner_WithCoverageDeficit_CreatesSupplementalRevision()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var local = Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now);
        const string generation = "ledger:1:entry-client-id";
        const string firstClientOrderId = "BACKSTOP-S-MSFT-20260721-001-11111111";
        var firstIntentId = ProtectiveOrderIntentIdFactory.Create(
            "paper-account", "MSFT", "sell", generation, protectionRevision: 0);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                local.LatestClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        intents.Setup(repository => repository.GetByIntentIdAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid intentId, CancellationToken _) =>
                intentId == firstIntentId
                    ? new OrderIntentRecord
                    {
                        IntentId = firstIntentId,
                        AccountId = "paper-account",
                        Kind = OrderIntentKind.ProtectiveStop,
                        ClientOrderId = firstClientOrderId,
                        Symbol = "MSFT",
                        Side = "sell",
                        RequestedQuantity = 6m,
                        StopPrice = 98m,
                        SessionDate = new DateOnly(2026, 7, 21),
                        CreatedAtUtc = now,
                        RequestJson = "{\"PositionGenerationIdentity\":\"ledger:1:entry-client-id\"}"
                    }
                    : null);
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                firstClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedProtectiveIntent(firstClientOrderId, 6m, generation, now));
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                firstClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(),
                firstClientOrderId,
                "broker-stop-1",
                OrderState.Acked,
                now,
                now,
                null,
                null,
                2));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(local);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>(
                (submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult(
                "broker-stop-2",
                "BACKSTOP-S-MSFT-20260721-002-22222222",
                Guid.NewGuid(),
                now));
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);
        var visibleOwner = new ActiveBrokerOrder(
            "broker-stop-1",
            "MSFT",
            "sell",
            "new",
            "stop",
            null,
            98m,
            6m,
            now,
            firstClientOrderId,
            0m,
            null,
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            [visibleOwner]));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal(4m, captured.Quantity);
        Assert.Equal(1, captured.ProtectionRevision);
        Assert.Equal(
            ProtectiveOrderIntentIdFactory.Create(
                "paper-account", "MSFT", "sell", generation, protectionRevision: 1),
            captured.IntentId);
    }

    [Fact]
    public async Task AmbiguousSubmission_WithChangedPriceAndCoverage_ReusesOriginalProtectionOwner()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 80)
            .Select(index => new OhlcvBar(
                "MSFT",
                now.AddDays(index - 80),
                "1d",
                100m,
                102m,
                99m,
                101m,
                1_000_000m))
            .ToArray();
        OrderIntentRecord? persisted = null;
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByIntentIdAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        var attempts = new List<ProtectiveStopSubmission>();
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) =>
            {
                attempts.Add(submission);
                persisted ??= new OrderIntentRecord
                {
                    IntentId = submission.IntentId,
                    AccountId = "paper-account",
                    Kind = OrderIntentKind.ProtectiveStop,
                    ClientOrderId = "BACKSTOP-S-MSFT-20260721-001-12345678",
                    Symbol = submission.Symbol,
                    Side = submission.Side,
                    RequestedQuantity = submission.Quantity,
                    StopPrice = submission.StopPrice,
                    SessionDate = submission.SessionDate,
                    CreatedAtUtc = submission.CreatedAtUtc
                };
            })
            .ThrowsAsync(new TimeoutException("Broker response was not received."));
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
                new OrderStateSnapshot(
                    Guid.NewGuid(),
                    clientOrderId,
                    null,
                    OrderState.Submitted,
                    now,
                    null,
                    null,
                    null,
                    1));
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new FixedMarketDataProvider(bars),
            now,
            orderEvents.Object);

        var first = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 100m, 0m)],
            []));
        var second = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 99m, -10m)],
            [BrokerOrder("MSFT", "sell", "stop", 4m, 95m)]));

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].IntentId, attempts[1].IntentId);
        Assert.Equal(attempts[0].Quantity, attempts[1].Quantity);
        Assert.Equal(attempts[0].StopPrice, attempts[1].StopPrice);
        Assert.Equal(attempts[0].SessionDate, attempts[1].SessionDate);
        Assert.Equal(attempts[0].ProtectionRevision, attempts[1].ProtectionRevision);
    }

    [Fact]
    public async Task OwnedStopCoveringFullPosition_DoesNotSubmitAnotherOrder()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        const string clientOrderId = "BACKSTOP-S-MSFT-20260721-001-12345678";
        const string generation = "ledger:1:entry-client-id";
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OwnedProtectiveIntent(clientOrderId, 10m, generation, now));
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                clientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveSnapshot(clientOrderId, "broker-stop", now));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now));
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);
        var position = new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m);
        var stop = BrokerOrder("MSFT", "sell", "stop", 10m, 98m) with
        {
            OrderId = "broker-stop",
            ClientOrderId = clientOrderId
        };

        var repairs = await service.EnsureAsync(
            Mock.Of<IBrokerClient>(), "paper-account", [position], [stop]);

        Assert.Empty(repairs);
    }

    [Fact]
    public async Task ActivatedStrategyStop_CancelsOnlyRedundantTradingFlowBackstop()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        const string generation = "ledger:1:entry-client-id";
        const string strategyClientOrderId = "BACKSTOP-S-MSFT-20260721-001-11111111";
        const string backstopClientOrderId = "BACKSTOP-S-MSFT-20260721-002-22222222";
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        submissions.Setup(service => service.RequestCancelAsync(
                It.Is<OrderCancellationSubmission>(cancel =>
                    cancel.ClientOrderId == backstopClientOrderId &&
                    cancel.BrokerOrderId == "backstop-1" &&
                    cancel.Reason == "redundant_protective_backstop"),
                broker.Object,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderStateSnapshot(
                Guid.NewGuid(),
                backstopClientOrderId,
                "backstop-1",
                OrderState.Canceled,
                now,
                null,
                null,
                null,
                1));
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
                clientOrderId == strategyClientOrderId
                    ? OwnedProtectiveIntent(strategyClientOrderId, 10m, generation, now)
                    : clientOrderId == backstopClientOrderId
                        ? OwnedProtectiveIntent(backstopClientOrderId, 10m, generation, now)
                        : null);
        var orderEvents = new Mock<IOrderEventRepository>();
        orderEvents.Setup(repository => repository.GetCurrentAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientOrderId, CancellationToken _) =>
                ActiveSnapshot(
                    clientOrderId,
                    clientOrderId == backstopClientOrderId ? "backstop-1" : "strategy-stop-1",
                    now));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync(
                "paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("MSFT", 10m, "entry-client-id", 100m, "buy", now));
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now,
            orderEvents.Object);
        var position = new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m);
        var strategyStop = BrokerOrder("MSFT", "sell", "stop", 10m, 98m) with
        {
            OrderId = "strategy-stop-1",
            ClientOrderId = strategyClientOrderId
        };
        var backstop = BrokerOrder("MSFT", "sell", "stop", 10m, 97m) with
        {
            OrderId = "backstop-1",
            ClientOrderId = backstopClientOrderId
        };

        var repair = Assert.Single(await service.EnsureAsync(
            broker.Object,
            "paper-account",
            [position],
            [strategyStop, backstop]));

        Assert.True(repair.Succeeded);
        Assert.Contains("Canceled redundant", repair.Detail, StringComparison.Ordinal);
        submissions.VerifyAll();
    }

    [Fact]
    public async Task UnownedPartialStopCoverage_IsNotAcceptedAsProtected()
    {
        var now = DateTimeOffset.UtcNow;
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot("MSFT", 10m, "SWGA-B-MSFT-20260721-001-12345678", 100m, "buy", now));
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", StopPrice = 98m });
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult("stop-2", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(intents.Object, positions.Object, submissions.Object, new EmptyMarketDataProvider(), now);

        var repairs = await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            [BrokerOrder("MSFT", "sell", "stop", 4m, 98m)]);

        Assert.True(Assert.Single(repairs).Succeeded);
        submissions.Verify(service => service.SubmitProtectiveStopAsync(
            It.Is<ProtectiveStopSubmission>(submission => submission.Quantity == 10m),
            It.IsAny<IBrokerClient>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LocalCommittedFill_IsProtectedWhileBrokerPositionRestLags()
    {
        var now = DateTimeOffset.UtcNow;
        var generationClientOrderId = "SWGA-B-MSFT-20260721-001-12345678";
        var localPosition = Snapshot("MSFT", 4m, generationClientOrderId, 100m, "buy", now);
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.ListCurrentAsync("paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([localPosition]);
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(localPosition);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", "MSFT", localPosition.PositionGenerationEventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        intents.Setup(repository => repository.GetByClientOrderIdAsync(generationClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord
            {
                AccountId = "paper-account",
                Symbol = "MSFT",
                StopPrice = 98m
            });
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.Is<ProtectiveStopSubmission>(submission =>
                    submission.Symbol == "MSFT" && submission.Quantity == 4m && submission.StopPrice == 98m),
                It.IsAny<IBrokerClient>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult("stop-1", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(
            intents.Object,
            positions.Object,
            submissions.Object,
            new EmptyMarketDataProvider(),
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [],
            []));

        Assert.True(repair.Succeeded);
        Assert.Contains("4 share", repair.Detail, StringComparison.Ordinal);
        submissions.VerifyAll();
    }

    [Fact]
    public async Task StaleLocalQuantity_NeverCreatesProtectionAboveReducedBrokerPosition()
    {
        var now = DateTimeOffset.UtcNow;
        var generationClientOrderId = "SWGA-B-MSFT-20260721-001-12345678";
        var staleLocal = Snapshot(
            "MSFT", 4m, generationClientOrderId, 100m, "buy", now.AddMinutes(-5));
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.ListCurrentAsync("paper-account", It.IsAny<CancellationToken>()))
            .ReturnsAsync([staleLocal]);
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleLocal);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.HasActivePositionExitAsync(
                "paper-account", "MSFT", staleLocal.PositionGenerationEventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        intents.Setup(repository => repository.GetByClientOrderIdAsync(generationClientOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { AccountId = "paper-account", Symbol = "MSFT", StopPrice = 98m });
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.Is<ProtectiveStopSubmission>(submission => submission.Quantity == 2m),
                It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderSubmissionResult("stop-2", "stop-client", Guid.NewGuid(), now));
        var service = CreateService(
            intents.Object, positions.Object, submissions.Object,
            new EmptyMarketDataProvider(), now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 2m, 100m, 99m, -2m)],
            []));

        Assert.True(repair.Succeeded);
        submissions.VerifyAll();
    }

    [Fact]
    public async Task FractionalPosition_FailsClosedInsteadOfSubmittingUnsupportedGtcStop()
    {
        var submissions = new Mock<IOrderSubmissionService>(MockBehavior.Strict);
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            Mock.Of<IPositionLedgerRepository>(),
            submissions.Object,
            new EmptyMarketDataProvider(),
            DateTimeOffset.UtcNow);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 1.5m, 100m, 101m, 1.5m)],
            []));

        Assert.False(repair.Succeeded);
        Assert.Contains("fractional", repair.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingStructuralStop_UsesOnlyHistoricalBarsAvailableAtReconciliationTime()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 80)
            .Select(index => new OhlcvBar(
                "MSFT",
                now.AddDays(index - 80),
                "1d",
                100m + index,
                102m + index,
                99m + index,
                101m + index,
                1_000_000m))
            .ToArray();
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-atr", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            positions.Object,
            submissions.Object,
            new FixedMarketDataProvider(bars),
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 180m, 181m, 10m)],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.True(captured.StopPrice > 0m);
        Assert.True(captured.StopPrice < 180m);
    }

    [Fact]
    public async Task AtrFallback_DiscardsProviderBarsAtOrAfterReconciliationTime()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 40)
            .Select(index => new OhlcvBar(
                "MSFT",
                now.AddDays(index - 40),
                "1d",
                100m,
                102m,
                99m,
                101m,
                1_000_000m))
            .Append(new OhlcvBar(
                "MSFT",
                now.AddMinutes(1),
                "1d",
                100m,
                1_000m,
                1m,
                900m,
                100_000_000m))
            .ToArray();
        var positions = new Mock<IPositionLedgerRepository>();
        positions.Setup(repository => repository.GetCurrentAsync("paper-account", "MSFT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PositionLedgerSnapshot?)null);
        ProtectiveStopSubmission? captured = null;
        var submissions = new Mock<IOrderSubmissionService>();
        submissions.Setup(service => service.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopSubmission, IBrokerClient, CancellationToken>((submission, _, _) => captured = submission)
            .ReturnsAsync(new OrderSubmissionResult("stop-atr", "BACKSTOP-S-MSFT-20260721-001-12345678", Guid.NewGuid(), now));
        var service = CreateService(
            Mock.Of<IOrderIntentRepository>(),
            positions.Object,
            submissions.Object,
            new UnfilteredMarketDataProvider(bars),
            now);

        var repair = Assert.Single(await service.EnsureAsync(
            Mock.Of<IBrokerClient>(),
            "paper-account",
            [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)],
            []));

        Assert.True(repair.Succeeded);
        Assert.NotNull(captured);
        Assert.InRange(captured.StopPrice, 95m, 100m);
    }

    private static ProtectiveOrderInvariantService CreateService(
        IOrderIntentRepository intents,
        IPositionLedgerRepository positions,
        IOrderSubmissionService submissions,
        IMarketDataProvider marketData,
        DateTimeOffset now,
        IOrderEventRepository? orderEvents = null)
    {
        var run = new ProductionRun
        {
            RunId = Guid.NewGuid(),
            Profile = "paper",
            Status = "running",
            StartedAtUtc = now,
            ConfigHash = new string('a', 64),
            CodeVersion = new string('b', 40)
        };
        return new ProtectiveOrderInvariantService(
            intents,
            orderEvents ?? Mock.Of<IOrderEventRepository>(),
            positions,
            submissions,
            new Lazy<IMarketDataProvider>(() => marketData),
            new IndicatorEngine(),
            new ProtectiveOrderOptions(1.5m),
            new ReconciliationRunContext(run),
            new FixedTimeProvider(now),
            NullLogger<ProtectiveOrderInvariantService>.Instance);
    }

    private static PositionLedgerSnapshot Snapshot(
        string symbol,
        decimal quantity,
        string clientOrderId,
        decimal fillPrice,
        string side,
        DateTimeOffset now) =>
        new("paper-account", symbol, quantity, "SWGA", now, now, 1, 1, clientOrderId, clientOrderId, fillPrice, side);

    private static OrderIntentRecord OwnedProtectiveIntent(
        string clientOrderId,
        decimal quantity,
        string positionGenerationIdentity,
        DateTimeOffset now) => new()
        {
            IntentId = Guid.NewGuid(),
            Kind = OrderIntentKind.ProtectiveStop,
            ClientOrderId = clientOrderId,
            AccountId = "paper-account",
            Symbol = "MSFT",
            Side = "sell",
            OrderType = "stop",
            TimeInForce = "gtc",
            RequestedQuantity = quantity,
            StopPrice = 98m,
            CreatedAtUtc = now,
            RequestJson = $"{{\"PositionGenerationIdentity\":\"{positionGenerationIdentity}\"}}"
        };

    private static OrderStateSnapshot ActiveSnapshot(
        string clientOrderId,
        string brokerOrderId,
        DateTimeOffset now) => new(
            Guid.NewGuid(),
            clientOrderId,
            brokerOrderId,
            OrderState.Acked,
            now,
            now,
            null,
            null,
            1);

    private static ActiveBrokerOrder BrokerOrder(
        string symbol,
        string side,
        string type,
        decimal quantity,
        decimal stopPrice) =>
        new(Guid.NewGuid().ToString("N"), symbol, side, "new", type, null, stopPrice, quantity,
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), 0m, null, DateTimeOffset.UtcNow);

    private sealed class EmptyMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedMarketDataProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars.Where(bar => bar.Timestamp >= start && bar.Timestamp <= end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class UnfilteredMarketDataProvider(IReadOnlyList<OhlcvBar> bars) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var bar in bars)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
