using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TradingFlow.Data.Context;

/// <summary>
/// Applies connection-scoped SQLite safety settings whenever EF opens a connection.
/// </summary>
public sealed class SqliteConnectionDurabilityInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SqliteDurability.ConfigureConnection(connection);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        SqliteDurability.ConfigureConnectionAsync(connection, cancellationToken);
}

internal static class SqliteDurability
{
    public const int BusyTimeoutMilliseconds = 5_000;

    /// <summary>
    /// Enables WAL for a file database and applies settings that must hold on the initializing connection.
    /// </summary>
    public static async Task ConfigureDatabaseAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var sqliteConnection = RequireSqlite(connection);
        await ConfigureConnectionAsync(sqliteConnection, cancellationToken);

        if (!IsInMemory(sqliteConnection))
        {
            var journalMode = await ExecuteScalarAsync(
                sqliteConnection,
                "PRAGMA journal_mode = WAL;",
                cancellationToken);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite refused WAL journal mode and returned '{journalMode ?? "null"}'.");
            }
        }
    }

    /// <summary>
    /// Applies settings that SQLite stores per connection rather than in the database file.
    /// </summary>
    public static void ConfigureConnection(DbConnection connection)
    {
        using var command = RequireSqlite(connection).CreateCommand();
        command.CommandText = ConnectionPragmas;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies settings that SQLite stores per connection rather than in the database file.
    /// </summary>
    public static async Task ConfigureConnectionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = RequireSqlite(connection).CreateCommand();
        command.CommandText = ConnectionPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string ConnectionPragmas =
        "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";

    private static SqliteConnection RequireSqlite(DbConnection connection) =>
        connection as SqliteConnection
        ?? throw new InvalidOperationException(
            $"TradingFlow persistence requires SQLite, but received {connection.GetType().FullName}.");

    private static bool IsInMemory(SqliteConnection connection)
    {
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        return builder.Mode == SqliteOpenMode.Memory
               || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }
}
