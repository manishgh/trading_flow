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
        Assert.All(ProductionTables, table => Assert.Contains(table, tables));
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var requiredProvenance = new[] { "run_id", "schema_version", "config_hash", "code_version" };
        foreach (var table in ProductionTables)
        {
            var columns = await ReadColumnsAsync(connection, table);
            Assert.All(requiredProvenance, column => Assert.Contains(column, columns));
        }
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
        Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
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

        Assert.Equal(9, recordTypes.Length);
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
            typeof(RiskEngine).Assembly,
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
