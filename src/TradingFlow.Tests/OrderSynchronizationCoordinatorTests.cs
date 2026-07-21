using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSynchronizationCoordinatorTests
{
    [Fact]
    public async Task CrossCheck_SecondMissingBrokerCycle_BlocksEntries_ThenRestRepairClears()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var admission = new EntryAdmissionControl();
        var coordinator = CreateCoordinator(repository, admission, time);
        var broker = new RecordingBrokerReader();
        coordinator.MarkStreamConnected();

        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.True(admission.GetSnapshot().EntriesAllowed);
        Assert.Equal(1, coordinator.GetHealth().DivergenceCycles[repository.Current.ClientOrderId]);

        time.Advance(TimeSpan.FromSeconds(15));
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        var blocked = admission.GetSnapshot();
        Assert.False(blocked.EntriesAllowed);
        Assert.Contains(blocked.Blocks, block => block.Code == "ORDER_STREAM_REST_DIVERGENCE");

        broker.ByClientOrderId = CreateBrokerOrder("new", 0m, now.AddSeconds(31));
        time.Advance(TimeSpan.FromSeconds(15));
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.Equal(OrderState.Acked, repository.Current.State);
        Assert.True(admission.GetSnapshot().EntriesAllowed);
        Assert.Empty(coordinator.GetHealth().DivergenceCycles);
        Assert.Equal("broker_rest", repository.Sources.Last());
    }

    [Fact]
    public async Task StreamFill_IsPrimary_AndTerminalStateWaitsForRestConfirmation()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var admission = new EntryAdmissionControl();
        var coordinator = CreateCoordinator(repository, admission, new MutableTimeProvider(now));
        coordinator.MarkStreamConnected();

        await coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "broker-1",
                repository.Current.ClientOrderId,
                "MSFT",
                OrderStatus.Filled,
                10m,
                100.50m,
                now.AddSeconds(1),
                BrokerUpdateSource.TradeStream),
            CancellationToken.None);

        Assert.Equal(OrderState.Filled, repository.Current.State);
        Assert.Equal(["broker_stream", "broker_stream"], repository.Sources);

        var broker = new RecordingBrokerReader
        {
            ByClientOrderId = CreateBrokerOrder("filled", 10m, now.AddSeconds(2), 100.50m)
        };
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.True(admission.GetSnapshot().EntriesAllowed);
        Assert.Empty(coordinator.GetHealth().DivergenceCycles);
        Assert.Equal(1, broker.ByClientOrderIdCalls);
    }

    [Fact]
    public async Task StreamUpdate_ForExternalClientOrder_IsIgnoredWithoutMutatingLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now));

        await coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "external-order",
                "manual-client-id",
                "MSFT",
                OrderStatus.Filled,
                1m,
                100m,
                now,
                BrokerUpdateSource.TradeStream),
            CancellationToken.None);

        Assert.Equal(OrderState.Submitted, repository.Current.State);
        Assert.Empty(repository.Sources);
    }

    [Fact]
    public void EntryAdmissionControl_PreservesIndependentBlocks()
    {
        var admission = new EntryAdmissionControl();
        var now = DateTimeOffset.UtcNow;
        admission.Block("stream", "STREAM_DOWN", "down", now);
        admission.Block("risk", "DAILY_LIMIT", "limit", now);

        Assert.True(admission.Clear("stream"));

        var snapshot = admission.GetSnapshot();
        Assert.False(snapshot.EntriesAllowed);
        Assert.Single(snapshot.Blocks);
        Assert.Equal("risk", snapshot.Blocks[0].Source);
        Assert.Throws<InvalidOperationException>(admission.EnsureEntriesAllowed);
    }

    private static OrderSynchronizationCoordinator CreateCoordinator(
        InMemoryEventRepository repository,
        EntryAdmissionControl admission,
        TimeProvider timeProvider) => new(
        repository,
        new OrderLifecycleService(repository),
        admission,
        timeProvider,
        NullLogger<OrderSynchronizationCoordinator>.Instance);

    private static OrderStateSnapshot CreateSnapshot(OrderState state, DateTimeOffset timestamp) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "SWGA-B-MSFT-20260721-001-12345678",
        state == OrderState.Submitted ? null : "broker-1",
        state,
        timestamp,
        state == OrderState.Submitted ? null : timestamp,
        null,
        null,
        1);

    private static ActiveBrokerOrder CreateBrokerOrder(
        string status,
        decimal filledQuantity,
        DateTimeOffset updatedAt,
        decimal? fillPrice = null) => new(
        "broker-1",
        "MSFT",
        "buy",
        status,
        "limit",
        100m,
        null,
        10m,
        updatedAt.AddMinutes(-1),
        "SWGA-B-MSFT-20260721-001-12345678",
        filledQuantity,
        fillPrice,
        updatedAt);

    private sealed class RecordingBrokerReader : IBrokerOrderReader
    {
        public ActiveBrokerOrder? ByClientOrderId { get; set; }

        public int ByClientOrderIdCalls { get; private set; }

        public Task<IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActiveBrokerOrder>>(
                ByClientOrderId is not null && !OrderStateMachine.IsTerminal(
                    BrokerOrderStateProjection.Project(OrderStatusCodec.ParseBrokerValue(ByClientOrderId.Status)))
                    ? [ByClientOrderId]
                    : []);

        public Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken)
        {
            ByClientOrderIdCalls++;
            return Task.FromResult(ByClientOrderId);
        }
    }

    private sealed class InMemoryEventRepository(OrderStateSnapshot initial) : IOrderEventRepository
    {
        private long eventId = initial.EventId;

        public OrderStateSnapshot Current { get; private set; } = initial;

        public List<string> Sources { get; } = [];

        public Task<OrderStateSnapshot?> GetCurrentAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderStateSnapshot?>(
                String.Equals(Current.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                    ? Current
                    : null);

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderStateSnapshot>>(
                OrderStateMachine.IsTerminal(Current.State) || Current.State == OrderState.Intent
                    ? []
                    : [Current]);

        public Task<OrderTransitionResult> TransitionAsync(
            OrderTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ClientOrderId != Current.ClientOrderId)
            {
                throw new InvalidOperationException("Unknown client order ID.");
            }

            if (Current.State == request.NewState)
            {
                return Task.FromResult(new OrderTransitionResult(Current, Applied: false));
            }

            if (Current.State != request.ExpectedPreviousState ||
                !OrderStateMachine.CanTransition(Current.State, request.NewState))
            {
                throw new InvalidOperationException("Invalid test transition.");
            }

            Sources.Add(request.Source);
            Current = Current with
            {
                BrokerOrderId = request.BrokerOrderId ?? Current.BrokerOrderId,
                State = request.NewState,
                LocalTimestampUtc = request.LocalTimestampUtc,
                BrokerTimestampUtc = request.BrokerTimestampUtc ?? Current.BrokerTimestampUtc,
                FilledQuantity = request.FilledQuantity ?? Current.FilledQuantity,
                FillPrice = request.FillPrice ?? Current.FillPrice,
                EventId = Interlocked.Increment(ref eventId)
            };
            return Task.FromResult(new OrderTransitionResult(Current, Applied: true));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
