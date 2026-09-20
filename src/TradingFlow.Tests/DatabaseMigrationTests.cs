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
        "protective_stop_replacements",
        "portfolio_risk_reservations",
        "gate_evaluations",
        "risk_events",
        "kill_switch_events",
        "reconciliations",
        "position_events",
        "candidates",
        "candidate_transitions",
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
        Assert.Contains("AspNetUsers", tables);
        Assert.Contains("AspNetRoles", tables);
        Assert.DoesNotContain("Orders", tables);
        Assert.All(ProductionTables, table => Assert.Contains(table, tables));
        Assert.Equal(
            db.Database.GetMigrations().Count(),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
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
        var reservationColumns = await ReadColumnsAsync(connection, "portfolio_risk_reservations");
        Assert.Contains("pending_quantity", reservationColumns);
        Assert.Contains("cumulative_filled_quantity", reservationColumns);
        Assert.Contains("open_position_quantity", reservationColumns);
        Assert.Contains("account_snapshot_requested_at_utc", reservationColumns);
        Assert.Contains("account_snapshot_observed_at_utc", reservationColumns);
        var orderIntentColumns = await ReadColumnsAsync(connection, "order_intents");
        Assert.Contains("position_generation_event_id", orderIntentColumns);
        var candidateColumns = await ReadColumnsAsync(connection, "candidates");
        Assert.Contains("strategy_content_sha256", candidateColumns);
        Assert.Contains("admission_profile_id", candidateColumns);
        Assert.Contains("setup_key", candidateColumns);
        Assert.Contains("discovery_window_start_utc", candidateColumns);
        Assert.Contains("discovery_window_end_utc", candidateColumns);
        Assert.Contains("semantic_decision_sha256", candidateColumns);
        var earningsAnalysisColumns = await ReadColumnsAsync(connection, "EarningsAnalysisSnapshots");
        Assert.Contains("EffectiveEpsActual", earningsAnalysisColumns);
        Assert.Contains("EffectiveRevenueActualMillions", earningsAnalysisColumns);
        Assert.Contains("ResultDataSource", earningsAnalysisColumns);
        var identityColumns = await ReadColumnsAsync(connection, "AspNetUsers");
        Assert.Contains("PasswordHash", identityColumns);
        Assert.DoesNotContain("Password", identityColumns);
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT \"unique\" FROM pragma_index_list('order_intents') WHERE name = 'IX_order_intents_candidate_id';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT partial FROM pragma_index_list('order_intents') WHERE name = 'IX_order_intents_candidate_id';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT \"unique\" FROM pragma_index_list('portfolio_risk_reservations') WHERE name = 'IX_portfolio_risk_reservations_account_id_symbol';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT partial FROM pragma_index_list('portfolio_risk_reservations') WHERE name = 'IX_portfolio_risk_reservations_account_id_symbol';"));
    }

    [Fact]
    public async Task RemoveLegacyOrdersTableMigration_DropsOnlyLegacyTableAndPreservesOtherData()
    {
        await using var connection = await OpenInMemoryAsync();
        var wishlistId = Guid.NewGuid();
        await using (var previousDb = CreateContext(connection))
        {
            var migrator = previousDb.GetService<IMigrator>();
            await migrator.MigrateAsync("20260906055954_AddStablePositionGeneration");
            previousDb.Wishlists.Add(new Wishlist
            {
                Id = wishlistId,
                Name = "migration-preserved",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await previousDb.SaveChangesAsync();
            await ExecuteAsync(
                connection,
                "INSERT INTO Orders (OrderId, Ticker, RunName, ClientOrderId, StrategyName, Broker, Status, EntryPrice, StopLossPrice, TakeProfitPrice, ShareQuantity, CreatedAt, UpdatedAt) " +
                "VALUES ('legacy-order', 'MSFT', 'legacy-run', 'legacy-client', 'legacy-strategy', 'alpaca', 'new', '100', '98', '104', 10, '2026-09-06T12:00:00+00:00', '2026-09-06T12:00:00+00:00');");
        }

        var tablesBefore = await ReadTablesAsync(connection);
        Assert.Contains("Orders", tablesBefore);

        await using (var upgradedDb = CreateContext(connection))
        {
            await upgradedDb.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(
                "migration-preserved",
                (await upgradedDb.Wishlists.AsNoTracking().SingleAsync(item => item.Id == wishlistId)).Name);
        }

        var tablesAfter = await ReadTablesAsync(connection);
        Assert.DoesNotContain("Orders", tablesAfter);
        tablesBefore.Remove("Orders");
        // Migrations after the legacy-order removal legitimately add new tables.
        // Compare the pre-existing schema only; dedicated fresh-database tests
        // verify that the newer application workflow tables are present.
        tablesAfter.Remove("application_events");
        tablesAfter.Remove("candidate_run_requests");
        tablesAfter.Remove("order_previews");
        tablesAfter.Remove("universe_previews");
        Assert.Equal(
            tablesBefore.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            tablesAfter.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
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
        await using var verificationDb = CreateContext(connection);
        Assert.Equal(
            verificationDb.Database.GetMigrations().Count(),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"));
        var positionColumns = await ReadColumnsAsync(connection, "position_events");
        Assert.Contains("strategy_id", positionColumns);
        Assert.Contains("execution_strategy_id", positionColumns);
        var earningsAnalysisColumns = await ReadColumnsAsync(connection, "EarningsAnalysisSnapshots");
        Assert.Contains("EffectiveEpsActual", earningsAnalysisColumns);
        Assert.Contains("EffectiveRevenueActualMillions", earningsAnalysisColumns);
        Assert.Contains("ResultDataSource", earningsAnalysisColumns);
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
            await migrator.MigrateAsync("20260831200123_EnforceAtomicCandidateOrderIntent");
        }

        Assert.Equal(
            1,
            await ScalarLongAsync(connection, "SELECT sequence_number FROM order_intents WHERE client_order_id = 'legacy-1';"));
        Assert.Equal(
            2,
            await ScalarLongAsync(connection, "SELECT sequence_number FROM order_intents WHERE client_order_id = 'legacy-2';"));
        Assert.Equal(
            "2026-07-21",
            await ScalarStringAsync(connection, "SELECT session_date FROM order_intents WHERE client_order_id = 'legacy-1';"));
    }

    [Fact]
    public async Task AccountScopeMigration_ExistingExecutionHistory_FailsInsteadOfGuessingAccountOwnership()
    {
        await using var connection = await OpenInMemoryAsync();
        var runId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        await using var previousDb = CreateContext(connection);
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
                 requested_quantity, limit_price, stop_price,
                 created_at_utc, request_json, run_id, schema_version, config_hash, code_version)
            VALUES
                ('{intentId}', 'legacy-unscoped', 'SWGA', 'MSFT', 'buy', 'limit', 'day',
                 '10', '100', '98',
                 '2026-07-21T14:30:00.0000000+00:00', '[]',
                 '{runId}', 1, '{new string('a', 64)}', '{new string('b', 40)}');
            """);
        await migrator.MigrateAsync("20260831200123_EnforceAtomicCandidateOrderIntent");

        await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync());

        var columns = await ReadColumnsAsync(connection, "order_intents");
        Assert.DoesNotContain("account_id", columns);
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM order_intents;"));
    }

    [Fact]
    public async Task CandidateLifecycleMigration_ExpiresLegacyCandidateAndMarksItsProvenanceExplicitly()
    {
        await using var connection = await OpenInMemoryAsync();
        var runId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        const string emptyJson = "{}";

        await using (var previousDb = CreateContext(connection))
        {
            var migrator = previousDb.GetService<IMigrator>();
            await migrator.MigrateAsync("20260827204546_AddDurableMarketState");
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO runs
                    (run_id, profile, status, started_at_utc, schema_version, config_hash, code_version)
                VALUES
                    ('{runId}', 'paper', 'running', '2026-08-27T13:00:00.0000000+00:00', 1, '{new string('a', 64)}', '{new string('b', 40)}');

                INSERT INTO candidates
                    (candidate_id, symbol, discovered_at_utc, revalidated_at_utc,
                     discovery_source, finviz_preset, horizon, setup_scores_json,
                     selected_strategy, state, reject_reasons_json,
                     run_id, schema_version, config_hash, code_version)
                VALUES
                    ('{candidateId}', 'MSFT', '2026-08-27T13:00:00.0000000+00:00',
                     '2026-08-27T13:05:00.0000000+00:00', 'legacy-screen', '', 'swing', '{emptyJson}',
                     'legacy-strategy', 'Armed', '[]', '{runId}', 1,
                     '{new string('a', 64)}', '{new string('b', 40)}');
                """);
            await migrator.MigrateAsync();
        }

        await using var upgradedDb = CreateContext(connection);
        var candidate = await upgradedDb.Candidates.AsNoTracking().SingleAsync();

        Assert.Equal(TradingFlow.Domain.Strategies.StrategyCandidateState.Expired, candidate.State);
        Assert.Equal("legacy-expired-history", candidate.AdmissionProfileId);
        Assert.Equal($"legacy:{candidateId}", candidate.SetupKey);
        Assert.Equal(new string('0', 64), candidate.StrategyContentSha256);
        Assert.Equal(new string('0', 64), candidate.SemanticDecisionSha256);
        Assert.Equal(candidate.DiscoveredAtUtc, candidate.DiscoveryWindowStartUtc);
        Assert.Equal(candidate.RevalidatedAtUtc, candidate.DiscoveryWindowEndUtc);
        Assert.Equal(candidate.RevalidatedAtUtc, candidate.ExpiresAtUtc);
        Assert.Empty(await upgradedDb.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task StablePositionGenerationMigration_BackfillsPartialFlatAndReopenedHistory()
    {
        await using var connection = await OpenInMemoryAsync();
        var runId = Guid.NewGuid();
        await using var previousDb = CreateContext(connection);
        var migrator = previousDb.GetService<IMigrator>();
        await migrator.MigrateAsync("20260905230210_AddDurableProtectiveStopReplacements");
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO runs
                (run_id, profile, status, started_at_utc, schema_version, config_hash, code_version)
            VALUES
                ('{runId}', 'paper', 'running', '2026-07-21T13:00:00.0000000+00:00',
                 1, '{new string('a', 64)}', '{new string('b', 40)}');

            INSERT INTO position_events
                (account_id, symbol, strategy_id, execution_strategy_id, quantity_after,
                 fill_quantity, fill_price, side, broker_order_id, client_order_id,
                 execution_id, source, broker_timestamp_utc, local_timestamp_utc,
                 payload_json, run_id, schema_version, config_hash, code_version)
            VALUES
                ('paper-account','MSFT','SWGA','SWGA','10','10','100','buy','b1','entry-a','e1','broker_rest','2026-07-21T13:01:00+00:00','2026-07-21T13:01:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}'),
                ('paper-account','MSFT','SWGA','SWGA','15','5','101','buy','b2','add-a','e2','broker_rest','2026-07-21T13:02:00+00:00','2026-07-21T13:02:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}'),
                ('paper-account','MSFT','SWGA','SWGA','0','15','102','sell','b3','flat-a','e3','broker_rest','2026-07-21T13:03:00+00:00','2026-07-21T13:03:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}'),
                ('paper-account','MSFT','SWGA','SWGA','-4','4','99','sell','b4','short-a','e4','broker_rest','2026-07-21T13:04:00+00:00','2026-07-21T13:04:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}'),
                ('paper-account','MSFT','SWGA','SWGA','0','4','98','buy','b5','flat-short','e5','broker_rest','2026-07-21T13:05:00+00:00','2026-07-21T13:05:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}'),
                ('paper-account','MSFT','SWGA','SWGA','7','7','103','buy','b6','entry-b','e6','broker_rest','2026-07-21T13:06:00+00:00','2026-07-21T13:06:00+00:00','[]','{runId}',1,'{new string('a', 64)}','{new string('b', 40)}');
            """);

        await migrator.MigrateAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT position_event_id, position_generation_event_id, position_generation_client_order_id " +
            "FROM position_events ORDER BY position_event_id;";
        var rows = new List<(long EventId, long GenerationId, string GenerationClientOrderId)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        Assert.Equal([1L, 1L, 1L, 4L, 4L, 6L], rows.Select(row => row.GenerationId));
        Assert.Equal(
            ["entry-a", "entry-a", "entry-a", "short-a", "short-a", "entry-b"],
            rows.Select(row => row.GenerationClientOrderId));
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

        Assert.Equal(13, recordTypes.Length);
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

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? String.Empty;
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
