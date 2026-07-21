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
        var broker = new Mock<IBrokerClient>(MockBehavior.Strict);
        FinalizedOrder? brokerOrder = null;
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
            .Callback<FinalizedOrder, CancellationToken>((order, _) =>
            {
                Assert.True(repository.ReservationCompleted);
                brokerOrder = order;
            })
            .ReturnsAsync("broker-order-1");
        var service = new OrderSubmissionService(
            repository,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        var result = await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal("broker-order-1", result.BrokerOrderId);
        Assert.Equal(repository.Intent!.ClientOrderId, result.ClientOrderId);
        Assert.Equal(result.ClientOrderId, brokerOrder!.ClientOrderId);
        broker.VerifyAll();
    }

    [Fact]
    public async Task SubmitBracketOrderAsync_Retry_ReusesPersistedClientOrderId()
    {
        var repository = new RecordingIntentRepository();
        var submittedClientIds = new List<string>();
        var broker = new Mock<IBrokerClient>();
        broker
            .Setup(client => client.SubmitOrderAsync(It.IsAny<FinalizedOrder>(), It.IsAny<CancellationToken>()))
            .Callback<FinalizedOrder, CancellationToken>((order, _) => submittedClientIds.Add(order.ClientOrderId))
            .ReturnsAsync("broker-order-1");
        var service = new OrderSubmissionService(
            repository,
            NullLogger<OrderSubmissionService>.Instance);
        var submission = CreateSubmission(Guid.NewGuid());

        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);
        await service.SubmitBracketOrderAsync(submission, broker.Object, CancellationToken.None);

        Assert.Equal(2, repository.ReservationAttempts);
        Assert.Equal(2, submittedClientIds.Count);
        Assert.Single(submittedClientIds.Distinct(StringComparer.Ordinal));
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

        public Task AppendAsync(OrderIntentRecord intent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
}
