using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class AccountReconciliationServiceTests
{
    [Fact]
    public async Task StartupCleanSnapshot_ClearsFailClosedAdmissionBlock()
    {
        var fixture = new Fixture();

        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);

        var result = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.Equal("clean", result.Status);
        Assert.Empty(result.Differences);
        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.True(fixture.Service.GetHealth().InitialReconciliationCompleted);
    }

    [Fact]
    public async Task UnexpectedBrokerPosition_BlocksUntilExplicitAck_AndChangedDiffReblocks()
    {
        var fixture = new Fixture();
        fixture.Broker.Positions = [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)];

        var mismatch = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.Equal("reconcile_mismatch", mismatch.Status);
        Assert.Contains(mismatch.Differences, item => item.Kind == "position_quantity" && item.Key == "MSFT");
        Assert.Contains(mismatch.Differences, item => item.Kind == "missing_protective_order" && item.Key == "MSFT");
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Contains(fixture.Admission.GetSnapshot().Blocks, block => block.Code == "RECONCILE_MISMATCH");

        await fixture.Service.AcknowledgeAsync(
            mismatch.ReconciliationId,
            "operator@example.com",
            "Verified a pre-existing manual paper position.",
            CancellationToken.None);

        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Equal("operator@example.com", fixture.Reconciliations.LastAcknowledged?.AcknowledgedBy);
        Assert.Equal(
            "Verified a pre-existing manual paper position.",
            fixture.Reconciliations.LastAcknowledged?.AcknowledgementReason);

        await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Equal("acknowledged_mismatch", fixture.Service.GetHealth().Status);

        fixture.Broker.Positions = [new BrokerPosition("MSFT", "long", 11m, 100m, 101m, 11m)];
        await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Equal("reconcile_mismatch", fixture.Service.GetHealth().Status);
    }

    [Fact]
    public async Task AcknowledgeNonOutstandingReconciliation_FailsWithoutClearingBlock()
    {
        var fixture = new Fixture();
        fixture.Broker.Positions = [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)];
        var mismatch = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.AcknowledgeAsync(
            Guid.NewGuid(),
            "operator@example.com",
            "This identifier is stale.",
            CancellationToken.None));

        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Null(fixture.Reconciliations.LastAcknowledged);
        Assert.Equal(mismatch.ReconciliationId, fixture.Service.GetHealth().OutstandingReconciliationId);
    }

    [Fact]
    public async Task MissingBrokerOrder_IsDeferredUntilConfiguredOrphanTimeout()
    {
        var fixture = new Fixture();
        fixture.Orders.Current =
        [
            new OrderStateSnapshot(
                Guid.NewGuid(),
                "SWGA-B-MSFT-20260721-001-12345678",
                null,
                OrderState.Submitted,
                fixture.Time.GetUtcNow().AddSeconds(-20),
                null,
                null,
                null,
                1)
        ];

        var beforeTimeout = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.Empty(beforeTimeout.Differences);
        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);

        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        var orphan = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.Single(orphan.Differences, item => item.Kind == "orphan_local_order");
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
    }

    [Fact]
    public async Task MatchingSignedShortPositionAndOpenOrder_AreClean()
    {
        var fixture = new Fixture();
        fixture.Positions.Current["MSFT"] = new PositionLedgerSnapshot(
            "MSFT",
            -5m,
            fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow(),
            1,
            "SWGA-S-MSFT-20260721-001-12345678",
            100m,
            "sell");
        fixture.Broker.Positions = [new BrokerPosition("MSFT", "short", 5m, 100m, 99m, 5m)];
        var localOrder = new OrderStateSnapshot(
            Guid.NewGuid(),
            "SWGA-S-MSFT-20260721-001-12345678",
            "broker-1",
            OrderState.Acked,
            fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow(),
            null,
            null,
            1);
        var localStopOrder = new OrderStateSnapshot(
            Guid.NewGuid(),
            "BACKSTOP-B-MSFT-20260721-001-12345678",
            "stop-1",
            OrderState.Acked,
            fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow(),
            null,
            null,
            2);
        fixture.Orders.Current = [localOrder, localStopOrder];
        var openOrders = new[]
        {
            new ActiveBrokerOrder(
                "broker-1", "MSFT", "sell", "new", "limit", 99m, null, 5m,
                fixture.Time.GetUtcNow(), localOrder.ClientOrderId, 0m, null, fixture.Time.GetUtcNow()),
            new ActiveBrokerOrder(
                "stop-1", "MSFT", "buy", "new", "stop", null, 102m, 5m,
                fixture.Time.GetUtcNow(), "BACKSTOP-B-MSFT-20260721-001-12345678", 0m, null, fixture.Time.GetUtcNow())
        };

        var result = await fixture.Service.ReconcileAsync(fixture.Broker, openOrders, CancellationToken.None);

        Assert.Empty(result.Differences);
        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
    }

    [Fact]
    public async Task ForeignOpenBrokerOrder_IsReportedAndBlocksEntries()
    {
        var fixture = new Fixture();
        var brokerOrder = new ActiveBrokerOrder(
            "manual-broker-order",
            "MSFT",
            "buy",
            "new",
            "limit",
            100m,
            null,
            1m,
            fixture.Time.GetUtcNow(),
            "manual-client-id",
            0m,
            null,
            fixture.Time.GetUtcNow());

        var result = await fixture.Service.ReconcileAsync(
            fixture.Broker,
            [brokerOrder],
            CancellationToken.None);

        Assert.Single(result.Differences, difference =>
            difference.Kind == "unexpected_broker_order" &&
            difference.Key == "manual-client-id");
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
    }

    [Fact]
    public async Task BrokerGeneratedBracketStopLeg_IsOwnedThroughPersistedParentClientId()
    {
        var fixture = new Fixture();
        const string parentClientId = "SWGA-B-MSFT-20260721-001-12345678";
        fixture.Positions.Current["MSFT"] = new PositionLedgerSnapshot(
            "MSFT", 10m, fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), 1,
            parentClientId, 100m, "buy");
        fixture.Broker.Positions = [new BrokerPosition("MSFT", "long", 10m, 100m, 101m, 10m)];
        fixture.Orders.Additional[parentClientId] = new OrderStateSnapshot(
            Guid.NewGuid(), parentClientId, "parent-1", OrderState.Filled,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), 10m, 100m, 1);
        var stopLeg = new ActiveBrokerOrder(
            "stop-leg-1", "MSFT", "sell", "new", "stop", null, 98m, 10m,
            fixture.Time.GetUtcNow(), "broker-generated-leg-id", 0m, null,
            fixture.Time.GetUtcNow(), parentClientId);

        var result = await fixture.Service.ReconcileAsync(
            fixture.Broker,
            [stopLeg],
            CancellationToken.None);

        Assert.Empty(result.Differences);
        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Service = new AccountReconciliationService(
                Orders,
                Positions,
                Reconciliations,
                Admission,
                Protector,
                new ReconciliationRunContext(CreateRun(Time.GetUtcNow())),
                new AccountReconciliationOptions(60, 30),
                Time,
                NullLogger<AccountReconciliationService>.Instance);
        }

        public MutableTimeProvider Time { get; } = new(
            new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero));
        public InMemoryOrderEvents Orders { get; } = new();
        public InMemoryPositions Positions { get; } = new();
        public InMemoryReconciliations Reconciliations { get; } = new();
        public EntryAdmissionControl Admission { get; } = new();
        public RecordingProtector Protector { get; } = new();
        public RecordingBroker Broker { get; } = new();
        public AccountReconciliationService Service { get; }
    }

    private static ProductionRun CreateRun(DateTimeOffset startedAt) => new()
    {
        RunId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Profile = "paper",
        Status = "running",
        StartedAtUtc = startedAt,
        ConfigHash = new string('a', 64),
        CodeVersion = new string('b', 40)
    };

    private sealed class InMemoryOrderEvents : IOrderEventRepository
    {
        public IReadOnlyList<OrderStateSnapshot> Current { get; set; } = [];
        public Dictionary<string, OrderStateSnapshot> Additional { get; } = new(StringComparer.Ordinal);

        public Task<OrderStateSnapshot?> GetCurrentAsync(string clientOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Current.SingleOrDefault(item => item.ClientOrderId == clientOrderId) ??
                Additional.GetValueOrDefault(clientOrderId));

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<OrderTransitionResult> TransitionAsync(OrderTransitionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryPositions : IPositionLedgerRepository
    {
        public Dictionary<string, PositionLedgerSnapshot> Current { get; } = new(StringComparer.Ordinal);

        public Task<decimal> GetAccountedFillQuantityAsync(string brokerOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<PositionLedgerSnapshot?> GetCurrentAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<PositionLedgerSnapshot?>(Current.GetValueOrDefault(symbol));

        public Task<PositionLedgerSnapshot> AppendFillAsync(PositionFillAppendRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PositionLedgerSnapshot>>(Current.Values.ToArray());
    }

    private sealed class InMemoryReconciliations : IReconciliationRepository
    {
        private ReconciliationRecord? outstanding;
        private ReconciliationRecord? latest;
        public ReconciliationRecord? LastAcknowledged { get; private set; }

        public Task<ReconciliationRecord> RecordAsync(ReconciliationWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (request.RequiresAcknowledgement && outstanding?.DiffHash == request.DiffHash)
            {
                return Task.FromResult(outstanding);
            }

            if (request.RequiresAcknowledgement &&
                outstanding is null &&
                latest?.Status == "acknowledged" &&
                latest.DiffHash == request.DiffHash)
            {
                return Task.FromResult(latest);
            }

            var record = new ReconciliationRecord
            {
                ReconciliationId = request.ReconciliationId,
                Status = request.Status,
                DiffHash = request.DiffHash,
                RequiresAcknowledgement = request.RequiresAcknowledgement,
                BrokerSnapshotJson = request.BrokerSnapshotJson,
                LocalSnapshotJson = request.LocalSnapshotJson,
                DiffJson = request.DiffJson
            };
            if (request.RequiresAcknowledgement)
            {
                outstanding = record;
            }

            latest = record;

            return Task.FromResult(record);
        }

        public Task<ReconciliationRecord?> GetOutstandingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(outstanding);

        public Task<ReconciliationRecord> AcknowledgeAsync(ReconciliationAcknowledgement acknowledgement, CancellationToken cancellationToken = default)
        {
            if (outstanding?.ReconciliationId != acknowledgement.ReconciliationId)
            {
                throw new InvalidOperationException("Unknown reconciliation.");
            }

            outstanding.RequiresAcknowledgement = false;
            outstanding.Status = "acknowledged";
            outstanding.AcknowledgedAtUtc = acknowledgement.AcknowledgedAtUtc;
            outstanding.AcknowledgedBy = acknowledgement.Actor;
            outstanding.AcknowledgementReason = acknowledgement.Reason;
            LastAcknowledged = outstanding;
            latest = outstanding;
            outstanding = null;
            return Task.FromResult(LastAcknowledged);
        }
    }

    private sealed class RecordingBroker : IBrokerClient
    {
        public IReadOnlyList<BrokerPosition> Positions { get; set; } = [];
        public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) => Task.FromResult(Positions);
        public Task<IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ActiveBrokerOrder>>([]);
        public Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) => Task.FromResult<ActiveBrokerOrder?>(null);
        public Task<BrokerOrderReceipt> SubmitOrderAsync(FinalizedOrder order, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(ProtectiveStopOrder order, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingProtector : IProtectiveOrderInvariantService
    {
        public List<string> Symbols { get; } = [];

        public Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
            IBrokerClient broker,
            IReadOnlyList<BrokerPosition> brokerPositions,
            IReadOnlyList<ActiveBrokerOrder> openOrders,
            CancellationToken cancellationToken = default)
        {
            Symbols.AddRange(brokerPositions.Select(position => position.Ticker));
            return Task.FromResult<IReadOnlyList<ProtectiveOrderRepair>>([]);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
