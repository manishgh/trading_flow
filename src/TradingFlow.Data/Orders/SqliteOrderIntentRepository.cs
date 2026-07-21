using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Commits an order intent as one append-only SQLite transaction.
/// </summary>
public sealed class SqliteOrderIntentRepository : IOrderIntentRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> contextFactory;

    public SqliteOrderIntentRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public async Task AppendAsync(
        OrderIntentRecord intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.OrderIntents.Add(intent);
        await context.SaveChangesAsync(cancellationToken);
    }
}
