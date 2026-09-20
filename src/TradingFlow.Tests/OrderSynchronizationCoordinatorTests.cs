using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSynchronizationCoordinatorTests
{
    [Fact]
    public async Task CrossCheck_OrphanTimeoutThenSecondMissingCycle_BlocksEntries_ThenRestRepairClears()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var admission = new EntryAdmissionControl();
        var coordinator = CreateCoordinator(repository, admission, time);
        var broker = new RecordingBrokerReader();
        coordinator.MarkStreamConnected();

        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.DoesNotContain(
            admission.GetSnapshot().Blocks,
            block => block.Code == "ORDER_STREAM_APPLY_FAILED");
        Assert.Empty(coordinator.GetHealth().DivergenceCycles);

        time.Advance(TimeSpan.FromSeconds(15));
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.True(admission.GetSnapshot().EntriesAllowed);
        Assert.Empty(coordinator.GetHealth().DivergenceCycles);

        time.Advance(TimeSpan.FromSeconds(15));
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
                "buy",
                OrderStatus.Filled,
                10m,
                100.50m,
                10m,
                10m,
                "execution-1",
                now.AddSeconds(1),
                BrokerUpdateSource.TradeStream,
                100.50m),
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
    public async Task DelayedStreamObservation_AfterNewerRestState_IsIgnoredWithoutBlockingEntries()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var current = new OrderStateSnapshot(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "SWGA-B-MSFT-20260721-001-12345678",
            "broker-1",
            OrderState.PartiallyFilled,
            now.AddSeconds(20),
            now.AddSeconds(20),
            6m,
            100m,
            3);
        var repository = new InMemoryEventRepository(current);
        var admission = new EntryAdmissionControl();
        var coordinator = CreateCoordinator(repository, admission, new MutableTimeProvider(now.AddSeconds(30)));
        coordinator.MarkStreamConnected();

        await coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "broker-1",
                current.ClientOrderId,
                "MSFT",
                "buy",
                OrderStatus.PartiallyFilled,
                4m,
                100m,
                4m,
                4m,
                "delayed-execution",
                now.AddSeconds(10),
                BrokerUpdateSource.TradeStream,
                100m),
            CancellationToken.None);

        Assert.Equal(current, repository.Current);
        Assert.Empty(repository.Sources);
        Assert.DoesNotContain(
            admission.GetSnapshot().Blocks,
            block => block.Code == "ORDER_STREAM_APPLY_FAILED");
        Assert.Empty(coordinator.GetHealth().DivergenceCycles);
    }

    [Fact]
    public async Task StreamFill_KnownIntent_AttributesPositionToOpeningStrategy()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var positions = new InMemoryPositionLedgerRepository();
        var intents = new Mock<IOrderIntentRepository>(MockBehavior.Strict);
        intents.Setup(store => store.GetByClientOrderIdAsync(
                repository.Current.ClientOrderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord
            {
                ClientOrderId = repository.Current.ClientOrderId,
                StrategyId = "SWGA",
                Symbol = "MSFT",
                Side = "buy",
                RequestedQuantity = 10m
            });
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now),
            positions,
            intents.Object);

        await coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "broker-1",
                repository.Current.ClientOrderId,
                "MSFT",
                "buy",
                OrderStatus.Filled,
                10m,
                100.50m,
                10m,
                10m,
                "execution-owned",
                now.AddSeconds(1),
                BrokerUpdateSource.TradeStream,
                100.50m),
            CancellationToken.None);

        var position = await positions.GetCurrentAsync("paper-account", "MSFT");
        Assert.Equal("SWGA", position?.StrategyId);
    }

    [Fact]
    public async Task StreamFill_MismatchedOwnedIntentSymbol_FailsBeforePositionMutation()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var positions = new InMemoryPositionLedgerRepository();
        var admission = new EntryAdmissionControl();
        var coordinator = CreateCoordinator(
            repository,
            admission,
            new MutableTimeProvider(now),
            positions);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessStreamUpdateAsync(
                new OrderUpdate(
                    "broker-1",
                    repository.Current.ClientOrderId,
                    "AAPL",
                    "buy",
                    OrderStatus.Filled,
                    10m,
                    100.50m,
                    10m,
                    10m,
                    "execution-mismatched",
                    now.AddSeconds(1),
                    BrokerUpdateSource.TradeStream,
                    100.50m),
                CancellationToken.None));

        Assert.Contains("does not match owned intent symbol", error.Message, StringComparison.Ordinal);
        Assert.Null(await positions.GetCurrentAsync("paper-account", "AAPL"));
        Assert.Equal(OrderState.Submitted, repository.Current.State);
        Assert.False(admission.GetSnapshot().EntriesAllowed);
    }

    [Fact]
    public async Task StreamUpdate_ForExternalClientOrder_IsIgnoredWithoutMutatingLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var positions = new InMemoryPositionLedgerRepository();
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now),
            positions);

        await coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "external-order",
                "manual-client-id",
                "MSFT",
                "buy",
                OrderStatus.Filled,
                1m,
                100m,
                1m,
                1m,
                "external-execution-1",
                now,
                BrokerUpdateSource.TradeStream,
                100m),
            CancellationToken.None);

        Assert.Equal(OrderState.Submitted, repository.Current.State);
        Assert.Empty(repository.Sources);
        Assert.Null(await positions.GetCurrentAsync("paper-account", "MSFT"));
    }

    [Fact]
    public async Task RestDiscoveredKnownFill_RepairsPositionLedgerIdempotently()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var positions = new InMemoryPositionLedgerRepository();
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now.AddSeconds(31)),
            positions);
        coordinator.MarkStreamConnected();
        var broker = new RecordingBrokerReader
        {
            ByClientOrderId = CreateBrokerOrder("filled", 10m, now.AddSeconds(30), 100.50m)
        };

        await coordinator.CrossCheckAsync(broker, CancellationToken.None);
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.Equal(OrderState.Filled, repository.Current.State);
        Assert.Equal(10m, (await positions.GetCurrentAsync("paper-account", "MSFT"))?.Quantity);
        Assert.Single(positions.ExecutionIds);
    }

    [Fact]
    public async Task RestDiscoveredCanceledOrderWithFill_RepairsPositionBeforeTerminalizing()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now));
        var positions = new InMemoryPositionLedgerRepository();
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now.AddSeconds(31)),
            positions);
        coordinator.MarkStreamConnected();
        var broker = new RecordingBrokerReader
        {
            ByClientOrderId = CreateBrokerOrder("canceled", 4m, now.AddSeconds(30), 100.50m)
        };

        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.Equal(OrderState.Canceled, repository.Current.State);
        Assert.Equal(4m, repository.Current.FilledQuantity);
        Assert.Equal(4m, (await positions.GetCurrentAsync("paper-account", "MSFT"))?.Quantity);
        Assert.Single(positions.ExecutionIds);
    }

    [Fact]
    public async Task RestRepair_DoesNotDoubleCountStreamFillWhenLifecycleWriteInitiallyFails()
    {
        var now = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryEventRepository(CreateSnapshot(OrderState.Submitted, now))
        {
            FailTransitions = true
        };
        var positions = new InMemoryPositionLedgerRepository();
        var coordinator = CreateCoordinator(
            repository,
            new EntryAdmissionControl(),
            new MutableTimeProvider(now.AddSeconds(31)),
            positions);
        coordinator.MarkStreamConnected();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ProcessStreamUpdateAsync(
            new OrderUpdate(
                "broker-1",
                repository.Current.ClientOrderId,
                "MSFT",
                "buy",
                OrderStatus.Filled,
                10m,
                100.50m,
                10m,
                10m,
                "execution-1",
                now.AddSeconds(1),
                BrokerUpdateSource.TradeStream,
                100.50m),
            CancellationToken.None));

        repository.FailTransitions = false;
        var broker = new RecordingBrokerReader
        {
            ByClientOrderId = CreateBrokerOrder("filled", 10m, now.AddSeconds(30), 100.50m)
        };
        await coordinator.CrossCheckAsync(broker, CancellationToken.None);

        Assert.Equal(OrderState.Filled, repository.Current.State);
        Assert.Equal(10m, (await positions.GetCurrentAsync("paper-account", "MSFT"))?.Quantity);
        Assert.Single(positions.ExecutionIds);
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

    [Fact]
    public async Task EntryAdmissionControl_BlockIsAtomicWithDispatchDrain()
    {
        var admission = new EntryAdmissionControl();
        var dispatch = admission.BeginEntryDispatch();

        admission.Block(
            "durable_order_recovery",
            "DURABLE_ORDER_RECOVERY_IN_PROGRESS",
            "Recovery is active.",
            DateTimeOffset.UtcNow);
        var drain = admission.WaitForEntryDispatchesToDrainAsync();

        Assert.False(drain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => admission.BeginEntryDispatch());
        dispatch.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static OrderSynchronizationCoordinator CreateCoordinator(
        InMemoryEventRepository repository,
        EntryAdmissionControl admission,
        TimeProvider timeProvider,
        InMemoryPositionLedgerRepository? positions = null,
        IOrderIntentRepository? intents = null)
    {
        if (intents is null)
        {
            var intentStore = new Mock<IOrderIntentRepository>(MockBehavior.Strict);
            intentStore
                .Setup(store => store.GetByClientOrderIdAsync(
                    repository.Current.ClientOrderId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OrderIntentRecord
                {
                    ClientOrderId = repository.Current.ClientOrderId,
                    AccountId = "paper-account",
                    StrategyId = "SWGA",
                    Symbol = "MSFT",
                    Side = "buy",
                    RequestedQuantity = 10m
                });
            intents = intentStore.Object;
        }

        var positionLedger = positions ?? new InMemoryPositionLedgerRepository();
        repository.Positions = positionLedger;
        return new OrderSynchronizationCoordinator(
            repository,
            intents,
            new OrderLifecycleService(repository),
            admission,
            new AccountReconciliationOptions(60, 30),
            timeProvider,
            NullLogger<OrderSynchronizationCoordinator>.Instance);
    }

    private static ProductionRun CreateRun(DateTimeOffset startedAt) => new()
    {
        RunId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Profile = "paper",
        Status = "running",
        StartedAtUtc = startedAt,
        ConfigHash = new string('a', 64),
        CodeVersion = new string('b', 40)
    };

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

    private sealed class RecordingBrokerReader : IAccountScopedBrokerOrderReader
    {
        public ActiveBrokerOrder? ByClientOrderId { get; set; }

        public int ByClientOrderIdCalls { get; private set; }

        public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BrokerAccountSnapshot(
                "paper-account",
                "ACTIVE",
                false,
                false,
                false,
                true,
                100_000m,
                100_000m,
                100_000m,
                0m,
                0m,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

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

        public bool FailTransitions { get; set; }

        public InMemoryPositionLedgerRepository? Positions { get; set; }

        public Task<OrderStateSnapshot?> GetCurrentAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderStateSnapshot?>(
                String.Equals(Current.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                    ? Current
                    : null);

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderStateSnapshot>>(
                OrderStateMachine.IsTerminal(Current.State) || Current.State == OrderState.Intent
                    ? []
                    : [Current]);

        public Task<OrderTransitionResult> TransitionAsync(
            OrderTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (FailTransitions)
            {
                throw new InvalidOperationException("Simulated lifecycle persistence failure.");
            }

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
            if (request.PositionFill is not null)
            {
                Positions?.ApplyProjection(request);
            }
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

        public Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
            BrokerOrderReplacementTransition request,
            CancellationToken cancellationToken = default)
        {
            if (!Current.ClientOrderId.Equals(request.OwnerClientOrderId, StringComparison.Ordinal) ||
                !String.Equals(Current.BrokerOrderId, request.PredecessorBrokerOrderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid test broker replacement.");
            }

            Current = Current with
            {
                BrokerOrderId = request.SuccessorBrokerOrderId,
                LocalTimestampUtc = request.LocalTimestampUtc,
                BrokerTimestampUtc = request.BrokerTimestampUtc,
                EventId = Interlocked.Increment(ref eventId)
            };
            return Task.FromResult(Current);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class InMemoryPositionLedgerRepository : IPositionLedgerRepository
    {
        private readonly Dictionary<string, PositionLedgerSnapshot> snapshots =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> executionIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> accountedByBrokerOrder = new(StringComparer.Ordinal);
        private long eventId;

        public IReadOnlyCollection<string> ExecutionIds => executionIds;

        public Task<PositionLedgerSnapshot> AppendFillAsync(
            PositionFillAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            var key = Key(request.AccountId, request.Symbol);
            if (!executionIds.Add(request.ExecutionId) && snapshots.TryGetValue(key, out var replay))
            {
                return Task.FromResult(replay);
            }

            var nextEventId = Interlocked.Increment(ref eventId);
            var continuingGeneration = snapshots.TryGetValue(key, out var generationPosition) &&
                generationPosition.Quantity != 0m &&
                (request.QuantityAfter == 0m ||
                 Math.Sign(generationPosition.Quantity) == Math.Sign(request.QuantityAfter));
            var snapshot = new PositionLedgerSnapshot(
                request.AccountId,
                request.Symbol,
                request.QuantityAfter,
                snapshots.TryGetValue(key, out var previous) && previous.Quantity != 0m
                    ? previous.StrategyId
                    : request.ExecutionStrategyId,
                request.BrokerTimestampUtc,
                request.LocalTimestampUtc,
                nextEventId,
                continuingGeneration ? generationPosition!.PositionGenerationEventId : nextEventId,
                continuingGeneration ? generationPosition!.PositionGenerationClientOrderId : request.ClientOrderId,
                request.ClientOrderId,
                request.FillPrice,
                request.Side);
            snapshots[key] = snapshot;
            var brokerKey = Key(request.AccountId, request.BrokerOrderId);
            accountedByBrokerOrder[brokerKey] =
                accountedByBrokerOrder.GetValueOrDefault(brokerKey) + request.FillQuantity;
            return Task.FromResult(snapshot);
        }

        public Task<decimal> GetAccountedFillQuantityAsync(
            string accountId,
            string brokerOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(accountedByBrokerOrder.GetValueOrDefault(Key(accountId, brokerOrderId)));

        public Task<PositionLedgerSnapshot?> GetCurrentAsync(
            string accountId,
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PositionLedgerSnapshot?>(snapshots.GetValueOrDefault(Key(accountId, symbol)));

        public Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PositionLedgerSnapshot>>(
                snapshots.Values.Where(item => item.AccountId == accountId).ToArray());

        public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentForRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunOwnedPositionLedgerSnapshot>>([]);

        public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentOwnersForSymbolAsync(
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunOwnedPositionLedgerSnapshot>>([]);

        public void ApplyProjection(OrderTransitionRequest request)
        {
            var projection = request.PositionFill
                ?? throw new InvalidOperationException("Position projection is required.");
            if (!executionIds.Add(projection.ExecutionId))
            {
                return;
            }

            var accountId = "paper-account";
            var brokerOrderId = request.BrokerOrderId ?? "broker-1";
            var brokerKey = Key(accountId, brokerOrderId);
            var accounted = accountedByBrokerOrder.GetValueOrDefault(brokerKey);
            var fillDelta = projection.BrokerCumulativeFillQuantity - accounted;
            if (fillDelta <= 0m)
            {
                return;
            }

            var key = Key(accountId, projection.Symbol);
            var previousQuantity = snapshots.GetValueOrDefault(key)?.Quantity ?? 0m;
            var signedDelta = projection.Side.Equals("buy", StringComparison.OrdinalIgnoreCase)
                ? fillDelta
                : -fillDelta;
            var quantityAfter = projection.AuthoritativePositionQuantity ?? previousQuantity + signedDelta;
            var nextEventId = Interlocked.Increment(ref eventId);
            var generationPosition = snapshots.GetValueOrDefault(key);
            var continuingGeneration = generationPosition is not null &&
                generationPosition.Quantity != 0m &&
                (quantityAfter == 0m ||
                 Math.Sign(generationPosition.Quantity) == Math.Sign(quantityAfter));
            snapshots[key] = new PositionLedgerSnapshot(
                accountId,
                projection.Symbol,
                quantityAfter,
                snapshots.GetValueOrDefault(key)?.StrategyId ?? "SWGA",
                request.BrokerTimestampUtc ?? request.LocalTimestampUtc,
                request.LocalTimestampUtc,
                nextEventId,
                continuingGeneration ? generationPosition!.PositionGenerationEventId : nextEventId,
                continuingGeneration ? generationPosition!.PositionGenerationClientOrderId : request.ClientOrderId,
                request.ClientOrderId,
                projection.AuthoritativeLastFillPrice ?? projection.BrokerCumulativeAverageFillPrice,
                projection.Side);
            accountedByBrokerOrder[brokerKey] = accounted + fillDelta;
        }

        private static string Key(string accountId, string value) => $"{accountId}|{value}";
    }
}
