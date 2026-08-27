using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Market;

namespace TradingFlow.Data.Market;

/// <summary>
/// Coordinates one active stream owner with atomic SQLite statements. A lease row
/// is never deleted because its fencing token must remain monotonic across releases.
/// </summary>
public sealed class SqliteMarketStreamLeaseRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory,
    TimeProvider? timeProvider = null) : IMarketStreamLeaseRepository
{
    private const int MaximumKeyLength = 200;
    private const int BusyRetryCount = 5;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<MarketStreamLease?> TryAcquireAsync(
        string resourceKey,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var key = ValidateIdentifier(resourceKey, nameof(resourceKey));
        var owner = ValidateIdentifier(ownerId, nameof(ownerId));
        ValidateDuration(leaseDuration);

        return ExecuteWithBusyRetryAsync(
            () => TryAcquireOnceAsync(key, owner, leaseDuration, cancellationToken),
            cancellationToken);
    }

    public Task<MarketStreamLease?> TryRenewAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var key = ValidateIdentifier(resourceKey, nameof(resourceKey));
        var owner = ValidateIdentifier(ownerId, nameof(ownerId));
        ValidateFencingToken(fencingToken);
        ValidateDuration(leaseDuration);

        return ExecuteWithBusyRetryAsync(
            () => TryRenewOnceAsync(key, owner, fencingToken, leaseDuration, cancellationToken),
            cancellationToken);
    }

    public Task<bool> TryReleaseAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        var key = ValidateIdentifier(resourceKey, nameof(resourceKey));
        var owner = ValidateIdentifier(ownerId, nameof(ownerId));
        ValidateFencingToken(fencingToken);

        return ExecuteWithBusyRetryAsync(
            () => TryReleaseOnceAsync(key, owner, fencingToken, cancellationToken),
            cancellationToken);
    }

    private async Task<MarketStreamLease?> TryAcquireOnceAsync(
        string resourceKey,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().ToUniversalTime();
        var expiresAtUtc = nowUtc.Add(leaseDuration);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO market_stream_leases
                (resource_key, owner_id, fencing_token, acquired_at_utc, renewed_at_utc, expires_at_utc)
            VALUES
                (@resource_key, @owner_id, 1, @now_utc, @now_utc, @expires_at_utc)
            ON CONFLICT(resource_key) DO UPDATE SET
                owner_id = excluded.owner_id,
                fencing_token = CASE
                    WHEN market_stream_leases.owner_id = excluded.owner_id
                         AND market_stream_leases.expires_at_utc > @now_utc
                    THEN market_stream_leases.fencing_token
                    ELSE market_stream_leases.fencing_token + 1
                END,
                acquired_at_utc = CASE
                    WHEN market_stream_leases.owner_id = excluded.owner_id
                         AND market_stream_leases.expires_at_utc > @now_utc
                    THEN market_stream_leases.acquired_at_utc
                    ELSE @now_utc
                END,
                renewed_at_utc = @now_utc,
                expires_at_utc = @expires_at_utc
            WHERE market_stream_leases.owner_id = excluded.owner_id
               OR market_stream_leases.expires_at_utc <= @now_utc
            RETURNING resource_key, owner_id, fencing_token,
                      acquired_at_utc, renewed_at_utc, expires_at_utc;
            """;
        AddText(command, "@resource_key", resourceKey);
        AddText(command, "@owner_id", ownerId);
        AddInteger(command, "@now_utc", ToUnixMilliseconds(nowUtc));
        AddInteger(command, "@expires_at_utc", ToUnixMilliseconds(expiresAtUtc));

        return await ReadLeaseAsync(command, cancellationToken);
    }

    private async Task<MarketStreamLease?> TryRenewOnceAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().ToUniversalTime();
        var expiresAtUtc = nowUtc.Add(leaseDuration);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE market_stream_leases
            SET renewed_at_utc = @now_utc,
                expires_at_utc = @expires_at_utc
            WHERE resource_key = @resource_key
              AND owner_id = @owner_id
              AND fencing_token = @fencing_token
              AND expires_at_utc > @now_utc
            RETURNING resource_key, owner_id, fencing_token,
                      acquired_at_utc, renewed_at_utc, expires_at_utc;
            """;
        AddText(command, "@resource_key", resourceKey);
        AddText(command, "@owner_id", ownerId);
        AddInteger(command, "@fencing_token", fencingToken);
        AddInteger(command, "@now_utc", ToUnixMilliseconds(nowUtc));
        AddInteger(command, "@expires_at_utc", ToUnixMilliseconds(expiresAtUtc));

        return await ReadLeaseAsync(command, cancellationToken);
    }

    private async Task<bool> TryReleaseOnceAsync(
        string resourceKey,
        string ownerId,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().ToUniversalTime();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE market_stream_leases
            SET renewed_at_utc = @now_utc,
                expires_at_utc = @now_utc
            WHERE resource_key = @resource_key
              AND owner_id = @owner_id
              AND fencing_token = @fencing_token
              AND expires_at_utc > @now_utc;
            """;
        AddText(command, "@resource_key", resourceKey);
        AddText(command, "@owner_id", ownerId);
        AddInteger(command, "@fencing_token", fencingToken);
        AddInteger(command, "@now_utc", ToUnixMilliseconds(nowUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<MarketStreamLease?> ReadLeaseAsync(
        System.Data.Common.DbCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MarketStreamLease(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            FromUnixMilliseconds(reader.GetInt64(3)),
            FromUnixMilliseconds(reader.GetInt64(4)),
            FromUnixMilliseconds(reader.GetInt64(5)));
    }

    private static async Task<T> ExecuteWithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException exception) when (
                attempt < BusyRetryCount && exception.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private static void AddText(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddInteger(System.Data.Common.DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.Int64;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > MaximumKeyLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value cannot exceed {MaximumKeyLength} characters.");
        }

        return normalized;
    }

    private static void ValidateDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                "Lease duration must be positive.");
        }
    }

    private static void ValidateFencingToken(long fencingToken)
    {
        if (fencingToken <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fencingToken),
                fencingToken,
                "Fencing token must be positive.");
        }
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);
}
