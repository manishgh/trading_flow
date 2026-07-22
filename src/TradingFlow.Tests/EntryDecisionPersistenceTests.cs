using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class EntryDecisionPersistenceTests
{
    [Fact]
    public async Task CandidateRepository_UpdatesRevalidationEvidenceWithoutChangingIdentity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteCandidateRepository(database.Factory);
        var run = CreateRun();
        var discoveredAt = new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero);
        var candidateId = Guid.NewGuid();
        var original = CreateCandidate(run, candidateId, discoveredAt, discoveredAt, 100m, 5m);

        await repository.UpsertValidatedAsync(run, original);
        var refreshed = CreateCandidate(
            run,
            candidateId,
            discoveredAt,
            discoveredAt.AddSeconds(15),
            101m,
            3m);
        await repository.UpsertValidatedAsync(run, refreshed);

        var stored = await repository.GetAsync(candidateId);
        Assert.NotNull(stored);
        Assert.Equal(candidateId, stored.CandidateId);
        Assert.Equal(discoveredAt, stored.DiscoveredAtUtc);
        Assert.Equal(discoveredAt.AddSeconds(15), stored.RevalidatedAtUtc);
        Assert.Equal(101m, stored.LastPrice);
        Assert.Equal(3m, stored.SpreadBps);
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
        await candidateRepository.UpsertValidatedAsync(run, candidate);
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
            Horizon = "intraday",
            LastPrice = lastPrice,
            SpreadBps = spreadBps,
            SetupScoresJson = "{}",
            SelectedStrategy = "intraday.test.v1",
            State = "SETUP_VALID",
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
}
