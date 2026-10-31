using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Data.Orders;

/// <summary>Persist two small mode profiles in existing application events; admission reads them in its own write transaction.</summary>
public sealed class SqliteOperationalTradingLimitStore(IDbContextFactory<TradingFlowDbContext> contexts,
    TradingLimitDefaults? configuredDefaults = null) : IOperationalTradingLimitStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TradingLimitDefaults defaults = configuredDefaults ?? TradingLimitDefaultsFactory.FromRegistry();

    /// <summary>Read current saved choices or explicit server defaults without creating data on a read.</summary>
    public async Task<OperationalTradingLimits> GetAsync(string mode, CancellationToken token = default)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        return await ReadAsync(context, mode, defaults, token);
    }

    /// <summary>Append a reviewed version atomically; an outdated editor cannot overwrite a newer saved profile.</summary>
    public async Task<OperationalTradingLimits> SaveAsync(string mode, int maxNewStocksPerDay, int maxOpenPositions,
        long expectedRevision, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        mode = OperationalTradingLimitProfile.Mode(mode);
        TradingLimitDefaultsFactory.Validate(mode, maxNewStocksPerDay, maxOpenPositions);
        await using var context = await contexts.CreateDbContextAsync(token);
        await context.Database.OpenConnectionAsync(token);
        await using var transaction = ((SqliteConnection)context.Database.GetDbConnection())
            .BeginTransaction(IsolationLevel.Serializable, deferred: false);
        context.Database.UseTransaction(transaction);
        var current = await ReadAsync(context, mode, defaults, token);
        if (current.Revision != expectedRevision)
            throw new InvalidOperationException("Trading limits changed since this screen was loaded. Reload the settings before saving.");
        var value = new OperationalTradingLimits(mode, maxNewStocksPerDay, maxOpenPositions, 0, nowUtc.ToUniversalTime());
        var record = new ApplicationEventRecord
        {
            StreamName = OperationalTradingLimitProfile.Stream(mode), EventType = "trading.limits.saved",
            OccurredAtUtc = nowUtc.ToUniversalTime(), PayloadJson = JsonSerializer.Serialize(value, Json)
        };
        context.ApplicationEvents.Add(record);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return value with { Revision = record.EventSequence };
    }

    /// <summary>Use the same profile decoder inside reservation/dispatch transactions, so stale per-run options cannot bypass changes.</summary>
    internal static async Task<OperationalTradingLimits> ReadAsync(TradingFlowDbContext context, string mode,
        TradingLimitDefaults defaults, CancellationToken token)
    {
        mode = OperationalTradingLimitProfile.Mode(mode);
        var stream = OperationalTradingLimitProfile.Stream(mode);
        var record = await context.ApplicationEvents.AsNoTracking().Where(row => row.StreamName == stream)
            .OrderByDescending(row => row.EventSequence).FirstOrDefaultAsync(token);
        var value = record is null
            ? new OperationalTradingLimits(mode, defaults.MaxNewStocksPerDay, defaults.MaxOpenPositions, 0, null)
            : (JsonSerializer.Deserialize<OperationalTradingLimits>(record.PayloadJson, Json)
                ?? throw new InvalidDataException("Saved trading limits are empty.")) with { Revision = record.EventSequence };
        if (value.Mode != mode) throw new InvalidDataException("Saved trading limits identify a different execution mode.");
        TradingLimitDefaultsFactory.Validate(mode, value.MaxNewStocksPerDay, value.MaxOpenPositions);
        return value;
    }
}
