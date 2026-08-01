using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Data.Context;
using TradingFlow.Engine.Risk;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class DatabaseMigrationTests
{
    private static readonly string[] ProductionTables =
    [
        "runs",
        "order_intents",
        "order_events",
        "gate_evaluations",
        "risk_events",
        "kill_switch_events",
        "reconciliations",
        "position_events",
        "candidates",
        "catalyst_results"
    ];

    [Fact]
    public async Task InitializeAsync_FreshDatabase_AppliesAllMigrationsAndProvenanceColumns()
    {
        await using var connection = await OpenInMemoryAsync();
        await using var db = CreateContext(connection);

        await new TradingFlowDatabaseInitializer().InitializeAsync(db);

        var tables = await ReadTablesAsync(connection);
        Assert.Contains("Wishlists", tables);
        Assert.Contains("EarningsCalendarEvents", tables);
        Assert.Contains("EarningsAnalysisSnapshots", tables);
        Assert.All(ProductionTables, table => Assert.Contains(table, tables));
        Assert.Equal(7, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var requiredProvenance = new[] { "run_id", "schema_version", "config_hash", "code_version" };
        foreach (var table in ProductionTables)
        {
            var columns = await ReadColumnsAsync(connection, table);
            Assert.All(requiredProvenance, column => Assert.Contains(column, columns));
        }

        var positionColumns = await ReadColumnsAsync(connection, "position_events");
        Assert.Contains("strategy_id", positionColumns);
        Assert.Contains("execution_strategy_id", positionColumns);
    }

    [Fact]
    public async Task InitializeAsync_RecognizedLegacyDatabase_PreservesRowsAndAddsProductionSchema()
    {
        await using var connection = await OpenInMemoryAsync();
        var wishlistId = Guid.NewGuid();

        await using (var legacyDb = CreateContext(connection))
        {
            var migrator = legacyDb.GetService<IMigrator>();
            await migrator.MigrateAsync(TradingFlowDatabaseInitializer.LegacyBaselineMigrationId);
            legacyDb.Wishlists.Add(new Wishlist
            {
                Id = wishlistId,
                Name = "preserved",
                IsDefault = true,
                IncludeExtendedHours = true,
                IsObserved = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await legacyDb.SaveChangesAsync();
            await legacyDb.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
        }

        await using (var upgradedDb = CreateContext(connection))
        {
            await new TradingFlowDatabaseInitializer().InitializeAsync(upgradedDb);

            var preserved = await upgradedDb.Wishlists.AsNoTracking().SingleAsync(item => item.Id == wishlistId);
            Assert.Equal("preserved", preserved.Name);
            Assert.True(preserved.IncludeExtendedHours);
            Assert.True(preserved.IsObserved);
            Assert.Empty(await upgradedDb.Database.GetPendingMigrationsAsync());
        }

        var tables = await ReadTablesAsync(connection);
        Assert.All(ProductionTables, table => Assert.Contains(table, tables));
        Assert.Equal(7, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
        var positionColumns = await ReadColumnsAsync(connection, "position_events");
        Assert.Contains("strategy_id", positionColumns);
        Assert.Contains("execution_strategy_id", positionColumns);
    }

    [Fact]
    public async Task DurableIntentMigration_BackfillsDistinctSequencesBeforeUniqueIndex()
    {
        await using var connection = await OpenInMemoryAsync();
        var runId = Guid.NewGuid();
        var firstIntentId = Guid.NewGuid();
        var secondIntentId = Guid.NewGuid();

        await using (var previousDb = CreateContext(connection))
        {
            var migrator = previousDb.GetService<IMigrator>();
            await migrator.MigrateAsync("20260721080737_ProductionJournalFoundation");
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO runs
                    (run_id, profile, status, started_at_utc, schema_version, config_hash, code_version)
                VALUES
                    ('{runId}', 'paper', 'running', '2026-07-21T13:00:00.0000000+00:00', 1, '{new string('a', 64)}', '{new string('b', 40)}');

                INSERT INTO order_intents
                    (intent_id, client_order_id, strategy_id, symbol, side, order_type, time_in_force,
                     requested_quantity, limit_price, stop_price, created_at_utc, request_json,
                     run_id, schema_version, config_hash, code_version)
                VALUES
                    ('{firstIntentId}', 'legacy-1', 'SWGA', 'MSFT', 'buy', 'limit', 'day',
                     '10', '100', '98', '2026-07-21T14:30:00.0000000+00:00', '[]',
                     '{runId}', 1, '{new string('a', 64)}', '{new string('b', 40)}'),
                    ('{secondIntentId}', 'legacy-2', 'SWGA', 'MSFT', 'buy', 'limit', 'day',
                     '10', '101', '99', '2026-07-21T14:35:00.0000000+00:00', '[]',
                     '{runId}', 1, '{new string('a', 64)}', '{new string('b', 40)}');
                """);
            await migrator.MigrateAsync();
        }

        await using var upgradedDb = CreateContext(connection);
        var intents = await upgradedDb.OrderIntents
            .AsNoTracking()
            .ToArrayAsync();
        intents = intents.OrderBy(intent => intent.CreatedAtUtc).ToArray();

        Assert.Equal([1, 2], intents.Select(intent => intent.SequenceNumber));
        Assert.All(intents, intent => Assert.Equal(new DateOnly(2026, 7, 21), intent.SessionDate));
        Assert.Empty(await upgradedDb.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task InitializeAsync_UnrecognizedPartialSchema_FailsWithoutCreatingMigrationHistory()
    {
        await using var connection = await OpenInMemoryAsync();
        await ExecuteAsync(connection, "CREATE TABLE Orders (OrderId TEXT NOT NULL PRIMARY KEY);");
        await using var db = CreateContext(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new TradingFlowDatabaseInitializer().InitializeAsync(db));

        Assert.Contains("does not match the recognized legacy schema", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", await ReadTablesAsync(connection));
    }

    [Fact]
    public void OperationalJournalModel_DoesNotUseBinaryFloatingPointNumbers()
    {
        var recordTypes = typeof(OperationalRecord).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(OperationalRecord).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal(10, recordTypes.Length);
        var defects = recordTypes
            .SelectMany(type => type.GetProperties().Select(property => (Type: type, Property: property)))
            .Where(item => item.Property.PropertyType == typeof(double)
                           || item.Property.PropertyType == typeof(double?)
                           || item.Property.PropertyType == typeof(float)
                           || item.Property.PropertyType == typeof(float?))
            .Select(item => $"{item.Type.Name}.{item.Property.Name}")
            .ToArray();

        Assert.Empty(defects);
    }

    [Fact]
    public async Task RelationalModel_DoesNotPersistBinaryFloatingPointNumbers()
    {
        await using var connection = await OpenInMemoryAsync();
        await using var db = CreateContext(connection);

        var defects = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => (Entity: entity, Property: property)))
            .Where(item => IsBinaryFloatingPoint(item.Property.ClrType))
            .Select(item => $"{item.Entity.ClrType.Name}.{item.Property.Name}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(defects);
    }

    [Fact]
    public void PublicMoneyPathContracts_DoNotExposeBinaryFloatingPointNumbers()
    {
        var assemblies = new[]
        {
            typeof(OperationalRecord).Assembly,
            typeof(SharedOrderRiskPlanner).Assembly,
            typeof(BacktestRunner).Assembly,
            typeof(AlpacaBrokerClient).Assembly,
            typeof(AlpacaManualOrderService).Assembly
        };
        var moneyTerms = new[]
        {
            "price", "cost", "fee", "profit", "loss", "pnl", "notional", "capital",
            "amount", "buyingpower", "cash", "equity", "stop", "target", "fill",
            "quantity", "units", "proceeds"
        };
        var defects = assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace?.StartsWith("TradingFlow", StringComparison.Ordinal) == true)
            .SelectMany(type =>
                type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Where(property => ContainsMoneyTerm(property.Name, moneyTerms))
                    .Select(property => (Name: $"{type.FullName}.{property.Name}", Type: property.PropertyType))
                    .Concat(type.GetConstructors()
                        .SelectMany(constructor => constructor.GetParameters())
                        .Where(parameter => ContainsMoneyTerm(parameter.Name, moneyTerms))
                        .Select(parameter => (Name: $"{type.FullName}.ctor({parameter.Name})", Type: parameter.ParameterType)))
                    .Concat(type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                        .Where(method => !method.IsSpecialName)
                        .SelectMany(method => method.GetParameters())
                        .Where(parameter => ContainsMoneyTerm(parameter.Name, moneyTerms))
                        .Select(parameter => (Name: $"{type.FullName}.{parameter.Member.Name}({parameter.Name})", Type: parameter.ParameterType))))
            .Where(item => IsBinaryFloatingPoint(item.Type))
            .Select(item => item.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(defects);
    }

    private static TradingFlowDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TradingFlowDbContext(options);
    }

    private static async Task<SqliteConnection> OpenInMemoryAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(1));
        }

        return values;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static bool ContainsMoneyTerm(string? value, IReadOnlyCollection<string> terms) =>
        value is not null && terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsBinaryFloatingPoint(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return underlyingType == typeof(double) || underlyingType == typeof(float);
    }
}
