using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

public sealed class EntryDecisionPersistenceTests
{
    private static readonly DateTimeOffset GateTime =
        new(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CandidateRepository_DoesNotMutatePersistedDiscoveryEvidenceOnReplay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCandidateRepository(database.Factory);
        var run = CreateRun();
        var discoveredAt = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero);
        var candidateId = Guid.NewGuid();
        var original = CreateCandidate(run, candidateId, discoveredAt, discoveredAt, 100m, 5m);

        await repository.UpsertDiscoveryAsync(run, original);
        var refreshed = CreateCandidate(
            run,
            candidateId,
            discoveredAt,
            discoveredAt.AddSeconds(15),
            101m,
            3m);
        await repository.UpsertDiscoveryAsync(run, refreshed);

        var stored = await repository.GetAsync(candidateId);
        Assert.NotNull(stored);
        Assert.Equal(candidateId, stored.CandidateId);
        Assert.Equal(discoveredAt, stored.DiscoveredAtUtc);
        Assert.Equal(discoveredAt, stored.RevalidatedAtUtc);
        Assert.Equal(100m, stored.LastPrice);
        Assert.Equal(5m, stored.SpreadBps);
        await using var context = await database.Factory.CreateDbContextAsync();
        Assert.Equal(1, await context.Candidates.CountAsync());
        Assert.Equal(1, await context.ProductionRuns.CountAsync());
    }

    [Fact]
    public async Task GateEvaluationRepository_PersistsContiguousRejectedPrefixAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var candidateRepository = new SqliteCandidateRepository(database.Factory);
        var repository = new SqliteGateEvaluationRepository(database.Factory);
        var run = CreateRun();
        var now = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero);
        var candidate = CreateCandidate(run, Guid.NewGuid(), now, now, 100m, 5m);
        await candidateRepository.UpsertDiscoveryAsync(run, candidate);
        var requests = new[]
        {
            new GateEvaluationAppendRequest(
                candidate.CandidateId, null, 1, "system_state", true, null, now, "{\"allowed\":true}"),
            new GateEvaluationAppendRequest(
                candidate.CandidateId, null, 2, "calendar_window", false,
                RejectCode.REJECT_SETUP_INVALID, now.AddMilliseconds(1), "{\"session\":\"closed\"}")
        };

        var saved = await repository.AppendBatchAsync(run, requests);

        Assert.Equal(2, saved.Count);
        await using var context = await database.Factory.CreateDbContextAsync();
        var stored = await context.GateEvaluations
            .AsNoTracking()
            .OrderBy(item => item.GateOrder)
            .ToArrayAsync();
        Assert.Collection(
            stored,
            first =>
            {
                Assert.Equal(1, first.GateOrder);
                Assert.True(first.Passed);
                Assert.Null(first.RejectCode);
            },
            second =>
            {
                Assert.Equal(2, second.GateOrder);
                Assert.False(second.Passed);
                Assert.Equal(RejectCode.REJECT_SETUP_INVALID, second.RejectCode);
            });
    }

    [Fact]
    public async Task GateEvaluationRepository_RejectsNonContiguousBatchBeforeWriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteGateEvaluationRepository(database.Factory);
        var run = CreateRun();
        var now = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero);
        var candidateId = Guid.NewGuid();
        var requests = new[]
        {
            new GateEvaluationAppendRequest(
                candidateId, null, 1, "system_state", true, null, now, "{}"),
            new GateEvaluationAppendRequest(
                candidateId, null, 3, "candidate_state", true, null, now, "{}")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AppendBatchAsync(run, requests));

        await using var context = await database.Factory.CreateDbContextAsync();
        Assert.Empty(await context.GateEvaluations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await context.ProductionRuns.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task GateEvaluationRepository_RejectionAtomicallyRiskBlocksTriggeredCandidate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var run = CreateRun();
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(database.Factory, run, candidateId, semanticHash, GateTime.AddMinutes(5));
        var repository = new SqliteGateEvaluationRepository(
            database.Factory,
            new FixedTimeProvider(GateTime));
        var requests = RejectedGatePrefix(candidateId, GateTime);
        var rejection = new CandidateGateRejection(
            candidateId,
            3,
            semanticHash,
            "MSFT",
            "swing.test.v1",
            "calendar_window",
            RejectCode.REJECT_SETUP_INVALID);

        var saved = await repository.AppendBatchAsync(run, requests, rejection);
        var replayed = await repository.AppendBatchAsync(run, requests, rejection);

        Assert.Equal(2, saved.Count);
        Assert.Equal(2, replayed.Count);
        await using var context = await database.Factory.CreateDbContextAsync();
        var candidate = await context.Candidates.AsNoTracking().SingleAsync();
        var transitions = await context.CandidateTransitions
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync();
        Assert.Equal(StrategyCandidateState.RiskBlocked, candidate.State);
        Assert.Equal(4, candidate.Version);
        Assert.Equal(GateTime, candidate.RevalidatedAtUtc);
        Assert.Equal(2, transitions.Length);
        Assert.Equal(StrategyCandidateState.RiskBlocked, transitions[^1].NewState);
        Assert.Equal("candidate_blocked_by_entry_gate", transitions[^1].ReasonCode);
        Assert.Equal(4, await context.GateEvaluations.CountAsync());
        Assert.All(
            await context.GateEvaluations.AsNoTracking().ToArrayAsync(),
            evaluation => Assert.Equal(GateTime, evaluation.EvaluatedAtUtc));
    }

    [Fact]
    public async Task GateEvaluationRepository_ExpiredCandidate_CommitsExpiredOutcome()
    {
        await using var database = await TestDatabase.CreateAsync();
        var run = CreateRun();
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(database.Factory, run, candidateId, semanticHash, GateTime.AddTicks(-1));
        var repository = new SqliteGateEvaluationRepository(
            database.Factory,
            new FixedTimeProvider(GateTime));

        await repository.AppendBatchAsync(
            run,
            RejectedGatePrefix(candidateId, GateTime),
            new CandidateGateRejection(
                candidateId,
                3,
                semanticHash,
                "MSFT",
                "swing.test.v1",
                "calendar_window",
                RejectCode.REJECT_SETUP_INVALID));

        await using var context = await database.Factory.CreateDbContextAsync();
        var candidate = await context.Candidates.AsNoTracking().SingleAsync();
        var terminal = await context.CandidateTransitions
            .AsNoTracking()
            .OrderBy(item => item.Sequence)
            .LastAsync();
        Assert.Equal(StrategyCandidateState.Expired, candidate.State);
        Assert.Equal(StrategyCandidateState.Expired, terminal.NewState);
        Assert.Equal("candidate_expired_during_entry_gates", terminal.ReasonCode);
    }

    [Fact]
    public async Task GateEvaluationRepository_MissingTriggeredTransition_RollsBackGateAudit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var run = CreateRun();
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(
            database.Factory,
            run,
            candidateId,
            semanticHash,
            GateTime.AddMinutes(5),
            includeTransition: false);
        var repository = new SqliteGateEvaluationRepository(
            database.Factory,
            new FixedTimeProvider(GateTime));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repository.AppendBatchAsync(
            run,
            RejectedGatePrefix(candidateId, GateTime),
            new CandidateGateRejection(
                candidateId,
                3,
                semanticHash,
                "MSFT",
                "swing.test.v1",
                "calendar_window",
                RejectCode.REJECT_SETUP_INVALID)));

        await using var context = await database.Factory.CreateDbContextAsync();
        Assert.Empty(await context.GateEvaluations.AsNoTracking().ToArrayAsync());
        var candidate = await context.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(StrategyCandidateState.Triggered, candidate.State);
        Assert.Equal(3, candidate.Version);
    }

    [Fact]
    public async Task GateEvaluationRepository_MismatchedSubmissionIdentity_AuditsWithoutBlockingCandidate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var run = CreateRun();
        var candidateId = Guid.NewGuid();
        var semanticHash = new string('c', 64);
        await SeedTriggeredCandidateAsync(database.Factory, run, candidateId, semanticHash, GateTime.AddMinutes(5));
        var repository = new SqliteGateEvaluationRepository(
            database.Factory,
            new FixedTimeProvider(GateTime));

        await repository.AppendBatchAsync(
            run,
            RejectedGatePrefix(candidateId, GateTime),
            new CandidateGateRejection(
                candidateId,
                3,
                semanticHash,
                "AAPL",
                "swing.test.v1",
                "calendar_window",
                RejectCode.REJECT_SETUP_INVALID));

        await using var context = await database.Factory.CreateDbContextAsync();
        var candidate = await context.Candidates.AsNoTracking().SingleAsync();
        Assert.Equal(StrategyCandidateState.Triggered, candidate.State);
        Assert.Equal(3, candidate.Version);
        Assert.Single(await context.CandidateTransitions.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, await context.GateEvaluations.CountAsync());
    }

    private static GateEvaluationAppendRequest[] RejectedGatePrefix(
        Guid candidateId,
        DateTimeOffset evaluatedAtUtc) =>
    [
        new(candidateId, null, 1, "system_state", true, null, evaluatedAtUtc, "{\"allowed\":true}"),
        new(candidateId, null, 2, "calendar_window", false,
            RejectCode.REJECT_SETUP_INVALID, evaluatedAtUtc, "{\"session\":\"closed\"}")
    ];

    private static async Task SeedTriggeredCandidateAsync(
        IDbContextFactory<TradingFlowDbContext> factory,
        ProductionRun run,
        Guid candidateId,
        string semanticHash,
        DateTimeOffset expiresAtUtc,
        bool includeTransition = true)
    {
        var candidateRepository = new SqliteCandidateRepository(factory);
        var candidate = CreateCandidate(
            run,
            candidateId,
            GateTime.AddMinutes(-1),
            GateTime.AddSeconds(-1),
            100m,
            5m);
        await candidateRepository.UpsertDiscoveryAsync(run, candidate);
        await using var context = await factory.CreateDbContextAsync();
        var persisted = await context.Candidates.SingleAsync(record => record.CandidateId == candidateId);
        persisted.State = StrategyCandidateState.Triggered;
        persisted.Version = 3;
        persisted.ExpiresAtUtc = expiresAtUtc;
        persisted.SemanticDecisionSha256 = semanticHash;
        if (includeTransition)
        {
            context.CandidateTransitions.Add(new CandidateTransitionRecord
            {
                CandidateId = candidateId,
                Sequence = 3,
                PreviousState = StrategyCandidateState.Armed,
                NewState = StrategyCandidateState.Triggered,
                OccurredAtUtc = GateTime.AddSeconds(-1),
                ReasonCode = "execution_trigger_satisfied",
                Source = "strategy_decision_kernel",
                SemanticDecisionSha256 = semanticHash,
                EvidenceJson = "{}",
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            });
        }

        await context.SaveChangesAsync();
    }

    private static ProductionRun CreateRun() => new()
    {
        RunId = Guid.NewGuid(),
        SchemaVersion = 1,
        ConfigHash = new string('a', 64),
        CodeVersion = "test-code",
        Profile = "paper",
        Status = "running",
        StartedAtUtc = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero)
    };

    private static CandidateRecord CreateCandidate(
        ProductionRun run,
        Guid candidateId,
        DateTimeOffset discoveredAt,
        DateTimeOffset revalidatedAt,
        decimal lastPrice,
        decimal spreadBps) => new()
        {
            CandidateId = candidateId,
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion,
            Symbol = "MSFT",
            DiscoveredAtUtc = discoveredAt,
            RevalidatedAtUtc = revalidatedAt,
            DiscoverySource = "strategy",
            FinvizPreset = String.Empty,
            Horizon = "swing",
            LastPrice = lastPrice,
            SpreadBps = spreadBps,
            SetupScoresJson = "{}",
            SelectedStrategy = "swing.test.v1",
            StrategyContentSha256 = new string('b', 64),
            AdmissionProfileId = "default-deterministic-v1",
            AdmissionProfileVersion = "1.0.0",
            SetupKey = "swing.test.v1:2026-07-22T14:00:00.0000000+00:00",
            DiscoveryWindowStartUtc = discoveredAt,
            DiscoveryWindowEndUtc = discoveredAt.AddMinutes(30),
            State = StrategyCandidateState.Discovered,
            ExpiresAtUtc = discoveredAt.AddMinutes(30),
            RejectReasonsJson = "[]"
        };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(
            SqliteConnection connection,
            IDbContextFactory<TradingFlowDbContext> factory)
        {
            this.connection = connection;
            Factory = factory;
        }

        public IDbContextFactory<TradingFlowDbContext> Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new TradingFlowDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, new TestDbContextFactory(options));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestampUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestampUtc;
    }
}
