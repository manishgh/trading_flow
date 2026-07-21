using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSubmissionServiceTests
{
    [Fact]
    public async Task SubmitBracketOrderAsync_PersistsIntentBeforeBrokerNetworkCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        FinalizedOrder? brokerOrder = null;
        var brokerAcceptedAt = new DateTimeOffset(2026, 7, 21, 14, 35, 1, TimeSpan.Zero);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
            .Callback<FinalizedOrder, CancellationToken>((order, _) =>
            {
                Assert.True(repository.ReservationCompleted);
                Assert.Equal(OrderState.Submitted, events.State);
                brokerOrder = order;
            })
            .ReturnsAsync(new BrokerOrderReceipt("broker-order-1", brokerAcceptedAt));
        var service = new OrderSubmissionService(
            repository,
            events,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        var result = await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal("broker-order-1", result.BrokerOrderId);
        Assert.Equal(repository.Intent!.ClientOrderId, result.ClientOrderId);
        Assert.Equal(result.ClientOrderId, brokerOrder!.ClientOrderId);
        Assert.Equal(brokerAcceptedAt, result.BrokerAcceptedAtUtc);
        Assert.Equal(OrderState.Acked, events.State);
        Assert.Equal([OrderState.Submitted, OrderState.Acked], events.AppliedStates);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_AcknowledgedRetry_SuppressesDuplicateBrokerCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var submittedClientIds = new List<string>();
        var broker = new Mock<IBrokerClient>();
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
            .Callback<FinalizedOrder, CancellationToken>((order, _) => submittedClientIds.Add(order.ClientOrderId))
            .ReturnsAsync(new BrokerOrderReceipt("broker-order-1", DateTimeOffset.UtcNow));
        var service = new OrderSubmissionService(
            repository,
            events,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);
        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal(2, repository.ReservationAttempts);
        Assert.Single(submittedClientIds);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_BrokerFailure_LeavesUncertainSubmissionAndBlocksRetry()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Broker response was not received."));
        var service = new OrderSubmissionService(
            repository,
            events,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None));
        var retryError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Equal(OrderState.Submitted, events.State);
        Assert.Contains("must be reconciled", retryError.Message, StringComparison.Ordinal);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ClientOrderIdFactory_UsesBindingFormatAndComponents()
    {
        var clientOrderId = ClientOrderIdFactory.Create(
            "research.swing-a",
            "buy",
            "msft.us",
            new DateOnly(2026, 7, 21),
            3,
            Guid.Parse("1f9c2a7e-0000-0000-0000-000000000000"));

        Assert.Equal("RESEARCHSW-B-MSFTUS-20260721-003-1f9c2a7e", clientOrderId);
        Assert.True(ClientOrderIdFactory.IsBindingFormat(clientOrderId));
        Assert.False(ClientOrderIdFactory.IsBindingFormat("legacy-order-id"));
        Assert.False(ClientOrderIdFactory.IsBindingFormat("RESEARCHSW-B-MSFTUS-20260721-000-1f9c2a7e"));
    }

    [Fact]
    public void OrderIntentIdFactory_SameSignalIsStable_AndLaterSignalDiffers()
    {
        var runId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero);

        var first = OrderIntentIdFactory.Create(runId, "SWGA", "buy", "MSFT", timestamp);
        var replay = OrderIntentIdFactory.Create(runId, "SWGA", "buy", "msft", timestamp);
        var later = OrderIntentIdFactory.Create(runId, "SWGA", "buy", "MSFT", timestamp.AddMinutes(5));

        Assert.Equal(first, replay);
        Assert.NotEqual(first, later);
    }

    [Fact]
    public void ExecutionRunContextFactory_HashesEffectiveConfigAndUsesNewYorkSessionDate()
    {
        var runId = Guid.NewGuid();
        var first = ExecutionRunContextFactory.Create(
            runId,
            "paper",
            new { Threshold = 2m, Symbols = new[] { "MSFT", "NVDA" } },
            DateTimeOffset.UtcNow);
        var same = ExecutionRunContextFactory.Create(
            runId,
            "paper",
            new { Threshold = 2m, Symbols = new[] { "MSFT", "NVDA" } },
            DateTimeOffset.UtcNow);
        var changed = ExecutionRunContextFactory.Create(
            runId,
            "paper",
            new { Threshold = 3m, Symbols = new[] { "MSFT", "NVDA" } },
            DateTimeOffset.UtcNow);

        Assert.Equal(first.ConfigHash, same.ConfigHash);
        Assert.NotEqual(first.ConfigHash, changed.ConfigHash);
        Assert.Matches("^[0-9a-f]{64}$", first.ConfigHash);
        Assert.Matches("^[0-9a-f]{40}$|^[0-9a-f]{64}$", first.CodeVersion);
        Assert.Equal(
            new DateOnly(2026, 7, 21),
            ExecutionRunContextFactory.ResolveSessionDate(
                new DateTimeOffset(2026, 7, 22, 1, 0, 0, TimeSpan.Zero),
                "America/New_York"));
    }

    private static BracketOrderSubmission CreateSubmission(Guid intentId)
    {
        var runContext = new ExecutionRunContext(
            Guid.NewGuid(),
            "paper",
            new string('a', 64),
            new string('b', 40),
            new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero));
        return new BracketOrderSubmission(
            intentId,
            CandidateId: null,
            runContext,
            "SWGA",
            "buy",
            "limit",
            "day",
            new DateOnly(2026, 7, 21),
            new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
            new FinalizedOrder("MSFT", "Swing A", 10, 100m, 98m, 104m, DateTimeOffset.UtcNow, String.Empty));
    }

    private sealed class RecordingIntentRepository : IOrderIntentRepository
    {
        public OrderIntentRecord? Intent { get; private set; }

        public bool ReservationCompleted { get; private set; }

        public int ReservationAttempts { get; private set; }

        public Task<OrderIntentRecord> ReserveAsync(
            ProductionRun run,
            OrderIntentReservation reservation,
            CancellationToken cancellationToken = default)
        {
            ReservationAttempts++;
            Intent ??= new OrderIntentRecord
            {
                IntentId = reservation.IntentId,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion,
                ClientOrderId = ClientOrderIdFactory.Create(
                    reservation.StrategyId,
                    reservation.Side,
                    reservation.Symbol,
                    reservation.SessionDate,
                    1,
                    reservation.IntentId),
                StrategyId = reservation.StrategyId,
                Symbol = reservation.Symbol,
                Side = reservation.Side,
                OrderType = reservation.OrderType,
                TimeInForce = reservation.TimeInForce,
                RequestedQuantity = reservation.RequestedQuantity,
                LimitPrice = reservation.LimitPrice,
                StopPrice = reservation.StopPrice,
                SessionDate = reservation.SessionDate,
                SequenceNumber = 1,
                CreatedAtUtc = reservation.CreatedAtUtc,
                RequestJson = reservation.RequestJson
            };
            ReservationCompleted = true;
            return Task.FromResult(Intent);
        }
    }

    private sealed class RecordingEventRepository(RecordingIntentRepository intents) : IOrderEventRepository
    {
        private long eventId;
        private OrderStateSnapshot? current;

        public OrderState? State => current?.State;

        public List<OrderState> AppliedStates { get; } = [];

        public Task<OrderStateSnapshot?> GetCurrentAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            if (current is null && intents.Intent is not null)
            {
                current = new OrderStateSnapshot(
                    intents.Intent.RunId,
                    clientOrderId,
                    BrokerOrderId: null,
                    OrderState.Intent,
                    intents.Intent.CreatedAtUtc,
                    BrokerTimestampUtc: null,
                    FilledQuantity: null,
                    FillPrice: null,
                    ++eventId);
            }

            return Task.FromResult(current);
        }

        public Task<OrderTransitionResult> TransitionAsync(
            OrderTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (current is null || current.State != request.ExpectedPreviousState)
            {
                throw new InvalidOperationException("Unexpected lifecycle state in test repository.");
            }

            current = current with
            {
                BrokerOrderId = request.BrokerOrderId ?? current.BrokerOrderId,
                State = request.NewState,
                LocalTimestampUtc = request.LocalTimestampUtc,
                BrokerTimestampUtc = request.BrokerTimestampUtc ?? current.BrokerTimestampUtc,
                FilledQuantity = request.FilledQuantity ?? current.FilledQuantity,
                FillPrice = request.FillPrice ?? current.FillPrice,
                EventId = ++eventId
            };
            AppliedStates.Add(request.NewState);
            return Task.FromResult(new OrderTransitionResult(current, Applied: true));
        }
    }
}
