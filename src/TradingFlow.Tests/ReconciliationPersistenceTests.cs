using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class ReconciliationPersistenceTests
{
    [Fact]
    public async Task PositionLedger_ExactExecutionReplayIsIdempotent_ConflictFailsClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlitePositionLedgerRepository(database.Factory);
        var request = CreatePositionRequest(CreateRun(), "execution-1", 10m);

        var first = await repository.AppendFillAsync(request);
        var replay = await repository.AppendFillAsync(request);

        Assert.Equal(first.PositionEventId, replay.PositionEventId);
        Assert.Equal(10m, (await repository.GetCurrentAsync("msft"))?.Quantity);
        Assert.Equal(10m, await repository.GetAccountedFillQuantityAsync("broker-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AppendFillAsync(request with { QuantityAfter = 11m }));
    }

    [Fact]
    public async Task PositionLedger_AcceptsDelayedBrokerTimestamp_InLocalAppendOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqlitePositionLedgerRepository(database.Factory);
        var run = CreateRun();
        var first = CreatePositionRequest(run, "execution-1", 10m);
        await repository.AppendFillAsync(first);

        await repository.AppendFillAsync(first with
        {
            QuantityAfter = 15m,
            FillQuantity = 5m,
            ExecutionId = "execution-2",
            BrokerTimestampUtc = run.StartedAtUtc,
            LocalTimestampUtc = run.StartedAtUtc.AddSeconds(2)
        });

        Assert.Equal(15m, (await repository.GetCurrentAsync("MSFT"))?.Quantity);
        Assert.Equal(15m, await repository.GetAccountedFillQuantityAsync("broker-1"));
    }

    [Fact]
    public async Task ReconciliationRepository_DeduplicatesOutstandingDiff_AndPersistsReasonedAck()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteReconciliationRepository(database.Factory);
        var run = CreateRun();
        var request = new ReconciliationWriteRequest(
            run,
            Guid.NewGuid(),
            run.StartedAtUtc,
            run.StartedAtUtc.AddSeconds(1),
            "reconcile_mismatch",
            "{\"positions\":[]}",
            "{\"positions\":[]}",
            "[{\"kind\":\"position_quantity\"}]",
            new string('c', 64),
            true);

        var first = await repository.RecordAsync(request);
        var replay = await repository.RecordAsync(request with { ReconciliationId = Guid.NewGuid() });

        Assert.Equal(first.ReconciliationId, replay.ReconciliationId);
        var acknowledged = await repository.AcknowledgeAsync(new ReconciliationAcknowledgement(
            first.ReconciliationId,
            "operator@example.com",
            "Broker position was manually flattened and verified.",
            run.StartedAtUtc.AddMinutes(1)));

        Assert.False(acknowledged.RequiresAcknowledgement);
        Assert.Equal("operator@example.com", acknowledged.AcknowledgedBy);
        Assert.Equal("Broker position was manually flattened and verified.", acknowledged.AcknowledgementReason);
        Assert.Null(await repository.GetOutstandingAsync());

        var acknowledgedReplay = await repository.RecordAsync(request with
        {
            ReconciliationId = Guid.NewGuid(),
            StartedAtUtc = run.StartedAtUtc.AddMinutes(2),
            CompletedAtUtc = run.StartedAtUtc.AddMinutes(2).AddSeconds(1)
        });
        Assert.Equal(first.ReconciliationId, acknowledgedReplay.ReconciliationId);
        Assert.False(acknowledgedReplay.RequiresAcknowledgement);

        await repository.RecordAsync(request with
        {
            ReconciliationId = Guid.NewGuid(),
            StartedAtUtc = run.StartedAtUtc.AddMinutes(3),
            CompletedAtUtc = run.StartedAtUtc.AddMinutes(3).AddSeconds(1),
            Status = "clean",
            DiffJson = "[]",
            DiffHash = new string('d', 64),
            RequiresAcknowledgement = false
        });
        var recurrence = await repository.RecordAsync(request with
        {
            ReconciliationId = Guid.NewGuid(),
            StartedAtUtc = run.StartedAtUtc.AddMinutes(4),
            CompletedAtUtc = run.StartedAtUtc.AddMinutes(4).AddSeconds(1)
        });
        Assert.NotEqual(first.ReconciliationId, recurrence.ReconciliationId);
        Assert.True(recurrence.RequiresAcknowledgement);
    }

    private static PositionFillAppendRequest CreatePositionRequest(
        ProductionRun run,
        string executionId,
        decimal quantityAfter) => new(
        run,
        "MSFT",
        quantityAfter,
        10m,
        100.50m,
        "buy",
        "broker-1",
        "SWGA-B-MSFT-20260721-001-12345678",
        executionId,
        "broker_stream",
        run.StartedAtUtc.AddSeconds(1),
        run.StartedAtUtc.AddSeconds(1),
        "{\"event\":\"fill\"}");

    private static ProductionRun CreateRun() => new()
    {
        RunId = Guid.NewGuid(),
        Profile = "paper",
        Status = "running",
        StartedAtUtc = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
        ConfigHash = new string('a', 64),
        CodeVersion = new string('b', 40)
    };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, TestContextFactory factory)
        {
            this.connection = connection;
            Factory = factory;
        }

        public TestContextFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestContextFactory(options);
            await using var context = factory.CreateDbContext();
            await context.Database.MigrateAsync();
            return new TestDatabase(connection, factory);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    public sealed class TestContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
