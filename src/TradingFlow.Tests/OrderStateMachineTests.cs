using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderStateMachineTests
{
    [Theory]
    [InlineData(OrderState.Intent, OrderState.Submitted)]
    [InlineData(OrderState.Submitted, OrderState.Acked)]
    [InlineData(OrderState.Submitted, OrderState.Rejected)]
    [InlineData(OrderState.Acked, OrderState.PartiallyFilled)]
    [InlineData(OrderState.Acked, OrderState.Filled)]
    [InlineData(OrderState.Acked, OrderState.CancelPending)]
    [InlineData(OrderState.Acked, OrderState.Canceled)]
    [InlineData(OrderState.Acked, OrderState.Rejected)]
    [InlineData(OrderState.Acked, OrderState.Expired)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Filled)]
    [InlineData(OrderState.PartiallyFilled, OrderState.CancelPending)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Canceled)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Expired)]
    [InlineData(OrderState.CancelPending, OrderState.Canceled)]
    [InlineData(OrderState.CancelPending, OrderState.PartiallyFilled)]
    [InlineData(OrderState.CancelPending, OrderState.Filled)]
    [InlineData(OrderState.CancelPending, OrderState.Rejected)]
    [InlineData(OrderState.CancelPending, OrderState.Expired)]
    public void CanTransition_AllowsOnlyDeclaredEdges(OrderState previous, OrderState next)
    {
        Assert.True(OrderStateMachine.CanTransition(previous, next));
    }

    [Theory]
    [InlineData(OrderState.Filled)]
    [InlineData(OrderState.Canceled)]
    [InlineData(OrderState.Rejected)]
    [InlineData(OrderState.Expired)]
    public void TerminalStates_HaveNoOutboundEdges(OrderState terminal)
    {
        Assert.True(OrderStateMachine.IsTerminal(terminal));
        Assert.All(Enum.GetValues<OrderState>(), next =>
            Assert.False(OrderStateMachine.CanTransition(terminal, next)));
    }

    [Fact]
    public void StorageCodec_RoundTripsExactBindingValues()
    {
        var expected = new Dictionary<OrderState, string>
        {
            [OrderState.Intent] = "INTENT",
            [OrderState.Submitted] = "SUBMITTED",
            [OrderState.Acked] = "ACKED",
            [OrderState.PartiallyFilled] = "PARTIALLY_FILLED",
            [OrderState.Filled] = "FILLED",
            [OrderState.CancelPending] = "CANCEL_PENDING",
            [OrderState.Canceled] = "CANCELED",
            [OrderState.Rejected] = "REJECTED",
            [OrderState.Expired] = "EXPIRED"
        };

        Assert.All(expected, pair =>
        {
            Assert.Equal(pair.Value, pair.Key.ToStorageValue());
            Assert.Equal(pair.Key, OrderStateMachine.ParseStorageValue(pair.Value));
        });
        Assert.Throws<InvalidOperationException>(() => OrderStateMachine.ParseStorageValue("acked"));
    }
}

public sealed class SqliteOrderEventRepositoryTests
{
    [Fact]
    public async Task ReserveAsync_CreatesIntentAndInitialEventAtomicallyWithProvenance()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();

        var intent = await database.ReserveAsync();
        var snapshot = await database.Events.GetCurrentAsync(intent.ClientOrderId);

