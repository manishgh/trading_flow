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
        CreatedAtUtc = DateTimeOffset.UtcNow,
        RequestJson = "{\"symbol\":\"MSFT\"}"
    };

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
