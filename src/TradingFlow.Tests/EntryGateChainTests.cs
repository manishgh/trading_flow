using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class EntryGateChainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_AllEvidencePasses_PersistsTwelveOrderedGatesBeforeSubmission()
    {
        var fixture = new Fixture();
        var submitted = false;

        var result = await fixture.Chain.ExecuteAsync(
            fixture.Submission,
            fixture.Broker,
            _ =>
            {
                Assert.Equal(12, fixture.Evaluations.Requests.Count);
                submitted = true;
                return Task.FromResult("submitted");
            });

        Assert.True(submitted);
        Assert.Equal("submitted", result);
        Assert.Equal(Enumerable.Range(1, 12), fixture.Evaluations.Requests.Select(item => item.GateOrder));
        Assert.All(fixture.Evaluations.Requests, item => Assert.True(item.Passed));
        Assert.Equal(1, fixture.Broker.AccountCalls);
        Assert.Equal(1, fixture.Broker.PositionCalls);
        Assert.Equal(1, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_HaltedSecurity_StopsAtFourthGateEvenWithFreshTrade()
    {
        var fixture = new Fixture(SecurityTradingState.Halted);

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                _ => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.HaltOrLuld, exception.Slot);
        Assert.Equal(4, fixture.Evaluations.Requests.Count);
        Assert.False(fixture.Evaluations.Requests[^1].Passed);
        Assert.Equal(0, fixture.Broker.AccountCalls);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_StaleQuote_StopsBeforeSpreadAndAccountCalls()
    {
        var fixture = new Fixture();
        fixture.Broker.MarketObservation = fixture.Broker.MarketObservation with
        {
            QuoteTimestampUtc = Now.AddSeconds(-3)
        };

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                _ => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.QuoteAge, exception.Slot);
        Assert.Equal(5, fixture.Evaluations.Requests.Count);
        Assert.Equal(0, fixture.Broker.AccountCalls);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    private sealed class Fixture
    {
        public Fixture(SecurityTradingState tradingState = SecurityTradingState.TradingObserved)
        {
            var candidateId = Guid.NewGuid();
            var runContext = new ExecutionRunContext(
                Guid.NewGuid(),
                "paper",
                new string('a', 64),
                new string('b', 40),
                Now.AddMinutes(-1));
            Submission = new BracketOrderSubmission(
                Guid.NewGuid(),
                new ValidatedEntryCandidate(
                    candidateId,
                    "strategy_signal",
                    "intraday",
                    Now.AddMinutes(-1),
                    Now.AddSeconds(-1),
                    "{}"),
                runContext,
                "INTRADAY-V1",
                "buy",
                "limit",
                "day",
                new DateOnly(2026, 7, 22),
                Now,
                new FinalizedOrder(
                    "MSFT", "test", 10, 100m, 98m, 104m, Now, String.Empty));
            Candidates = new MemoryCandidateRepository(new CandidateRecord
            {
                CandidateId = candidateId,
                Symbol = "MSFT",
                DiscoveredAtUtc = Now.AddMinutes(-1),
                RevalidatedAtUtc = Now.AddSeconds(-1),
                DiscoverySource = "strategy_signal",
                Horizon = "intraday",
                SelectedStrategy = "INTRADAY-V1",
                State = "SETUP_VALID"
            });
            Evaluations = new RecordingGateEvaluationRepository();
            Broker = new TestBroker(Now);
            Chain = new EntryGateChain(
                new EntryAdmissionControl(),
                new PassThroughPositionConflictGuard(),
                Candidates,
                Evaluations,
                new FixedTradingStatusProvider(tradingState, Now),
                new EntryGateOptions(20, 2_000, 20m, 15m, 15m, 100m, 75m, 1, 3),
                new FixedTimeProvider(Now));
        }

        public BracketOrderSubmission Submission { get; }
        public MemoryCandidateRepository Candidates { get; }
        public RecordingGateEvaluationRepository Evaluations { get; }
        public TestBroker Broker { get; }
        public EntryGateChain Chain { get; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedTradingStatusProvider(
        SecurityTradingState state,
        DateTimeOffset now) : ISecurityTradingStatusProvider
    {
        public Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public SecurityTradingStatus GetStatus(string symbol) =>
            new(symbol, state, state == SecurityTradingState.Halted ? "H" : "T", null, now, now);
    }

    private sealed class MemoryCandidateRepository(CandidateRecord candidate) : ICandidateRepository
    {
        public Task<CandidateRecord> UpsertValidatedAsync(
            ProductionRun run,
            CandidateRecord value,
            CancellationToken cancellationToken = default) => Task.FromResult(value);

        public Task<CandidateRecord?> GetAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate.CandidateId == candidateId ? candidate : null);
    }

    private sealed class RecordingGateEvaluationRepository : IGateEvaluationRepository
    {
        public IReadOnlyList<GateEvaluationAppendRequest> Requests { get; private set; } = [];

        public Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
            ProductionRun run,
            IReadOnlyList<GateEvaluationAppendRequest> evaluations,
            CancellationToken cancellationToken = default)
        {
            Requests = evaluations.ToArray();
            return Task.FromResult<IReadOnlyList<GateEvaluationRecord>>([]);
        }
    }

    private sealed class PassThroughPositionConflictGuard : IPositionConflictGuard
    {
        public Task<T> ExecuteEntryAsync<T>(
            string symbol,
            string strategyId,
            Func<CancellationToken, Task<T>> submit,
            CancellationToken cancellationToken = default) => submit(cancellationToken);
    }

    private sealed class TestBroker : IBrokerClient, IBrokerAccountProvider, IBrokerMarketObservationProvider
    {
        public TestBroker(DateTimeOffset now)
        {
            MarketObservation = new BrokerMarketObservation(
                "MSFT", "sip", 99.95m, 100.05m, now.AddMilliseconds(-100),
                100m, now.AddMilliseconds(-100), now);
        }

        public BrokerMarketObservation MarketObservation { get; set; }
        public int AccountCalls { get; private set; }
        public int PositionCalls { get; private set; }
        public int OpenOrderCalls { get; private set; }

        public Task<TradingSessionSnapshot> GetSessionAsync(
            DateTimeOffset timestampUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TradingSessionSnapshot(
                new DateOnly(2026, 7, 22), EquityTradingSession.Regular,
                timestampUtc, timestampUtc.AddHours(-1), timestampUtc.AddHours(5)));

        public Task<AssetTradingEligibility?> GetEligibilityAsync(
            string symbol,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssetTradingEligibility?>(new AssetTradingEligibility(
                symbol, true, true, true, Now, Shortable: true, BorrowStatus: "easy_to_borrow"));

        public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken)
        {
            AccountCalls++;
            return Task.FromResult(new BrokerAccountSnapshot(
                "paper", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 0m, 0m, Now));
        }

        public Task<BrokerMarketObservation> GetMarketObservationAsync(
            string symbol,
            EquityTradingSession session,
            CancellationToken cancellationToken) => Task.FromResult(MarketObservation);

        public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
        {
            PositionCalls++;
            return Task.FromResult<IReadOnlyList<BrokerPosition>>([]);
        }

        public Task<IReadOnlyList<ActiveBrokerOrder>> GetOpenOrdersAsync(CancellationToken cancellationToken)
        {
            OpenOrderCalls++;
            return Task.FromResult<IReadOnlyList<ActiveBrokerOrder>>([]);
        }

        public Task<BrokerOrderReceipt> SubmitOrderAsync(BrokerEntryOrder order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(ProtectiveStopOrder order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<ActiveBrokerOrder?>(null);
        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ModifyOrderAsync(string orderId, decimal newStopLoss, decimal newTakeProfit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string[]> SubmitExitOrdersAsync(string ticker, int quantity, decimal stopLossPrice, decimal takeProfitPrice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ClosePositionAsync(string ticker, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ClosePositionAsync(string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
