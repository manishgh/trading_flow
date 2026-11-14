using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>Owns the lifetime of the single reconciliation run created by one Web host.</summary>
public sealed class SqliteProductionHostRunRepository(IDbContextFactory<TradingFlowDbContext> contexts)
{
    // Register before order writers start. Renewal uses the same database clock as crash recovery.
    public async Task StartAsync(ProductionRun run, TimeSpan duration, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        run.HostLeaseExpiresAtUtcTicks = now.Add(duration).UtcTicks;
        context.ProductionRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        await CloseAbandonedAsync(context, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Expired ownership cannot be revived; reaping retries as neighboring leases expire.
    public async Task<bool> RenewAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        var nextExpiry = now.Add(duration).UtcTicks;
        var renewed = await context.ProductionRuns
            .Where(run => run.RunId == runId && run.Status == "running" && run.HostLeaseExpiresAtUtcTicks > now.UtcTicks)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.HostLeaseExpiresAtUtcTicks, nextExpiry), cancellationToken);
        await CloseAbandonedAsync(context, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return renewed == 1;
    }

    // Shutdown uses this host’s unique identity and never mutates another owner’s row.
    public async Task StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var now = await ProductionRunPersistence.ReadDatabaseTimeAsync(context, cancellationToken);
        await context.ProductionRuns
            .Where(run => run.RunId == runId && run.Status == "running" && run.HostLeaseExpiresAtUtcTicks != null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, "stopped")
                .SetProperty(run => run.FinishedAtUtc, now), cancellationToken);
    }

    // Only explicit ownership plus an expired lease establishes abandonment.
    private static Task<int> CloseAbandonedAsync(
        TradingFlowDbContext context, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.ProductionRuns
            .Where(run => run.Status == "running" && run.HostLeaseExpiresAtUtcTicks <= now.UtcTicks)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, "interrupted")
                .SetProperty(run => run.FinishedAtUtc, now), cancellationToken);

}
