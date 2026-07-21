using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class SqliteDurabilityTests
{
    [Fact]
    public async Task InitializeAsync_FileDatabase_EnforcesWalAndConnectionDurability()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);

        await using var context = new TradingFlowDbContext(options);
        await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        await context.Database.OpenConnectionAsync();

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(2, await ScalarLongAsync(connection, "PRAGMA synchronous;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5_000, await ScalarLongAsync(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task AppendAsync_ClosedAndReopenedDatabase_PreservesOrderIntent()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var contextFactory = new TestDbContextFactory(options);
        var runId = Guid.NewGuid();
        var intentId = Guid.NewGuid();

        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
            context.ProductionRuns.Add(new ProductionRun
            {
                RunId = runId,
                SchemaVersion = 1,
                ConfigHash = new string('a', 64),
                CodeVersion = "test-code-version",
                Profile = "paper",
                Status = "running",
                StartedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var repository = new SqliteOrderIntentRepository(contextFactory);
        await repository.AppendAsync(new OrderIntentRecord
        {
            IntentId = intentId,
            RunId = runId,
            SchemaVersion = 1,
            ConfigHash = new string('a', 64),
            CodeVersion = "test-code-version",
            ClientOrderId = "durable-intent-001",
            StrategyId = "test-strategy",
            Symbol = "MSFT",
            Side = "buy",
            OrderType = "limit",
            TimeInForce = "day",
            RequestedQuantity = 2.5m,
            LimitPrice = 420.25m,
            SessionDate = new DateOnly(2026, 7, 21),
            SequenceNumber = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestJson = "{\"symbol\":\"MSFT\"}"
        });

        await using var reopenedContext = new TradingFlowDbContext(
            database.CreateOptions(withDurabilityInterceptor: false));
        var persisted = await reopenedContext.OrderIntents
            .AsNoTracking()
            .SingleAsync(record => record.IntentId == intentId);

        Assert.Equal("durable-intent-001", persisted.ClientOrderId);
        Assert.Equal(2.5m, persisted.RequestedQuantity);
        Assert.Equal(420.25m, persisted.LimitPrice);
    }

    [Fact]
    public async Task AppendAsync_DuplicateClientOrderId_IsRejected()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var contextFactory = new TestDbContextFactory(options);
        var runId = Guid.NewGuid();

        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
            context.ProductionRuns.Add(CreateRun(runId));
            await context.SaveChangesAsync();
        }

        var repository = new SqliteOrderIntentRepository(contextFactory);
        await repository.AppendAsync(CreateIntent(runId, Guid.NewGuid(), "duplicate-client-id"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AppendAsync(CreateIntent(runId, Guid.NewGuid(), "duplicate-client-id")));
    }

    [Fact]
    public async Task ReserveAsync_RepeatedLogicalIntent_ReusesPersistedClientOrderId()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var first = await repository.ReserveAsync(run, reservation);
        var replay = await repository.ReserveAsync(run, reservation);

        Assert.Equal(first.ClientOrderId, replay.ClientOrderId);
        Assert.Equal(1, replay.SequenceNumber);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(1, await verification.OrderIntents.CountAsync());
    }

    [Fact]
    public async Task ReserveAsync_ParallelDistinctIntents_AllocatesDistinctMonotonicSequences()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateReservation(Guid.NewGuid(), "MSFT"))
            .ToArray();
        var reserved = await Task.WhenAll(
            reservations.Select(reservation => repository.ReserveAsync(run, reservation)));

        Assert.Equal(Enumerable.Range(1, 8), reserved.Select(intent => intent.SequenceNumber).Order());
        Assert.Equal(8, reserved.Select(intent => intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReserveAsync_ParallelRepositoryInstances_AllocateDistinctMonotonicSequences()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var contextFactory = new TestDbContextFactory(options);
        var repositories = Enumerable.Range(0, 4)
            .Select(_ => new SqliteOrderIntentRepository(contextFactory))
            .ToArray();
        var run = CreateRun(Guid.NewGuid());

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        var reservations = Enumerable.Range(0, 8)
            .Select(_ => CreateReservation(Guid.NewGuid(), "MSFT"))
            .ToArray();
        var reserved = await Task.WhenAll(reservations.Select((reservation, index) =>
            repositories[index % repositories.Length].ReserveAsync(run, reservation)));

        Assert.Equal(Enumerable.Range(1, 8), reserved.Select(intent => intent.SequenceNumber).Order());
        Assert.Equal(8, reserved.Select(intent => intent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentWithChangedRequest_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        var changed = reservation with { RequestedQuantity = reservation.RequestedQuantity + 1m };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, changed));
        Assert.Contains("different order request", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentWithChangedProfile_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        var changedRun = new ProductionRun
        {
            RunId = run.RunId,
            SchemaVersion = run.SchemaVersion,
            ConfigHash = run.ConfigHash,
            CodeVersion = run.CodeVersion,
            Profile = "live",
            Status = run.Status,
            StartedAtUtc = run.StartedAtUtc
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(changedRun, reservation));
        Assert.Contains("different provenance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReserveAsync_ReusedIntentForCompletedRun_FailsClosed()
    {
        await using var database = new TemporarySqliteDatabase();
        var options = database.CreateOptions(withDurabilityInterceptor: true);
        var repository = new SqliteOrderIntentRepository(new TestDbContextFactory(options));
        var run = CreateRun(Guid.NewGuid());
        var reservation = CreateReservation(Guid.NewGuid(), "MSFT");

        await using (var context = new TradingFlowDbContext(options))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(context);
        }

        await repository.ReserveAsync(run, reservation);
        await using (var context = new TradingFlowDbContext(options))
        {
            var persistedRun = await context.ProductionRuns.SingleAsync(record => record.RunId == run.RunId);
            persistedRun.Status = "completed";
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.ReserveAsync(run, reservation));
        Assert.Contains("state 'completed'", error.Message, StringComparison.Ordinal);
    }

    private static ProductionRun CreateRun(Guid runId) => new()
    {
        RunId = runId,
        SchemaVersion = 1,
        ConfigHash = new string('b', 64),
        CodeVersion = "test-code-version",
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    private static OrderIntentRecord CreateIntent(Guid runId, Guid intentId, string clientOrderId) => new()
    {
        IntentId = intentId,
        RunId = runId,
        SchemaVersion = 1,
        ConfigHash = new string('b', 64),
        CodeVersion = "test-code-version",
        ClientOrderId = clientOrderId,
        StrategyId = "test-strategy",
        Symbol = "MSFT",
        Side = "buy",
        OrderType = "market",
        TimeInForce = "day",
        RequestedQuantity = 1m,
        SessionDate = new DateOnly(2026, 7, 21),
        SequenceNumber = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        RequestJson = "{\"symbol\":\"MSFT\"}"
    };

    private static OrderIntentReservation CreateReservation(Guid intentId, string symbol) => new(
        intentId,
        CandidateId: null,
        StrategyId: "SWGA",
        Symbol: symbol,
        Side: "buy",
        OrderType: "limit",
        TimeInForce: "day",
        RequestedQuantity: 10m,
        LimitPrice: 100m,
        StopPrice: 98m,
        SessionDate: new DateOnly(2026, 7, 21),
        CreatedAtUtc: new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
        RequestJson: $"{{\"symbol\":\"{symbol}\",\"quantity\":10}}");

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TemporarySqliteDatabase : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"tradingflow-durability-{Guid.NewGuid():N}");

        public TemporarySqliteDatabase()
        {
            Directory.CreateDirectory(root);
        }

        public DbContextOptions<TradingFlowDbContext> CreateOptions(bool withDurabilityInterceptor)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "tradingflow.db"),
                Pooling = false
            }.ToString();
            var builder = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite(connectionString);
            if (withDurabilityInterceptor)
            {
                builder.AddInterceptors(new SqliteConnectionDurabilityInterceptor());
            }

            return builder.Options;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
