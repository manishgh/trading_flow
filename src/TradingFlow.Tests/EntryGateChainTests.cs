using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Tests;

public sealed class EntryGateChainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);
    private static readonly StrategyArtifactIdentity StrategyIdentity =
        new("SWING-V1", "1.0.0", new string('d', 64));

    [Fact]
    public async Task ExecuteAsync_AllEvidencePasses_PersistsTwelveOrderedGatesBeforeSubmission()
    {
        var fixture = new Fixture();
        var submitted = false;
        EntryGateApproval? approval = null;

        var result = await fixture.Chain.ExecuteAsync(
            fixture.Submission,
            fixture.Broker,
            (value, _) =>
            {
                Assert.Equal(12, fixture.Evaluations.Requests.Count);
                approval = value;
                submitted = true;
                return Task.FromResult("submitted");
            });

        Assert.True(submitted);
        Assert.NotNull(approval);
        Assert.Equal(new DateOnly(2026, 7, 22), approval!.TradeDate);
        Assert.False(approval.SubmitOutsideRegularHours);
        Assert.Equal("submitted", result);
        Assert.Equal(Enumerable.Range(1, 12), fixture.Evaluations.Requests.Select(item => item.GateOrder));
        Assert.All(fixture.Evaluations.Requests, item => Assert.True(item.Passed));
        Assert.Null(fixture.Evaluations.CandidateRejection);
        Assert.Equal(2, fixture.Broker.AccountCalls);
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
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.HaltOrLuld, exception.Slot);
        Assert.Equal(4, fixture.Evaluations.Requests.Count);
        Assert.False(fixture.Evaluations.Requests[^1].Passed);
        Assert.NotNull(fixture.Evaluations.CandidateRejection);
        Assert.Equal("halt_or_luld", fixture.Evaluations.CandidateRejection!.FailedGateName);
        Assert.Equal(RejectCode.REJECT_HALT_OR_LULD, fixture.Evaluations.CandidateRejection.RejectCode);
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
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.QuoteAge, exception.Slot);
        Assert.Equal(5, fixture.Evaluations.Requests.Count);
        Assert.Equal(0, fixture.Broker.AccountCalls);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_StaleAccountSnapshot_FailsClosedAtAccountGate()
    {
        var fixture = new Fixture();
        fixture.Broker.AccountSnapshot = fixture.Broker.AccountSnapshot with
        {
            ObservedAtUtc = Now.AddSeconds(-61)
        };

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.Account, exception.Slot);
        Assert.Equal(RejectCode.REJECT_DEGRADED_DATA, exception.RejectCode);
        Assert.Equal(7, fixture.Evaluations.Requests.Count);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_AccountResponseCompletedAfterGateStarted_IsNotFutureDated()
    {
        var clock = new SequencedTimeProvider(
            Now,
            Now.AddMilliseconds(20));
        var fixture = new Fixture(timeProvider: clock);
        fixture.Broker.AccountSnapshot = fixture.Broker.AccountSnapshot with
        {
            RequestedAtUtc = Now.AddMilliseconds(5),
            ObservedAtUtc = Now.AddMilliseconds(15)
        };

        var result = await fixture.Chain.ExecuteAsync(
            fixture.Submission,
            fixture.Broker,
            (_, _) => Task.FromResult("submitted"));

        Assert.Equal("submitted", result);
        Assert.All(fixture.Evaluations.Requests, evaluation => Assert.True(evaluation.Passed));
    }

    [Fact]
    public async Task ExecuteAsync_SwingOrder_UsesLowerRegulationTBuyingPower()
    {
        var fixture = new Fixture(horizon: "swing");
        fixture.Broker.AccountSnapshot = fixture.Broker.AccountSnapshot with
        {
            BuyingPower = 100_000m,
            RegulationTBuyingPower = 500m
        };

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.Account, exception.Slot);
        Assert.Equal(RejectCode.REJECT_BUDGET_EXHAUSTED, exception.RejectCode);
        Assert.Equal(7, fixture.Evaluations.Requests.Count);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledMarketObservation_ReleasesStatusObservation()
    {
        var statuses = new TrackingTradingStatusProvider();
        var fixture = new Fixture(tradingStatusProvider: statuses);
        fixture.Broker.MarketObservationException = new OperationCanceledException("cancelled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(1, statuses.EnsureCalls);
        Assert.Equal(1, statuses.ReleaseCalls);
    }

    [Fact]
    public async Task ExecuteAsync_TriggeredCandidateWithoutMatchingTransition_IsRejectedBeforeMarketObservation()
    {
        var fixture = new Fixture(includeTriggerTransition: false);

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.CandidateState, exception.Slot);
        Assert.Equal(3, fixture.Evaluations.Requests.Count);
        Assert.False(fixture.Evaluations.Requests[^1].Passed);
        Assert.Equal(0, fixture.Broker.AccountCalls);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_NonSwingCandidate_IsRejectedAtCandidateGateBeforeMarketObservation()
    {
        var fixture = new Fixture(horizon: "unsupported-short-horizon");

        var exception = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.CandidateState, exception.Slot);
        Assert.Equal(RejectCode.REJECT_SETUP_INVALID, exception.RejectCode);
        Assert.Equal(3, fixture.Evaluations.Requests.Count);
        Assert.Equal(0, fixture.Broker.AccountCalls);
        Assert.Equal(0, fixture.Broker.PositionCalls);
        Assert.Equal(0, fixture.Broker.OpenOrderCalls);
    }

    [Fact]
    public async Task ExecuteAsync_OperatorOverrideRejection_DoesNotRequestCandidateTransition()
    {
        var fixture = new Fixture(
            SecurityTradingState.Halted,
            operatorOverride: true);

        await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Null(fixture.Evaluations.CandidateRejection);
        Assert.False(fixture.Evaluations.Requests[^1].Passed);
    }

    [Fact]
    public async Task ExecuteAsync_SuspendedStrategy_IsRejectedInCandidateGateAndTerminalized()
    {
        var fixture = new Fixture();
        await fixture.Authorizations.SuspendPaperExperimentAsync(
            StrategyIdentity,
            "suspend-for-test",
            "test-grant",
            "test-operator",
            Now,
            "Block new entries.");

        var error = await Assert.ThrowsAsync<EntryGateRejectedException>(() =>
            fixture.Chain.ExecuteAsync(
                fixture.Submission,
                fixture.Broker,
                (_, _) => Task.FromResult("must-not-submit")));

        Assert.Equal(EntryGateSlot.CandidateState, error.Slot);
        Assert.NotNull(fixture.Evaluations.CandidateRejection);
        Assert.Contains("strategyAuthorizationError", fixture.Evaluations.Requests[^1].InputsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_UsesServerClockForProviderSessionLookup()
    {
        var fixture = new Fixture();
        fixture.Submission = fixture.Submission with
        {
            CreatedAtUtc = Now.AddDays(-2),
            SessionDate = new DateOnly(2026, 7, 20)
        };

        var approval = await fixture.Chain.ExecuteAsync(
            fixture.Submission,
            fixture.Broker,
            (value, _) => Task.FromResult(value));

        Assert.Equal(Now, fixture.Broker.LastSessionRequestUtc);
        Assert.Equal(new DateOnly(2026, 7, 22), approval.TradeDate);
    }

    private sealed class Fixture
    {
        public Fixture(
            SecurityTradingState tradingState = SecurityTradingState.TradingObserved,
            ISecurityTradingStatusProvider? tradingStatusProvider = null,
            bool includeTriggerTransition = true,
            bool operatorOverride = false,
            string horizon = "swing",
            TimeProvider? timeProvider = null)
        {
            var candidateId = Guid.NewGuid();
            const int candidateVersion = 3;
            var semanticDecisionSha256 = new string('c', 64);
            var runContext = new ExecutionRunContext(
                Guid.NewGuid(),
                "paper",
                new string('a', 64),
                new string('b', 40),
                Now.AddMinutes(-1));
            Submission = new EntryOrderSubmission(
                Guid.NewGuid(),
                new ValidatedEntryCandidate(
                    candidateId,
                    "strategy_signal",
                    horizon,
                    Now.AddMinutes(-1),
                    Now.AddSeconds(-1),
                    "{}",
                    candidateVersion,
                    semanticDecisionSha256),
                runContext,
                "SWING-V1",
                "buy",
                "limit",
                "day",
                new DateOnly(2026, 7, 22),
                Now,
                new FinalizedOrder(
                    "MSFT", "test", 10, 100m, 98m, 104m, Now, String.Empty),
                StrategyIdentity: operatorOverride ? null : StrategyIdentity,
                StrategySelectionMode: operatorOverride ? null : StrategySelectionMode.RunPaperShadow,
                OperatorOverride: operatorOverride
                    ? OperatorOverrideAuthorization.Issue(
                        new ManualEntryOptions(ManualEntryPolicy.OperatorDirect),
                        "test-operator",
                        "test override",
                        Now)
                    : null);
            Candidates = new MemoryCandidateRepository(new CandidateRecord
            {
                CandidateId = candidateId,
                Symbol = "MSFT",
                DiscoveredAtUtc = Now.AddMinutes(-1),
                RevalidatedAtUtc = Now.AddSeconds(-1),
                DiscoverySource = "strategy_signal",
                Horizon = horizon,
                SelectedStrategy = "SWING-V1",
                StrategyContentSha256 = new string('d', 64),
                AdmissionProfileId = "test-profile",
                AdmissionProfileVersion = "1.0.0",
                SetupKey = "SWING-V1:2026-07-22T14:34:00.0000000+00:00",
                DiscoveryWindowStartUtc = Now.AddMinutes(-1),
                DiscoveryWindowEndUtc = Now.AddMinutes(5),
                State = StrategyCandidateState.Triggered,
                Version = candidateVersion,
                ExpiresAtUtc = Now.AddMinutes(5),
                SemanticDecisionSha256 = semanticDecisionSha256
            }, includeTriggerTransition);
            Evaluations = new RecordingGateEvaluationRepository();
            Authorizations = new StrategyAuthorizationTestRegistry();
            Authorizations.Seed(StrategyIdentity, StrategyExecutionAuthorization.PaperExperiment);
            Authorizations.Seed(StrategyIdentity, StrategyExecutionAuthorization.PaperShadow);
            Broker = new TestBroker(Now);
            Chain = new EntryGateChain(
                new EntryAdmissionControl(),
                new PassThroughPositionConflictGuard(),
                Candidates,
                Evaluations,
                Authorizations,
                tradingStatusProvider ?? new FixedTradingStatusProvider(tradingState, Now),
                new EntryGateOptions(20, 2_000, 20m, 15m, 15m, 75m, 3),
                timeProvider ?? new FixedTimeProvider(Now));
        }

        public EntryOrderSubmission Submission { get; set; }
        public MemoryCandidateRepository Candidates { get; }
        public RecordingGateEvaluationRepository Evaluations { get; }
        public StrategyAuthorizationTestRegistry Authorizations { get; }
        public TestBroker Broker { get; }
        public EntryGateChain Chain { get; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequencedTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow()
        {
            var current = Math.Min(Interlocked.Increment(ref index) - 1, values.Length - 1);
            return values[current];
        }
    }

    private sealed class FixedTradingStatusProvider(
        SecurityTradingState state,
        DateTimeOffset now) : ISecurityTradingStatusProvider
    {
        public Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReleaseObservationAsync(string symbol, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public SecurityTradingStatus GetStatus(string symbol) =>
            new(symbol, state, state == SecurityTradingState.Halted ? "H" : "T", null, now, now);
    }

    private sealed class TrackingTradingStatusProvider : ISecurityTradingStatusProvider
    {
        public int EnsureCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public Task ReleaseObservationAsync(string symbol, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }

        public SecurityTradingStatus GetStatus(string symbol) =>
            new(symbol, SecurityTradingState.Unknown, null, null, null, Now);
    }

    private sealed class MemoryCandidateRepository(
        CandidateRecord candidate,
        bool includeTriggerTransition) : ICandidateRepository
    {
        public Task<CandidateRecord> UpsertDiscoveryAsync(
            ProductionRun run,
            CandidateRecord value,
            CancellationToken cancellationToken = default) => Task.FromResult(value);

        public Task<CandidateRecord> ApplyDecisionAsync(
            ProductionRun run,
            Guid candidateId,
            int expectedVersion,
            string semanticDecisionSha256,
            DateTimeOffset expiresAtUtc,
            IReadOnlyList<CandidateTransitionAppendRequest> transitions,
            CancellationToken cancellationToken = default) => Task.FromResult(candidate);

        public Task<CandidateRecord?> GetAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate.CandidateId == candidateId ? candidate : null);

        public Task<IReadOnlyList<CandidateTransitionRecord>> GetTransitionsAsync(
            Guid candidateId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CandidateTransitionRecord>>(
                candidate.CandidateId != candidateId || !includeTriggerTransition
                    ? []
                    :
                    [
                        new CandidateTransitionRecord
                        {
                            CandidateId = candidate.CandidateId,
                            Sequence = candidate.Version,
                            PreviousState = StrategyCandidateState.Armed,
                            NewState = StrategyCandidateState.Triggered,
                            OccurredAtUtc = candidate.RevalidatedAtUtc,
                            ReasonCode = "execution_trigger_satisfied",
                            Source = "strategy_decision_kernel",
                            SemanticDecisionSha256 = candidate.SemanticDecisionSha256,
                            EvidenceJson = "{}"
                        }
                    ]);
    }

    private sealed class RecordingGateEvaluationRepository : IGateEvaluationRepository
    {
        public IReadOnlyList<GateEvaluationAppendRequest> Requests { get; private set; } = [];

        public CandidateGateRejection? CandidateRejection { get; private set; }

        public Task<IReadOnlyList<GateEvaluationRecord>> AppendBatchAsync(
            ProductionRun run,
            IReadOnlyList<GateEvaluationAppendRequest> evaluations,
            CandidateGateRejection? candidateRejection = null,
            CancellationToken cancellationToken = default)
        {
            Requests = evaluations.ToArray();
            CandidateRejection = candidateRejection;
            return Task.FromResult<IReadOnlyList<GateEvaluationRecord>>([]);
        }
    }

    private sealed class PassThroughPositionConflictGuard : IPositionConflictGuard
    {
        public Task<T> ExecuteEntryAsync<T>(
            string accountId,
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
            AccountSnapshot = new BrokerAccountSnapshot(
                "paper", "ACTIVE", false, false, false, true,
                100_000m, 100_000m, 100_000m, 0m, 0m, now, now);
        }

        public BrokerMarketObservation MarketObservation { get; set; }
        public BrokerAccountSnapshot AccountSnapshot { get; set; }
        public Exception? MarketObservationException { get; set; }
        public int AccountCalls { get; private set; }
        public int PositionCalls { get; private set; }
        public int OpenOrderCalls { get; private set; }
        public DateTimeOffset? LastSessionRequestUtc { get; private set; }

        public Task<TradingSessionSnapshot> GetSessionAsync(
            DateTimeOffset timestampUtc,
            CancellationToken cancellationToken)
        {
            LastSessionRequestUtc = timestampUtc;
            return Task.FromResult(new TradingSessionSnapshot(
                new DateOnly(2026, 7, 22), EquityTradingSession.Regular,
                timestampUtc, timestampUtc.AddHours(-1), timestampUtc.AddHours(5)));
        }

        public Task<AssetTradingEligibility?> GetEligibilityAsync(
            string symbol,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssetTradingEligibility?>(new AssetTradingEligibility(
                symbol, true, true, true, Now, Shortable: true, BorrowStatus: "easy_to_borrow"));

        public Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken)
        {
            AccountCalls++;
            return Task.FromResult(AccountSnapshot);
        }

        public Task<BrokerMarketObservation> GetMarketObservationAsync(
            string symbol,
            EquityTradingSession session,
            CancellationToken cancellationToken) => MarketObservationException is null
                ? Task.FromResult(MarketObservation)
                : Task.FromException<BrokerMarketObservation>(MarketObservationException);

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
        public Task<BrokerOrderReceipt> SubmitExitOrderAsync(BrokerExitOrder order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BrokerOrderReceipt> SubmitProtectiveStopAsync(ProtectiveStopOrder order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ActiveBrokerOrder?> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<ActiveBrokerOrder?>(null);
        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAllOrdersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ActiveBrokerOrder> ReplaceProtectiveOrderAsync(BrokerProtectiveOrderReplacement replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
