using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Forces committed SQLite WAL pages toward the main database before the
/// execution host relinquishes ownership. Individual journal writes are already
/// transactional; this is the final process-lifetime durability checkpoint.
/// </summary>
public sealed class SqliteExecutionJournalFlushService(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IExecutionJournalFlushService
{
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(FULL);";
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        if (!await result.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("SQLite did not return a WAL checkpoint result.");
        }

        var busy = result.GetInt32(0);
        var logPages = result.GetInt32(1);
        var checkpointedPages = result.GetInt32(2);
        if (busy != 0 || checkpointedPages < logPages)
        {
            throw new InvalidOperationException(
                $"SQLite WAL checkpoint was incomplete: busy={busy}, log_pages={logPages}, checkpointed_pages={checkpointedPages}.");
        }
    }
}
