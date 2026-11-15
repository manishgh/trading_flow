using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Domain.Execution;
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
    public async Task ProtectionRepairFailure_IsReportedAndKeepsEntriesBlocked()
    {
        var fixture = new Fixture();
        fixture.Protector.Repairs =
        [
            new ProtectiveOrderRepair("MSFT", false, "broker rejected protective stop")
        ];

        var result = await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);

        Assert.False(result.BrokerProtectionReady);
        Assert.Equal(1, result.ProtectionRepairFailureCount);
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Contains(
            fixture.Admission.GetSnapshot().Blocks,
            block => block.Code == "BROKER_SIDE_PROTECTION_FAILED");
    }

    [Fact]
    public async Task ProtectionVerificationException_DoesNotPublishReadinessOrReopenEntries()
    {
        var fixture = new Fixture();
        await fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None);
        Assert.True(fixture.Admission.GetSnapshot().EntriesAllowed);
        var lastCompletedAt = fixture.Service.GetHealth().LastCompletedAtUtc;
        fixture.Protector.Failure = new IOException("protective-order provider unavailable");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.ReconcileAsync(fixture.Broker, [], CancellationToken.None));

        Assert.Equal("protective-order provider unavailable", error.Message);
        Assert.False(fixture.Admission.GetSnapshot().EntriesAllowed);
        Assert.Equal("protection_verification_failed", fixture.Service.GetHealth().Status);
        Assert.Equal(lastCompletedAt, fixture.Service.GetHealth().LastCompletedAtUtc);
        Assert.Contains(
            fixture.Admission.GetSnapshot().Blocks,
            block => block.Code == "BROKER_SIDE_PROTECTION_FAILED");
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
        Assert.Contains("MSFT", fixture.Protector.Symbols);
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
            "paper-account",
            "MSFT",
            -5m,
            "SWGA",
            fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow(),
            1,
            1,
            "SWGA-S-MSFT-20260721-001-12345678",
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
            "paper-account", "MSFT", 10m, "SWGA", fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), 1,
            1, parentClientId, parentClientId, 100m, "buy");
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

    // The stop drains owned opening orders while continuing the existing protection reconciliation.
    [Fact]
    public async Task AccountLossBreach_CancelsStandaloneRemaindersAndRecordsLinkedReview()
    {
        var fixture = new Fixture();
        var now = fixture.Time.GetUtcNow();
        var scope = new AccountLossScope("alpaca", "paper", "paper-account");
        var day = new DateOnly(2026, 7, 21);
        var prior = new AccountClosingEquity(day.AddDays(-1), now.AddDays(-1), 100_000m);
        var week = new AccountClosingEquity(day.AddDays(-4), now.AddDays(-4), 100_000m);
        fixture.Broker.AccountTime = fixture.Time;
        fixture.Broker.AccountEquity = 96_000m;
        fixture.Broker.LossEvidence = new AccountLossEvidence(day, prior, week, [week, prior], null);
        fixture.Broker.Positions = [new BrokerPosition("HELD", "long", 3m, 10m, 9m, -3m)];

        var store = new Mock<IAccountLossStateStore>(MockBehavior.Strict);
        store.Setup(candidate => candidate.GetAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountLossState?)null);
        store.Setup(candidate => candidate.RecordAsync(scope, It.IsAny<ProductionRun>(),
                It.IsAny<AccountLossSample>(), It.IsAny<AccountLossGuardOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountLossScope _, ProductionRun _, AccountLossSample sample, AccountLossGuardOptions options,
                CancellationToken _) => AccountLossLimitPolicy.Observe(null, sample, options.Limits));
        store.Setup(candidate => candidate.SetCancellationReviewAsync(
                scope, It.IsAny<ProductionRun>(),
                It.IsAny<AccountLossCancellationReview>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        store.Setup(candidate => candidate.CompleteCancellationSweepAsync(scope, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var guard = new AccountLossGuard(store.Object, new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1)),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions("paper-account")), fixture.Time,
            NullLogger<AccountLossGuard>.Instance);
        var intents = new Mock<IOrderIntentRepository>();
        intents.Setup(repository => repository.GetByClientOrderIdAsync("new-entry", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "new-entry", AccountId = "paper-account", Kind = OrderIntentKind.StrategyEntry });
        intents.Setup(repository => repository.GetByClientOrderIdAsync("partial-entry", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "partial-entry", AccountId = "paper-account", Kind = OrderIntentKind.StrategyEntry });
        intents.Setup(repository => repository.GetByClientOrderIdAsync("linked-entry", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "linked-entry", AccountId = "paper-account", Symbol = "MSFT", Kind = OrderIntentKind.StrategyEntry });
        intents.Setup(repository => repository.GetByClientOrderIdAsync("exit", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderIntentRecord { ClientOrderId = "exit", AccountId = "paper-account", Kind = OrderIntentKind.PositionExit });
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(command => command.RequestCancelAsync(
                It.Is<OrderCancellationSubmission>(request =>
                    (request.ClientOrderId == "new-entry" || request.ClientOrderId == "partial-entry") &&
                    request.RequireStandaloneOpeningOrder),
                fixture.Broker, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderCancellationSubmission request, IBrokerClient _, CancellationToken _) =>
                new OrderStateSnapshot(Guid.NewGuid(), request.ClientOrderId, request.BrokerOrderId,
                    request.ClientOrderId == "partial-entry" ? OrderState.CancelPending : OrderState.Canceled, now, now, 0m, null, 1));

        var service = new AccountReconciliationService(
            fixture.Orders, fixture.Positions, fixture.Reconciliations, fixture.Admission, fixture.Protector,
            new ReconciliationRunContext(CreateRun(now)), new AccountReconciliationOptions(60, 30),
            fixture.Time, NullLogger<AccountReconciliationService>.Instance,
            orderIntents: intents.Object, lossGuard: guard, orderCommands: commands.Object);
        var opening = new ActiveBrokerOrder("new-order", "MSFT", "buy", "new", "limit", 100m, null, 10m,
            now, "new-entry", 0m, null, now, OrderClass: "simple");
        var partial = opening with { OrderId = "partial-order", ClientOrderId = "partial-entry", FilledQuantity = 1m };
        var exit = opening with { OrderId = "exit-order", ClientOrderId = "exit", Side = "sell" };
        var linked = partial with { OrderId = "linked-order", ClientOrderId = "linked-entry", OrderClass = "bracket" };
        var protective = opening with { OrderId = "stop-order", ClientOrderId = "stop", Side = "sell", ParentClientOrderId = "linked-entry" };

        fixture.Broker.FreshOpenOrders = [opening, partial, linked, exit, protective];
        var result = await service.ReconcileAsync(fixture.Broker, [], default);

        Assert.True(result.BrokerProtectionReady);
        Assert.Contains("HELD", fixture.Protector.Symbols);
        commands.Verify(command => command.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "new-entry"),
            fixture.Broker, It.IsAny<CancellationToken>()), Times.Once);
        commands.Verify(command => command.RequestCancelAsync(
            It.Is<OrderCancellationSubmission>(request => request.ClientOrderId == "partial-entry"),
            fixture.Broker, It.IsAny<CancellationToken>()), Times.Once);
        commands.VerifyNoOtherCalls();
        store.Verify(candidate => candidate.CompleteCancellationSweepAsync(scope, 1, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(candidate => candidate.SetCancellationReviewAsync(
            scope, It.IsAny<ProductionRun>(), It.Is<AccountLossCancellationReview>(review => review.ClientOrderId == "linked-entry"),
            true, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(candidate => candidate.SetCancellationReviewAsync(
            scope, It.IsAny<ProductionRun>(), It.Is<AccountLossCancellationReview>(review => review.ClientOrderId == "new-entry"),
            false, It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(candidate => candidate.SetCancellationReviewAsync(
            scope, It.IsAny<ProductionRun>(), It.Is<AccountLossCancellationReview>(review => review.ClientOrderId == "partial-entry"),
            false, It.IsAny<CancellationToken>()), Times.Never);
    }

    // A stored review survives terminal-order detection until the broker and durable position agree and protection is verified.
    [Theory]
    [InlineData("verified_stop", true)]
    [InlineData("flat_after_accounted_close", true)]
    [InlineData("stale_flat_projection", false)]
    [InlineData("failed_repair", false)]
    [InlineData("unverified_stop", false)]
    [InlineData("later_fill", false)]
    [InlineData("terminal_below_observed_fill", false)]
    [InlineData("replacement", false)]
    public async Task CancellationReview_RequiresStableTerminalFillAndVerifiedExposure(string evidence, bool resolves)
    {
        var fixture = new Fixture();
        var now = fixture.Time.GetUtcNow();
        const string clientId = "linked-entry";
        const string brokerId = "linked-order";
        const string symbol = "MSFT";
        var review = new AccountLossCancellationReview(clientId, brokerId, symbol,
            evidence == "terminal_below_observed_fill" ? 4m : 2m, now);
        var lossState = new AccountLossState(now, null, null, null, null, null, CancellationReviews: [review]);
        var terminal = new ActiveBrokerOrder(brokerId, symbol, "buy", "canceled", "limit", 100m, null, 10m,
            now, clientId, 3m, 100m, now.AddSeconds(1), OrderClass: "bracket");
        var quantity = evidence == "flat_after_accounted_close" ? 0m : terminal.FilledQuantity;
        fixture.Positions.Current[symbol] = new PositionLedgerSnapshot(
            "paper-account", symbol, quantity, "strategy", now, now, 2, 1, clientId, clientId, 100m, "buy");
        fixture.Orders.Additional[clientId] = new OrderStateSnapshot(Guid.NewGuid(), clientId, brokerId,
            OrderState.Canceled, now, terminal.UpdatedAt, terminal.FilledQuantity, terminal.FilledAveragePrice, 1);
        fixture.Broker.AccountTime = fixture.Time;
        fixture.Broker.Positions = quantity == 0m || evidence == "stale_flat_projection"
            ? []
            : [new BrokerPosition(symbol, "long", quantity, 100m, 101m, 3m)];
        fixture.Broker.LookupOrder = evidence switch
        {
            "later_fill" => terminal with { FilledQuantity = 4m, UpdatedAt = now.AddSeconds(2) },
            "replacement" => terminal with { ReplacedByOrderId = "new-broker-id" },
            _ => terminal
        };
        fixture.Protector.VerifiedCoverage = evidence != "unverified_stop";
        fixture.Protector.Repairs = evidence == "failed_repair"
            ? [new ProtectiveOrderRepair(symbol, false, "Stop order was rejected")]
            : [];

        var store = new Mock<IAccountLossStateStore>();
        store.Setup(service => service.RecordAsync(It.IsAny<AccountLossScope>(), It.IsAny<ProductionRun>(),
                It.IsAny<AccountLossSample>(), It.IsAny<AccountLossGuardOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lossState);
        var guard = new AccountLossGuard(store.Object,
            new AccountLossGuardOptions("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1)),
            new BrokerAccountBindingService(new BrokerAccountBindingOptions("paper-account")), fixture.Time,
            NullLogger<AccountLossGuard>.Instance);
        var commands = new Mock<IOrderCommandService>();
        commands.Setup(service => service.ReconcileTerminalOpeningOrderAsync(
                "paper-account", clientId, brokerId, fixture.Broker, It.IsAny<CancellationToken>()))
            .ReturnsAsync(terminal);
        var service = new AccountReconciliationService(fixture.Orders, fixture.Positions, fixture.Reconciliations,
            fixture.Admission, fixture.Protector, new ReconciliationRunContext(CreateRun(now)),
            new AccountReconciliationOptions(60, 30), fixture.Time, NullLogger<AccountReconciliationService>.Instance,
            lossGuard: guard, orderCommands: commands.Object);

        await service.ReconcileAsync(fixture.Broker, []);

        store.Verify(candidate => candidate.SetCancellationReviewAsync(
            new AccountLossScope("alpaca", "paper", "paper-account"), It.IsAny<ProductionRun>(), review,
            false, It.IsAny<CancellationToken>()), resolves ? Times.Once() : Times.Never());
        Assert.Equal(resolves,
            await service.IsOpeningOrderResolvedAsync(fixture.Broker, clientId, brokerId, symbol, review.FilledQuantity));
        commands.Verify(candidate => candidate.RequestCancelAsync(
            It.IsAny<OrderCancellationSubmission>(), It.IsAny<IBrokerClient>(), It.IsAny<CancellationToken>()), Times.Never);
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

        public Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<OrderTransitionResult> TransitionAsync(OrderTransitionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
            BrokerOrderReplacementTransition request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryPositions : IPositionLedgerRepository
    {
        public Dictionary<string, PositionLedgerSnapshot> Current { get; } = new(StringComparer.Ordinal);

        public Task<decimal> GetAccountedFillQuantityAsync(string accountId, string brokerOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0m);

        public Task<PositionLedgerSnapshot?> GetCurrentAsync(string accountId, string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<PositionLedgerSnapshot?>(Current.GetValueOrDefault(symbol));

        public Task<PositionLedgerSnapshot> AppendFillAsync(PositionFillAppendRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PositionLedgerSnapshot>>(Current.Values.ToArray());

        public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentForRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunOwnedPositionLedgerSnapshot>>([]);

        public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentOwnersForSymbolAsync(
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunOwnedPositionLedgerSnapshot>>([]);
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

    private sealed class RecordingBroker : IBrokerClient, IBrokerAccountLossEvidenceProvider
    {
        public IReadOnlyList<BrokerPosition> Positions { get; set; } = [];
        public IReadOnlyList<ActiveBrokerOrder> FreshOpenOrders { get; set; } = [];
        public TimeProvider AccountTime { get; set; } = TimeProvider.System;
        public decimal AccountEquity { get; set; } = 100_000m;
        public AccountLossEvidence? LossEvidence { get; set; }
        public ActiveBrokerOrder? LookupOrder { get; set; }

        // Supply coherent marked equity on the same test clock as loss supervision.
        public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken)
        {
            var now = AccountTime.GetUtcNow();
            return Task.FromResult(new BrokerAccountSnapshot(
                "paper-account", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, AccountEquity, 0m, 0m, now, now));
        }

        // Use only the explicitly seeded closing marks in account-loss cases.
        public Task<AccountLossEvidence> GetAccountLossEvidenceAsync(
            BrokerAccountSnapshot account, DateOnly? lastTrustedSession, CancellationToken cancellationToken) =>
            Task.FromResult(LossEvidence ?? throw new InvalidOperationException("No account-loss fixture supplied."));
        public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) => Task.FromResult(Positions);
        // Simulate a fresh post-breach read independently from the earlier reconciliation snapshot.
        public Task<IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FreshOpenOrders);
        // Return the final terminal parent separately from the earlier command evidence.
        public Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(LookupOrder?.ClientOrderId == clientOrderId ? LookupOrder : null);
        public Task<BrokerOrderReceipt> SubmitOrderAsync(BrokerEntryOrder order, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrokerOrderReceipt> SubmitExitOrderAsync(BrokerExitOrder order, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TradingSessionSnapshot> GetSessionAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new TradingSessionSnapshot(
                DateOnly.FromDateTime(timestampUtc.UtcDateTime),
                EquityTradingSession.Regular,
                timestampUtc,
                null,
                null));
        public Task<AssetTradingEligibility?> GetEligibilityAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<AssetTradingEligibility?>(new AssetTradingEligibility(
                symbol,
                Active: true,
                Tradable: true,
                OvernightTradable: true,
                DateTimeOffset.UtcNow));
        public Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(ProtectiveStopOrder order, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ActiveBrokerOrder> ReplaceProtectiveOrderAsync(BrokerProtectiveOrderReplacement replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingProtector : IProtectiveOrderInvariantService
    {
        public List<string> Symbols { get; } = [];
        public IReadOnlyList<ProtectiveOrderRepair> Repairs { get; set; } = [];
        public Exception? Failure { get; set; }
        public bool VerifiedCoverage { get; set; }

        // Keep the positive resting-coverage check independent of the result of a repair attempt.
        public Task<bool> HasVerifiedCoverageAsync(
            IBrokerClient broker, string accountId, BrokerPosition position,
            IReadOnlyList<ActiveBrokerOrder> openOrders, CancellationToken cancellationToken = default) =>
            Task.FromResult(VerifiedCoverage);

        public Task<IReadOnlyList<ProtectiveOrderRepair>> EnsureAsync(
            IBrokerClient broker,
            string accountId,
            IReadOnlyList<BrokerPosition> brokerPositions,
            IReadOnlyList<ActiveBrokerOrder> openOrders,
            CancellationToken cancellationToken = default)
        {
            Symbols.AddRange(brokerPositions.Select(position => position.Ticker));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Repairs);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