        Assert.NotNull(snapshot);
        Assert.Equal(OrderState.Intent, snapshot.State);
        await using var context = database.CreateContext();
        var persisted = await context.OrderEvents.SingleAsync();
        Assert.Null(persisted.PreviousState);
        Assert.Equal("INTENT", persisted.NewState);
        Assert.Equal(intent.RunId, persisted.RunId);
        Assert.Equal(intent.ConfigHash, persisted.ConfigHash);
        Assert.Equal(intent.CodeVersion, persisted.CodeVersion);
    }

    [Fact]
    public async Task TransitionAsync_ValidLifecycle_AppendsEveryStateAndCumulativePartialFill()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        var timestamp = intent.CreatedAtUtc.AddSeconds(1);

        await database.TransitionAsync(intent, OrderState.Intent, OrderState.Submitted, timestamp);
        await database.TransitionAsync(
            intent,
            OrderState.Submitted,
            OrderState.Acked,
            timestamp.AddSeconds(1),
            brokerOrderId: "broker-1",
            brokerTimestampUtc: timestamp.AddMilliseconds(900));
        await database.TransitionAsync(
            intent,
            OrderState.Acked,
            OrderState.PartiallyFilled,
            timestamp.AddSeconds(2),
            brokerOrderId: "broker-1",
            filledQuantity: 2m,
            fillPrice: 100m);
        await database.TransitionAsync(
            intent,
            OrderState.PartiallyFilled,
            OrderState.PartiallyFilled,
            timestamp.AddSeconds(3),
            brokerOrderId: "broker-1",
            filledQuantity: 4m,
            fillPrice: 100.25m);
        var filled = await database.TransitionAsync(
            intent,
            OrderState.PartiallyFilled,
            OrderState.Filled,
            timestamp.AddSeconds(4),
            brokerOrderId: "broker-1",
            filledQuantity: 10m,
            fillPrice: 100.50m);

        Assert.Equal(OrderState.Filled, filled.Snapshot.State);
        Assert.Equal(10m, filled.Snapshot.FilledQuantity);
        await using var context = database.CreateContext();
        Assert.Equal(
            ["INTENT", "SUBMITTED", "ACKED", "PARTIALLY_FILLED", "PARTIALLY_FILLED", "FILLED"],
            await context.OrderEvents.OrderBy(record => record.EventId).Select(record => record.NewState).ToListAsync());
    }

    [Fact]
    public async Task TransitionAsync_ExactReplay_IsNoOp()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        var request = database.CreateTransition(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));

        var first = await database.Events.TransitionAsync(request);
        var replay = await database.Events.TransitionAsync(request with
        {
            LocalTimestampUtc = request.LocalTimestampUtc.AddSeconds(1)
        });

        Assert.True(first.Applied);
        Assert.False(replay.Applied);
        await using var context = database.CreateContext();
        Assert.Equal(2, await context.OrderEvents.CountAsync());
    }

    [Fact]
    public async Task TransitionAsync_InvalidAndTerminalTransitions_FailWithoutAppending()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Acked,
            intent.CreatedAtUtc.AddSeconds(1)));
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(2));
        await database.TransitionAsync(
            intent,
            OrderState.Submitted,
            OrderState.Rejected,
            intent.CreatedAtUtc.AddSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransitionAsync(
            intent,
            OrderState.Rejected,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(4)));

        await using var context = database.CreateContext();
        Assert.Equal(3, await context.OrderEvents.CountAsync());
    }

    [Fact]
    public async Task TransitionAsync_BrokerIdCannotChange_AndPartialFillCannotDecrease()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        var timestamp = intent.CreatedAtUtc.AddSeconds(1);
        await database.TransitionAsync(intent, OrderState.Intent, OrderState.Submitted, timestamp);
        await database.TransitionAsync(
            intent,
            OrderState.Submitted,
            OrderState.Acked,
            timestamp.AddSeconds(1),
            brokerOrderId: "broker-1");
        await database.TransitionAsync(
            intent,
            OrderState.Acked,
            OrderState.PartiallyFilled,
            timestamp.AddSeconds(2),
            brokerOrderId: "broker-1",
            filledQuantity: 5m,
            fillPrice: 100m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransitionAsync(
            intent,
            OrderState.PartiallyFilled,
            OrderState.PartiallyFilled,
            timestamp.AddSeconds(3),
            brokerOrderId: "broker-1",
            filledQuantity: 4m,
            fillPrice: 100m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransitionAsync(
            intent,
            OrderState.PartiallyFilled,
            OrderState.Filled,
            timestamp.AddSeconds(4),
            brokerOrderId: "broker-2",
            filledQuantity: 10m,
            fillPrice: 101m));
    }

    [Fact]
    public async Task TransitionAsync_BrokerSourceWithoutBrokerTimestamp_FailsClosed()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Events.TransitionAsync(new OrderTransitionRequest(
                intent.ClientOrderId,
                OrderState.Submitted,
                OrderState.Acked,
                Source: "broker_stream",
                LocalTimestampUtc: intent.CreatedAtUtc.AddSeconds(2),
                BrokerOrderId: "broker-1",
                PayloadJson: "{}")));

        Assert.Contains("broker timestamp", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransitionAsync_ConcurrentRepositoryInstances_AppendStateOnce()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        var first = new SqliteOrderEventRepository(database.ContextFactory);
        var second = new SqliteOrderEventRepository(database.ContextFactory);
        var request = database.CreateTransition(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));

        var results = await Task.WhenAll(
            first.TransitionAsync(request),
            second.TransitionAsync(request));

        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => !result.Applied);
        await using var context = database.CreateContext();
        Assert.Equal(1, await context.OrderEvents.CountAsync(record => record.NewState == "SUBMITTED"));
    }

    [Fact]
    public async Task ListReconcilableAsync_ReturnsOnlyLatestNonTerminalBrokerRelevantState()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();

        Assert.Empty(await database.Events.ListReconcilableAsync());
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));

        var active = await database.Events.ListReconcilableAsync();
        Assert.Single(active);
        Assert.Equal(OrderState.Submitted, active[0].State);

        await database.TransitionAsync(
            intent,
            OrderState.Submitted,
            OrderState.Rejected,
            intent.CreatedAtUtc.AddSeconds(2));

        Assert.Empty(await database.Events.ListReconcilableAsync());
    }

    [Fact]
    public async Task LifecycleService_PartialFillFromSubmitted_JournalsAckBeforeFill()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));
        var lifecycle = new OrderLifecycleService(database.Events);

        var snapshot = await lifecycle.ApplyBrokerUpdateAsync(new OrderUpdate(
            "broker-1",
            intent.ClientOrderId,
            intent.Symbol,
            OrderStatus.PartiallyFilled,
            4m,
            100.25m,
            intent.CreatedAtUtc.AddSeconds(2),
            BrokerUpdateSource.TradeStream));
        snapshot = await lifecycle.ApplyBrokerUpdateAsync(new OrderUpdate(
            "broker-1",
            intent.ClientOrderId,
            intent.Symbol,
            OrderStatus.PartiallyFilled,
            7m,
            100.40m,
            intent.CreatedAtUtc.AddSeconds(3),
            BrokerUpdateSource.TradeStream));

        Assert.Equal(OrderState.PartiallyFilled, snapshot.State);
        Assert.Equal(7m, snapshot.FilledQuantity);
        await using var context = database.CreateContext();
        Assert.Equal(
            new[] { "INTENT", "SUBMITTED", "ACKED", "PARTIALLY_FILLED", "PARTIALLY_FILLED" },
            await context.OrderEvents.OrderBy(record => record.EventId).Select(record => record.NewState).ToArrayAsync());
    }

    [Fact]
    public async Task LifecycleService_DuplicateFillUpdate_IsIdempotent()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));
        var lifecycle = new OrderLifecycleService(database.Events);
        var update = new OrderUpdate(
            "broker-1",
            intent.ClientOrderId,
            intent.Symbol,
            OrderStatus.Filled,
            10m,
            100.50m,
            intent.CreatedAtUtc.AddSeconds(2),
            BrokerUpdateSource.TradeStream);

        var first = await lifecycle.ApplyBrokerUpdateAsync(update);
        var replay = await lifecycle.ApplyBrokerUpdateAsync(update);

        Assert.Equal(OrderState.Filled, first.State);
        Assert.Equal(first.EventId, replay.EventId);
        await using var context = database.CreateContext();
        Assert.Equal(4, await context.OrderEvents.CountAsync());
    }

    [Theory]
    [InlineData(OrderStatus.Canceled, OrderState.Canceled)]
    [InlineData(OrderStatus.Rejected, OrderState.Rejected)]
    [InlineData(OrderStatus.Expired, OrderState.Expired)]
    public async Task LifecycleService_TerminalBrokerUpdate_UsesCanonicalTerminalState(
        OrderStatus brokerStatus,
        OrderState expectedState)
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));
        var lifecycle = new OrderLifecycleService(database.Events);

        var snapshot = await lifecycle.ApplyBrokerUpdateAsync(new OrderUpdate(
            "broker-1",
            intent.ClientOrderId,
            intent.Symbol,
            brokerStatus,
            0m,
            0m,
            intent.CreatedAtUtc.AddSeconds(2),
            BrokerUpdateSource.TradeStream));

        Assert.Equal(expectedState, snapshot.State);
    }

    [Fact]
    public async Task LifecycleService_UnknownIntent_FailsClosed()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var lifecycle = new OrderLifecycleService(database.Events);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.ApplyBrokerUpdateAsync(new OrderUpdate(
                "broker-1",
                "SWGA-B-MSFT-20260721-001-12345678",
                "MSFT",
                OrderStatus.Accepted,
                0m,
                0m,
                DateTimeOffset.UtcNow,
                BrokerUpdateSource.TradeStream)));

        Assert.Contains("unknown client order ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleService_CancelRequest_IsJournaledBeforeBrokerCancellation()
    {
        await using var database = await OrderEventTestDatabase.CreateAsync();
        var intent = await database.ReserveAsync();
        await database.TransitionAsync(
            intent,
            OrderState.Intent,
            OrderState.Submitted,
            intent.CreatedAtUtc.AddSeconds(1));
        var lifecycle = new OrderLifecycleService(database.Events);
        await lifecycle.ApplyBrokerUpdateAsync(new OrderUpdate(
            "broker-1",
            intent.ClientOrderId,
            intent.Symbol,
            OrderStatus.Accepted,
            0m,
            0m,
            intent.CreatedAtUtc.AddSeconds(2),
            BrokerUpdateSource.TradeStream));

        var pending = await lifecycle.RequestCancelAsync(intent.ClientOrderId, "broker-1");
        var replay = await lifecycle.RequestCancelAsync(intent.ClientOrderId, "broker-1");

        Assert.Equal(OrderState.CancelPending, pending.State);
        Assert.Equal(pending.EventId, replay.EventId);
        await using var context = database.CreateContext();
        Assert.Equal(1, await context.OrderEvents.CountAsync(record => record.NewState == "CANCEL_PENDING"));
    }

    private sealed class OrderEventTestDatabase : IAsyncDisposable
    {
        private readonly string root;
        private readonly DbContextOptions<TradingFlowDbContext> options;

        private OrderEventTestDatabase(
            string root,
            DbContextOptions<TradingFlowDbContext> options,
            TestDbContextFactory contextFactory)
        {
            this.root = root;
            this.options = options;
            ContextFactory = contextFactory;
            Intents = new SqliteOrderIntentRepository(contextFactory);
            Events = new SqliteOrderEventRepository(contextFactory);
        }

        public TestDbContextFactory ContextFactory { get; }

        public SqliteOrderIntentRepository Intents { get; }

        public SqliteOrderEventRepository Events { get; }

        public static async Task<OrderEventTestDatabase> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"tradingflow-order-events-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "tradingflow.db")};Pooling=False")
                .AddInterceptors(new SqliteConnectionDurabilityInterceptor())
                .Options;
            var factory = new TestDbContextFactory(options);
            await using var context = new TradingFlowDbContext(options);
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
            return new OrderEventTestDatabase(root, options, factory);
        }

        public TradingFlowDbContext CreateContext() => new(options);

        public Task<OrderIntentRecord> ReserveAsync()
        {
            var run = new ProductionRun
            {
                RunId = Guid.NewGuid(),
                SchemaVersion = 1,
                ConfigHash = new string('a', 64),
                CodeVersion = new string('b', 40),
                Profile = "paper",
                Status = "running",
                StartedAtUtc = new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero)
            };
            var reservation = new OrderIntentReservation(
                Guid.NewGuid(),
                CandidateId: null,
                StrategyId: "SWGA",
                Symbol: "MSFT",
                Side: "buy",
                OrderType: "limit",
                TimeInForce: "day",
                RequestedQuantity: 10m,
                LimitPrice: 100m,
                StopPrice: 98m,
                SessionDate: new DateOnly(2026, 7, 21),
                CreatedAtUtc: new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
                RequestJson: "{\"symbol\":\"MSFT\",\"quantity\":10}");
            return Intents.ReserveAsync(run, reservation);
        }

        public OrderTransitionRequest CreateTransition(
            OrderIntentRecord intent,
            OrderState previous,
            OrderState next,
            DateTimeOffset timestamp,
            string? brokerOrderId = null,
            DateTimeOffset? brokerTimestampUtc = null,
            decimal? filledQuantity = null,
            decimal? fillPrice = null) => new(
                intent.ClientOrderId,
                previous,
                next,
                Source: "engine",
                LocalTimestampUtc: timestamp,
                BrokerTimestampUtc: brokerTimestampUtc,
                BrokerOrderId: brokerOrderId,
                FilledQuantity: filledQuantity,
                FillPrice: fillPrice,
                PayloadJson: $"{{\"state\":\"{next.ToStorageValue()}\"}}");

        public Task<OrderTransitionResult> TransitionAsync(
            OrderIntentRecord intent,
            OrderState previous,
            OrderState next,
            DateTimeOffset timestamp,
            string? brokerOrderId = null,
            DateTimeOffset? brokerTimestampUtc = null,
            decimal? filledQuantity = null,
            decimal? fillPrice = null) =>
            Events.TransitionAsync(CreateTransition(
                intent,
                previous,
                next,
                timestamp,
                brokerOrderId,
                brokerTimestampUtc,
                filledQuantity,
                fillPrice));

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
