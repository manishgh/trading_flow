using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace TradingFlow.Data.Context;

/// <summary>
/// Applies versioned migrations and safely adopts the recognized pre-migration schema.
/// </summary>
public sealed class TradingFlowDatabaseInitializer
{
    public const string LegacyBaselineMigrationId = "20260721080221_LegacyOperationalBaseline";
    private const string EfProductVersion = "10.0.10";
    private const string HistoryTable = "__EFMigrationsHistory";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> LegacySchema =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DecisionAudits"] = Columns(
                "Id", "RunName", "Ticker", "StrategyName", "Timestamp", "Decision", "RejectionReason", "SignalJson"),
            ["Jobs"] = Columns(
                "Id", "RunName", "ConfigPath", "Status", "CreatedAt", "StartedAt", "FinishedAt", "ErrorMessage"),
            ["NewsItems"] = Columns(
                "Id", "Ticker", "Timestamp", "Headline", "SentimentScore", "Provider", "Source", "Url", "Summary", "IngestedAt"),
            ["Orders"] = Columns(
                "OrderId", "Ticker", "RunName", "ClientOrderId", "StrategyName", "Broker", "Status", "EntryPrice",
                "StopLossPrice", "TakeProfitPrice", "ShareQuantity", "CreatedAt", "UpdatedAt"),
            ["TickerLocks"] = Columns("Ticker", "PodId", "AcquiredAt", "ExpiresAt"),
            ["Wishlists"] = Columns(
                "Id", "Name", "Description", "IsDefault", "IncludeExtendedHours", "IsObserved", "CreatedAtUtc", "UpdatedAtUtc"),
            ["WishlistItems"] = Columns(
                "Id", "WishlistId", "Ticker", "DisplayName", "Notes", "Active", "AddedAtUtc"),
            ["WishlistSignals"] = Columns(
                "Id", "WishlistId", "Ticker", "SignalType", "Severity", "DetectedAtUtc", "Price", "Reason",
                "SnapshotJson", "NewsHeadline", "NewsUrl", "NewsProvider", "Acknowledged")
        };

    public async Task InitializeAsync(
        TradingFlowDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var connection = dbContext.Database.GetDbConnection();
        var openedHere = connection.State == ConnectionState.Closed;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var tables = await ReadTablesAsync(connection, cancellationToken);
            if (tables.Count > 0 && !tables.Contains(HistoryTable))
            {
                await AdoptLegacySchemaAsync(connection, tables, cancellationToken);
            }

            await dbContext.Database.MigrateAsync(cancellationToken);

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            if (pending.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Database initialization completed with pending migrations: {string.Join(", ", pending)}.");
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task AdoptLegacySchemaAsync(
        DbConnection connection,
        IReadOnlySet<string> tables,
        CancellationToken cancellationToken)
    {
        var defects = new List<string>();
        foreach (var expectedTable in LegacySchema)
        {
            if (!tables.Contains(expectedTable.Key))
            {
                defects.Add($"missing table {expectedTable.Key}");
                continue;
            }

            var actualColumns = await ReadColumnsAsync(connection, expectedTable.Key, cancellationToken);
            var missingColumns = expectedTable.Value
                .Where(column => !actualColumns.Contains(column))
                .OrderBy(column => column, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingColumns.Length > 0)
            {
                defects.Add($"{expectedTable.Key} missing columns [{string.Join(", ", missingColumns)}]");
            }
        }

        if (defects.Count > 0)
        {
            throw new InvalidOperationException(
                "Existing database does not match the recognized legacy schema; no migration was applied. " +
                string.Join("; ", defects));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """,
            cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($migration, $version);";
            AddParameter(insert, "$migration", LegacyBaselineMigrationId);
            AddParameter(insert, "$version", EfProductVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static IReadOnlySet<string> Columns(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}
