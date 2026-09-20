using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class OrderSubmissionServiceTests
{
    [Fact]
    public void OperatorOverrideAuthorization_StrategyGatedPolicyCannotIssue()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OperatorOverrideAuthorization.Issue(
                new ManualEntryOptions(ManualEntryPolicy.StrategyGated),
                "test-operator",
                "explicit test override",
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_OperatorOverrideCannotUseLiveProfile()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = CreateService(repository);
        var now = DateTimeOffset.UtcNow;
        var submission = CreateSubmission(Guid.NewGuid()) with
        {
            RunContext = CreateSubmission(Guid.NewGuid()).RunContext with { Profile = "live" },
            StrategyIdentity = null,
            StrategySelectionMode = null,
            OperatorOverride = OperatorOverrideAuthorization.Issue(
                new ManualEntryOptions(ManualEntryPolicy.OperatorDirect),
                "test-operator",
                "explicit test override",
                now)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Contains("only in the paper profile", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_OperatorOverridePersistsExplicitExitPolicyIdentity()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.SubmitOrderAsync(
                It.IsAny<BrokerEntryOrder>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt("operator-order-1", DateTimeOffset.UtcNow));
        var now = DateTimeOffset.UtcNow;
        var submission = CreateSubmission(Guid.NewGuid()) with
        {
            StrategyIdentity = null,
            StrategySelectionMode = null,
            OperatorOverride = OperatorOverrideAuthorization.Issue(
                new ManualEntryOptions(ManualEntryPolicy.OperatorDirect),
                "test-operator",
                "explicit test override",
                now),
            ExitPolicyIdentity = TestStrategyIdentity
        };

        await CreateService(repository).SubmitEntryOrderAsync(
            submission,
            broker.Object,
            CancellationToken.None);

        Assert.NotNull(repository.Intent);
        using var request = JsonDocument.Parse(repository.Intent!.RequestJson);
        var exitPolicy = request.RootElement.GetProperty("exitPolicyIdentity");
        Assert.Equal(TestStrategyIdentity.StrategyId, exitPolicy.GetProperty("StrategyId").GetString());
        Assert.Equal(TestStrategyIdentity.SemanticVersion, exitPolicy.GetProperty("SemanticVersion").GetString());
        Assert.Equal(TestStrategyIdentity.ContentSha256, exitPolicy.GetProperty("ContentSha256").GetString());
        broker.VerifyAll();
    }

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
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var context = ExecutionRunContextFactory.Create(
            Guid.NewGuid(), "paper", new { test = true }, DateTimeOffset.UtcNow, typeof(OrderSubmissionServiceTests).Assembly);

        var result = await service.SubmitProtectiveStopAsync(
            new ProtectiveStopSubmission(
                Guid.NewGuid(), context, "paper-account", "MSFT", "sell", 10m, 98m,
                new DateOnly(2026, 7, 21), DateTimeOffset.UtcNow, "ledger:1:entry-1", 0),
            broker.Object,
            CancellationToken.None);

        Assert.Equal("backstop-1", result.BrokerOrderId);
        Assert.Equal("BACKSTOP", repository.Intent!.StrategyId);
        Assert.Equal(OrderState.Acked, events.State);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_PersistsIntentBeforeBrokerNetworkCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
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
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        var result = await service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal("broker-order-1", result.BrokerOrderId);
        Assert.Equal(repository.Intent!.ClientOrderId, result.ClientOrderId);
        Assert.Equal(result.ClientOrderId, brokerOrder!.Order.ClientOrderId);
        Assert.False(brokerOrder.SubmitOutsideRegularHours);
        Assert.Equal(brokerAcceptedAt, result.BrokerAcceptedAtUtc);
        Assert.Equal(OrderState.Acked, events.State);
        Assert.Equal([OrderState.Submitted, OrderState.Acked], events.AppliedStates);
        Assert.NotNull(repository.Reservation);
        Assert.Equal(submission.Candidate.CandidateId, repository.Reservation!.CandidateId);
        Assert.Equal(submission.Candidate.CandidateVersion, repository.Reservation.CandidateExpectedVersion);
        Assert.Equal(
            submission.Candidate.SemanticDecisionSha256,
            repository.Reservation.CandidateSemanticDecisionSha256);
        Assert.Equal(OrderIntentKind.StrategyEntry, repository.Reservation.Kind);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_PositionConflict_DoesNotReserveOrCallBroker()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            new RecordingEventRepository(repository),
            new RejectingEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            service.SubmitEntryOrderAsync(
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
    public async Task SubmitEntryOrderAsync_AcknowledgedRetry_SuppressesDuplicateBrokerCall()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var gates = new PassThroughEntryGateChain();
        var submittedClientIds = new List<string>();
        var broker = new Mock<IBrokerClient>();
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerEntryOrder, CancellationToken>((order, _) => submittedClientIds.Add(order.Order.ClientOrderId))
            .ReturnsAsync(new BrokerOrderReceipt("broker-order-1", DateTimeOffset.UtcNow));
        var service = new OrderSubmissionService(
            repository,
            events,
            gates,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None);
        await service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal(1, repository.ReservationAttempts);
        Assert.Equal(1, gates.Calls);
        Assert.Single(submittedClientIds);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitProtectiveStopAsync_UncertainRetry_ReusesIntentAndDoesNotCallBrokerTwice()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Broker response was not received."));
        var service = new OrderSubmissionService(
            repository,
            events,
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var context = ExecutionRunContextFactory.Create(
            Guid.NewGuid(), "paper", new { test = true }, DateTimeOffset.UtcNow, typeof(OrderSubmissionServiceTests).Assembly);
        var submission = new ProtectiveStopSubmission(
            Guid.NewGuid(),
            context,
            "paper-account",
            "MSFT",
            "sell",
            10m,
            98m,
            new DateOnly(2026, 7, 21),
            DateTimeOffset.UtcNow,
            "ledger:1:entry-1",
            0);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.SubmitProtectiveStopAsync(submission, broker.Object, CancellationToken.None));
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitProtectiveStopAsync(submission, broker.Object, CancellationToken.None));

        Assert.Contains("must be reconciled", retry.Message, StringComparison.Ordinal);
        broker.Verify(
            client => client.SubmitProtectiveStopAsync(It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_BrokerFailure_LeavesUncertainSubmissionAndBlocksRetry()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Broker response was not received."));
        var service = new OrderSubmissionService(
            repository,
            events,
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await Assert.ThrowsAsync<TimeoutException>(
            () => service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None));
        var retryError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Equal(OrderState.Submitted, events.State);
        Assert.Contains("must be reconciled", retryError.Message, StringComparison.Ordinal);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_ExistingIntentFromCompletedRun_DoesNotCallBroker()
    {
        var repository = new RecordingIntentRepository { OwningRunStatus = "completed" };
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            events,
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitEntryOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("run state 'completed'", error.Message, StringComparison.Ordinal);
        Assert.NotNull(repository.Intent);
        Assert.Null(events.State);
        broker.Verify(
            client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitProtectiveStopAsync_ExistingIntentFromCompletedRun_RestoresProtection()
    {
        var repository = new RecordingIntentRepository { OwningRunStatus = "completed" };
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt(
                "protective-1",
                new DateTimeOffset(2026, 7, 21, 14, 35, 1, TimeSpan.Zero)));
        var service = new OrderSubmissionService(
            repository,
            events,
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

        var result = await service.SubmitProtectiveStopAsync(
                new ProtectiveStopSubmission(
                    Guid.NewGuid(),
                    CreateSubmission(Guid.NewGuid()).RunContext,
                    "paper-account",
                    "MSFT",
                    "sell",
                    10m,
                    98m,
                    new DateOnly(2026, 7, 21),
                    new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
                    "ledger:1:entry-1",
                    0),
                broker.Object,
                CancellationToken.None);

        Assert.Equal("protective-1", result.BrokerOrderId);
        Assert.NotNull(repository.Intent);
        broker.Verify(
            client => client.SubmitProtectiveStopAsync(It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitProtectiveStopAsync_DurableUnsentIntent_IsDispatchedByCommonDispatcher()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var context = CreateSubmission(Guid.NewGuid()).RunContext;
        var intentId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero);
        var submission = new ProtectiveStopSubmission(
            intentId,
            context,
            "paper-account",
            "MSFT",
            "sell",
            10m,
            98m,
            new DateOnly(2026, 7, 21),
            createdAt,
            "ledger:1:entry-1",
            0);
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            strategyId = "BACKSTOP",
            symbol = "MSFT",
            side = "sell",
            orderType = "stop",
            timeInForce = "gtc",
            quantity = 10m,
            stopPrice = 98m,
            submission.PositionGenerationIdentity,
            submission.ProtectionRevision,
            submission.SessionDate
        });
        await repository.ReserveAsync(
            new ProductionRun
            {
                RunId = context.RunId,
                Profile = context.Profile,
                Status = "running",
                StartedAtUtc = context.StartedAtUtc,
                ConfigHash = context.ConfigHash,
                CodeVersion = context.CodeVersion
            },
            new OrderIntentReservation(
                intentId,
                OrderIntentKind.ProtectiveStop,
                null,
                "paper-account",
                "BACKSTOP",
                "MSFT",
                "sell",
                "stop",
                "gtc",
                10m,
                null,
                98m,
                submission.SessionDate,
                createdAt,
                requestJson));
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        broker.Setup(client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrokerOrderReceipt("recovered-backstop", createdAt.AddSeconds(1)));
        var service = new OrderSubmissionService(
            repository,
            events,
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

        var result = await service.SubmitProtectiveStopAsync(
            submission,
            broker.Object,
            CancellationToken.None);

        Assert.Equal("recovered-backstop", result.BrokerOrderId);
        Assert.Equal(OrderState.Acked, events.State);
        broker.Verify(
            client => client.SubmitProtectiveStopAsync(
                It.IsAny<ProtectiveStopOrder>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_EntryAdmissionBlocked_DoesNotPersistOrCallBroker()
    {
        var repository = new RecordingIntentRepository();
        var events = new RecordingEventRepository(repository);
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            events,
            new RejectingEntryGateChain(
                new EntryGateRejectedException(
                    EntryGateSlot.SystemState,
                    RejectCode.REJECT_DEGRADED_DATA,
                    "stream down")),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            service.SubmitEntryOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("stream down", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_RegularSession_DoesNotMarkOrderAsExtendedWhenPermissionIsEnabled()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        BrokerEntryOrder? submitted = null;
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<BrokerEntryOrder>(), It.IsAny<CancellationToken>()))
            .Callback<BrokerEntryOrder, CancellationToken>((order, _) => submitted = order)
            .ReturnsAsync(new BrokerOrderReceipt("regular-order", DateTimeOffset.UtcNow));
        var service = CreateService(repository);

        var result = await service.SubmitEntryOrderAsync(
            CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true },
            broker.Object,
            CancellationToken.None);

        Assert.NotNull(submitted);
        Assert.False(submitted.SubmitOutsideRegularHours);
        Assert.False(result.SubmittedOutsideRegularHours);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_PremarketWithoutPermission_FailsBeforeIntentReservation()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            new RecordingEventRepository(repository),
            new RejectingEntryGateChain(new InvalidOperationException(
                "Equity entry rejected during premarket because allow_extended_hours_trading is false.")),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitEntryOrderAsync(
                CreateSubmission(Guid.NewGuid()),
                broker.Object,
                CancellationToken.None));

        Assert.Contains("allow_extended_hours_trading is false", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_OvernightEntry_FailsBeforeIntentReservationWhenProtectionIsUnavailable()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            new RecordingEventRepository(repository),
            new RejectingEntryGateChain(new InvalidOperationException(
                    "Extended-hours order contract cannot attach a broker-resting protective stop.")),
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitEntryOrderAsync(submission, broker.Object, CancellationToken.None));

        Assert.Contains("cannot attach a broker-resting protective stop", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.ReservationAttempts);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitEntryOrderAsync_AfterHoursEnabled_DoesNotSubmitUnprotectedEntry()
    {
        var repository = new RecordingIntentRepository();
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        var service = new OrderSubmissionService(
            repository,
            new RecordingEventRepository(repository),
            new RejectingEntryGateChain(new InvalidOperationException(
                    "Extended-hours order contract cannot attach a broker-resting protective stop.")),
            NullLogger<OrderSubmissionService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitEntryOrderAsync(
                CreateSubmission(Guid.NewGuid()) with { AllowExtendedHoursTrading = true },
                broker.Object,
                CancellationToken.None));

        Assert.Contains("cannot attach a broker-resting protective stop", error.Message, StringComparison.Ordinal);
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

    private static EntryOrderSubmission CreateSubmission(Guid intentId)
    {
        var runContext = new ExecutionRunContext(
            Guid.NewGuid(),
            "paper",
            new string('a', 64),
            new string('b', 40),
            new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero));
        return new EntryOrderSubmission(
            intentId,
            new ValidatedEntryCandidate(
                Guid.NewGuid(),
                "test",
                "swing",
                new DateTimeOffset(2026, 7, 21, 14, 34, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
                "{}",
                CandidateVersion: 4,
                SemanticDecisionSha256: new string('d', 64)),
            runContext,
            "SWGA",
            "buy",
            "limit",
            "day",
            new DateOnly(2026, 7, 21),
            new DateTimeOffset(2026, 7, 21, 14, 35, 0, TimeSpan.Zero),
            new FinalizedOrder("MSFT", "Swing A", 10, 100m, 98m, 104m, DateTimeOffset.UtcNow, String.Empty),
            StrategyIdentity: TestStrategyIdentity,
            StrategySelectionMode: StrategySelectionMode.RunPaperShadow);
    }

    private static OrderSubmissionService CreateService(RecordingIntentRepository repository) =>
        new(
            repository,
            new RecordingEventRepository(repository),
            new PassThroughEntryGateChain(),
            NullLogger<OrderSubmissionService>.Instance);

    private static readonly StrategyArtifactIdentity TestStrategyIdentity = new(
        "test.swing",
        "1.0.0",
        new string('c', 64));

    private sealed class RecordingIntentRepository : IOrderIntentRepository
    {
        public Task<ProductionRun?> GetRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProductionRun?>(null);

        public Task<IReadOnlyList<OrderIntentRecord>> ListByRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderIntentRecord>>(
                Intent?.RunId == runId ? [Intent] : []);

        public OrderIntentRecord? Intent { get; private set; }

        public OrderIntentReservation? Reservation { get; private set; }

        public bool ReservationCompleted { get; private set; }

        public int ReservationAttempts { get; private set; }

        public string OwningRunStatus { get; set; } = "running";

        public Task<OrderIntentRecord?> GetByIntentIdAsync(
            Guid intentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Intent?.IntentId == intentId ? Intent : null);

        public Task<OrderIntentRecord?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Intent?.ClientOrderId == clientOrderId ? Intent : null);

        public Task<PortfolioRiskReservationRecord?> GetRiskReservationByIntentIdAsync(
            Guid intentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PortfolioRiskReservationRecord?>(null);

        public Task<IReadOnlyList<ActiveOrderIntent>> ListActiveForSymbolAsync(
            string accountId,
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveOrderIntent>>([]);

        public Task<IReadOnlyList<ActiveOrderIntent>> ListActiveProtectiveForSymbolAsync(
            string accountId,
            string symbol,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveOrderIntent>>([]);

        public Task<bool> HasActivePositionExitAsync(
            string accountId,
            string symbol,
            long positionGenerationEventId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<OrderIntentRecord>> ListUnpreparedPositionExitsAsync(
            string accountId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OrderIntentRecord>>([]);

        public Task<bool> TryExpireUnattemptedUnleasedPositionExitAsync(
            Guid intentId,
            string reason,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<OrderIntentReservationResult> ReserveAsync(
            ProductionRun run,
            OrderIntentReservation reservation,
            CancellationToken cancellationToken = default)
        {
            ReservationAttempts++;
            Reservation = reservation;
            var created = Intent is null;
            Intent ??= new OrderIntentRecord
            {
                IntentId = reservation.IntentId,
                Kind = reservation.Kind,
                CandidateId = reservation.CandidateId,
                CandidateTriggeredVersion = reservation.CandidateExpectedVersion,
                CandidateConsumedVersion = reservation.CandidateExpectedVersion + 1,
                CandidateSemanticDecisionSha256 = reservation.CandidateSemanticDecisionSha256,
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
                AccountId = reservation.AccountId,
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
            return Task.FromResult(new OrderIntentReservationResult(Intent, created, OwningRunStatus));
        }
    }

    private sealed class PassThroughEntryGateChain : IEntryGateChain
    {
        public int Calls { get; private set; }

        public Task<T> ExecuteAsync<T>(
            EntryOrderSubmission submission,
            IBrokerClient brokerClient,
            Func<EntryGateApproval, CancellationToken, Task<T>> submit,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var notional = submission.Order.ShareQuantity * submission.Order.LimitPrice;
            return submit(
                new EntryGateApproval(
                    submission.SessionDate,
                    EquityTradingSession.Regular,
                    SubmitOutsideRegularHours: false,
                    submission.CreatedAtUtc.AddMinutes(5),
                    new PortfolioRiskReservationRequest(
                        "paper-account",
                        submission.Candidate.Horizon,
                        100_000m,
                        100_000m,
                        0m,
                        0m,
                        new HashSet<string>(StringComparer.Ordinal),
                        notional,
                        submission.Order.ShareQuantity *
                            Math.Abs(submission.Order.LimitPrice - submission.Order.StopLossPrice),
                        100_000m,
                        1_500m,
                        3,
                        submission.CreatedAtUtc,
                        submission.CreatedAtUtc)),
                cancellationToken);
        }
    }

    private sealed class RejectingEntryGateChain(Exception? exception = null) : IEntryGateChain
    {
        public Task<T> ExecuteAsync<T>(
            EntryOrderSubmission submission,
            IBrokerClient brokerClient,
            Func<EntryGateApproval, CancellationToken, Task<T>> submit,
            CancellationToken cancellationToken = default) =>
            throw exception ?? new EntryGateRejectedException(
                EntryGateSlot.PositionConflict,
                RejectCode.REJECT_SETUP_INVALID,
                $"Position for {submission.Order.Ticker} belongs to another strategy.");
    }

    private sealed class RecordingEventRepository(RecordingIntentRepository intents) :
        IOrderEventRepository,
        IOrderDispatchService
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
            string accountId,
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

        public Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
            BrokerOrderReplacementTransition request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<OrderSubmissionResult> DispatchAsync(
            Guid intentId,
            IBrokerClient broker,
            CancellationToken cancellationToken = default)
        {
            var intent = intents.Intent is { } candidate && candidate.IntentId == intentId
                ? candidate
                : throw new InvalidOperationException("Unknown test intent.");
            var state = await GetCurrentAsync(intent.ClientOrderId, cancellationToken)
                ?? throw new InvalidOperationException("Missing test lifecycle state.");
            if (state.State == OrderState.Acked)
            {
                return new OrderSubmissionResult(
                    state.BrokerOrderId!,
                    intent.ClientOrderId,
                    intent.IntentId,
                    state.BrokerTimestampUtc!.Value);
            }

            if (state.State == OrderState.Submitted)
            {
                throw new InvalidOperationException(
                    $"Order '{intent.ClientOrderId}' must be reconciled before retry.");
            }

            await TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    OrderState.Intent,
                    OrderState.Submitted,
                    "test_dispatcher",
                    DateTimeOffset.UtcNow,
                    PayloadJson: intent.RequestJson),
                cancellationToken);
            BrokerOrderReceipt receipt;
            if (intent.Kind == OrderIntentKind.ProtectiveStop)
            {
                receipt = await broker.SubmitProtectiveStopAsync(
                    new ProtectiveStopOrder(
                        intent.Symbol,
                        intent.Side,
                        intent.RequestedQuantity,
                        intent.StopPrice!.Value,
                        intent.TimeInForce,
                        intent.ClientOrderId),
                    cancellationToken);
            }
            else
            {
                using var request = JsonDocument.Parse(intent.RequestJson);
                var takeProfit = request.RootElement.GetProperty("takeProfitPrice").GetDecimal();
                receipt = await broker.SubmitOrderAsync(
                    new BrokerEntryOrder(
                        new FinalizedOrder(
                            intent.Symbol,
                            intent.StrategyId,
                            Decimal.ToInt32(intent.RequestedQuantity),
                            intent.LimitPrice!.Value,
                            intent.StopPrice!.Value,
                            takeProfit,
                            intent.CreatedAtUtc,
                            intent.ClientOrderId),
                        intent.Side,
                        intent.OrderType,
                        intent.TimeInForce,
                        false),
                    cancellationToken);
            }

            var acknowledged = await TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    OrderState.Submitted,
                    OrderState.Acked,
                    "broker_rest",
                    DateTimeOffset.UtcNow,
                    receipt.BrokerAcceptedAtUtc,
                    receipt.BrokerOrderId),
                cancellationToken);
            return new OrderSubmissionResult(
                receipt.BrokerOrderId,
                intent.ClientOrderId,
                intent.IntentId,
                acknowledged.Snapshot.BrokerTimestampUtc!.Value);
        }

        public Task<int> RecoverPendingAsync(
            IBrokerClient broker,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
