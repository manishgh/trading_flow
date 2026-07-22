using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSubmissionServiceTests
{
    [Fact]
    public async Task SubmitProtectiveStopAsync_BypassesEntryBlock_ButStillWritesIntentBeforeBrokerCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var admission = new EntryAdmissionControl();
        admission.Block("test", "ENTRIES_BLOCKED", "Protection must still be allowed.", DateTimeOffset.UtcNow);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .Callback<ProtectiveStopOrder, CancellationToken>((_, _) =>
            {
                Assert.True(repository.ReservationCompleted);
                Assert.Equal(OrderState.Submitted, events.State);
            })
            .ReturnsAsync(new BrokerOrderReceipt("backstop-1", DateTimeOffset.UtcNow));
        var service = new OrderSubmissionService(
            repository,
            events,
            new RecordingCandidateRepository(),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var context = ExecutionRunContextFactory.Create(
            Guid.NewGuid(), "paper", new { test = true }, DateTimeOffset.UtcNow, typeof(OrderSubmissionServiceTests).Assembly);

        var result = await service.SubmitProtectiveStopAsync(
            new ProtectiveStopSubmission(
                Guid.NewGuid(), context, "MSFT", "sell", 10m, 98m,
                new DateOnly(2026, 7, 21), DateTimeOffset.UtcNow),
            broker.Object,
            CancellationToken.None);

        Assert.Equal("backstop-1", result.BrokerOrderId);
        Assert.Equal("BACKSTOP", repository.Intent!.StrategyId);
        Assert.Equal(OrderState.Acked, events.State);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_PersistsIntentBeforeBrokerNetworkCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        SetupRegularSession(broker);
        BrokerEntryOrder? brokerOrder = null;
        var brokerAcceptedAt = new DateTimeOffset(2026, 7, 21, 14, 35, 1, TimeSpan.Zero);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerEntryOrder, CancellationToken>((order, _) =>
            {
                Assert.True(repository.ReservationCompleted);
                Assert.Equal(OrderState.Submitted, events.State);
                brokerOrder = order;
            })
            .ReturnsAsync(new BrokerOrderReceipt("broker-order-1", brokerAcceptedAt));
        var service = new OrderSubmissionService(
            repository,
            events,
            new RecordingCandidateRepository(),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        var result = await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal("broker-order-1", result.BrokerOrderId);
        Assert.Equal(repository.Intent!.ClientOrderId, result.ClientOrderId);
        Assert.Equal(result.ClientOrderId, brokerOrder!.Order.ClientOrderId);
        Assert.False(brokerOrder.SubmitOutsideRegularHours);
        Assert.Equal(brokerAcceptedAt, result.BrokerAcceptedAtUtc);
        Assert.Equal(OrderState.Acked, events.State);
        Assert.Equal([OrderState.Submitted, OrderState.Acked], events.AppliedStates);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_PositionConflict_DoesNotReserveOrCallBroker()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            new RecordingEventRepository(repository),
            new RecordingCandidateRepository(),
            new RejectingEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            service.SubmitBracketOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Equal(RejectCode.REJECT_SETUP_INVALID, error.RejectCode);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_AcknowledgedRetry_SuppressesDuplicateBrokerCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var submittedClientIds = new List<string>();
        var broker = new Mock<IBrokerClient>();
        SetupRegularSession(broker);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerEntryOrder, CancellationToken>((order, _) => submittedClientIds.Add(order.Order.ClientOrderId))
            .ReturnsAsync(new BrokerOrderReceipt("broker-order-1", DateTimeOffset.UtcNow));
        var service = new OrderSubmissionService(
            repository,
            events,
            new RecordingCandidateRepository(),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);
        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal(2, repository.ReservationAttempts);
        Assert.Single(submittedClientIds);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_BrokerFailure_LeavesUncertainSubmissionAndBlocksRetry()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        SetupRegularSession(broker);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Broker response was not received."));
        var service = new OrderSubmissionService(
            repository,
            events,
            new RecordingCandidateRepository(),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None));
        var retryError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Equal(OrderState.Submitted, events.State);
        Assert.Contains("must be reconciled", retryError.Message, StringComparison.Ordinal);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_EntryAdmissionBlocked_DoesNotPersistOrCallBroker()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            events,
            new RecordingCandidateRepository(),
            new RejectingEntryGateChain(
                new EntryGateRejectedException(
                    EntryGateSlot.SystemState,
                    RejectCode.REJECT_DEGRADED_DATA,
                    "stream down")),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            service.SubmitBracketOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("stream down", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_RegularSession_DoesNotMarkOrderAsExtendedWhenPermissionIsEnabled()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        SetupRegularSession(broker);
        BrokerEntryOrder? submitted = null;
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerEntryOrder, CancellationToken>((order, _) => submitted = order)
            .ReturnsAsync(new BrokerOrderReceipt("regular-order", DateTimeOffset.UtcNow));
        var service = CreateService(repository);

        var result = await service.SubmitBracketOrderAsync(
            CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true },
            broker.Object,
            CancellationToken.None);

        Assert.NotNull(submitted);
        Assert.False(submitted.SubmitOutsideRegularHours);
        Assert.False(result.SubmittedOutsideRegularHours);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_PremarketWithoutPermission_FailsBeforeIntentReservation()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.GetSessionAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset timestamp, CancellationToken _) =>
                new TradingSessionSnapshot(
                    new DateOnly(2026, 7, 21),
                    EquityTradingSession.Premarket,
                    timestamp,
                    timestamp.AddHours(1),
                    timestamp.AddHours(7.5)));
        var service = CreateService(repository);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitBracketOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("allow_extended_hours_trading is false", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_OvernightEntry_FailsBeforeIntentReservationWhenProtectionIsUnavailable()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.GetSessionAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset timestamp, CancellationToken _) =>
                new TradingSessionSnapshot(
                    new DateOnly(2026, 7, 22),
                    EquityTradingSession.Overnight,
                    timestamp,
                    timestamp.AddHours(12),
                    timestamp.AddHours(18.5)));
        var service = CreateService(repository);
        var submission = CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Contains("does not support broker-protected bracket orders", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_AfterHoursEnabled_DoesNotSubmitUnprotectedEntry()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.GetSessionAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset timestamp, CancellationToken _) =>
                new TradingSessionSnapshot(
                    new DateOnly(2026, 7, 21),
                    EquityTradingSession.AfterHours,
                    timestamp,
                    timestamp.AddHours(-8),
                    timestamp.AddHours(-1)));
        var service = CreateService(repository);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitBracketOrderAsync(
                CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true },
                broker.Object,
                CancellationToken.None));

        Assert.Contains("does not support broker-protected bracket orders", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyAll();
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
            new ValidatedEntryCandidate(
                Guid.NewGuid(),
                "test",
                "swing",
                new DateTimeOffset(2026, 7, 21, 14, 34, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
                "{}"),
            runContext,
            "SWGA",
            "buy",
            "limit",
            "day",
            new DateOnly(2026, 7, 21),
            new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
            new FinalizedOrder("MSFT", "Swing A", 10, 100m, 98m, 104m, DateTimeOffset.UtcNow, String.Empty));
    }

    private static void SetupRegularSession(Mock<IBrokerClient> broker)
    {
        broker
            .Setup(client => client.GetSessionAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset timestamp, CancellationToken _) =>
                new TradingSessionSnapshot(
                    DateOnly.FromDateTime(timestamp.UtcDateTime),
                    EquityTradingSession.Regular,
                    timestamp,
                    timestamp.AddHours(-1),
                    timestamp.AddHours(5)));
    }

    private static OrderSubmissionService CreateService(RecordingIntentRepository repository) =>
        new(
            repository,
            new RecordingEventRepository(repository),
            new RecordingCandidateRepository(),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

    private sealed class RecordingIntentRepository : IOrderIntentRepository
    {
        public OrderIntentRecord? Intent { get; private set; }

        public bool ReservationCompleted { get; private set; }

        public int ReservationAttempts { get; private set; }

        public Task<OrderIntentRecord?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Intent?.ClientOrderId == clientOrderId ? Intent : null);

        public Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveOrderIntent>>([]);

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

    private sealed class RecordingCandidateRepository : ICandidateRepository
    {
        private CandidateRecord? candidate;

        public Task<CandidateRecord> UpsertValidatedAsync(
            ProductionRun run,
            CandidateRecord value,
            CancellationToken cancellationToken = default)
        {
            candidate = value;
            return Task.FromResult(value);
        }

        public Task<CandidateRecord?> GetAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate?.CandidateId == candidateId ? candidate : null);
    }

    private sealed class PassThroughEntryGateChain : IEntryGateChain
    {
        public Task<T> ExecuteAsync<T>(
            BracketOrderSubmission submission,
            IBrokerClient brokerClient,
            Func<CancellationToken, Task<T>> submit,
            CancellationToken cancellationToken = default) =>
            submit(cancellationToken);
    }

    private sealed class RejectingEntryGateChain(Exception? exception = null) : IEntryGateChain
    {
        public Task<T> ExecuteAsync<T>(
            BracketOrderSubmission submission,
            IBrokerClient brokerClient,
            Func<CancellationToken, Task<T>> submit,
            CancellationToken cancellationToken = default) =>
            throw exception ?? new EntryGateRejectedException(
                EntryGateSlot.PositionConflict,
                RejectCode.REJECT_SETUP_INVALID,
                $"Position for {submission.Order.Ticker} belongs to another strategy.");
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

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderStateSnapshot>>(
                current is not null && current.State is
                    OrderState.Submitted or
                    OrderState.Acked or
                    OrderState.PartiallyFilled or
                    OrderState.CancelPending
                        ? [current]
                        : []);

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
