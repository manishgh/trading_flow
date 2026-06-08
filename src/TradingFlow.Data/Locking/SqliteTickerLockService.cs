using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Locking;

namespace TradingFlow.Data.Locking;

public sealed class SqliteTickerLockService : ITickerLockService
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteTickerLockService(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<bool> TryAcquireLockAsync(string ticker, string podId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        
        var now = DateTimeOffset.UtcNow;
        var lockEntity = await context.TickerLocks.FirstOrDefaultAsync(x => x.Ticker == ticker, cancellationToken);

        if (lockEntity == null)
        {
            lockEntity = new TickerLockEntity
            {
                Ticker = ticker,
                PodId = podId,
                AcquiredAt = now,
                ExpiresAt = now.Add(ttl)
            };
            context.TickerLocks.Add(lockEntity);
        }
        else
        {
            // If another pod holds it and it hasn't expired, deny lock.
            if (lockEntity.PodId != podId && lockEntity.ExpiresAt > now)
            {
                return false; // Locked by someone else
            }

            // Otherwise, we either already own it (refresh) or it expired (steal it).
            lockEntity.PodId = podId;
            lockEntity.AcquiredAt = now;
            lockEntity.ExpiresAt = now.Add(ttl);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Concurrency issue: another pod inserted it first
            return false;
        }
    }

    public async Task ReleaseLockAsync(string ticker, string podId, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var lockEntity = await context.TickerLocks.FirstOrDefaultAsync(x => x.Ticker == ticker && x.PodId == podId, cancellationToken);
        
        if (lockEntity != null)
        {
            context.TickerLocks.Remove(lockEntity);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
