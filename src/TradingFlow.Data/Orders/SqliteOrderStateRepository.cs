using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;

namespace TradingFlow.Data.Orders;

public sealed class SqliteOrderStateRepository : IOrderStateRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> _contextFactory;

    public SqliteOrderStateRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task SaveOrderAsync(PersistedOrder order, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Orders.FirstOrDefaultAsync(x => x.OrderId == order.OrderId, cancellationToken);
        
        if (existing == null)
        {
            context.Orders.Add(order);
        }
        else
        {
            existing.Status = order.Status;
            existing.EntryPrice = order.EntryPrice;
            existing.StopLossPrice = order.StopLossPrice;
            existing.TakeProfitPrice = order.TakeProfitPrice;
            existing.ShareQuantity = order.ShareQuantity;
            existing.ClientOrderId = order.ClientOrderId;
            existing.StrategyName = order.StrategyName;
            existing.Broker = order.Broker;
            existing.RunName = order.RunName;
            existing.UpdatedAt = order.UpdatedAt;
        }
        
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateOrderStatusAsync(string orderId, string status, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Orders.FirstOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);
        if (existing != null)
        {
            existing.Status = status;
            existing.UpdatedAt = System.DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PersistedOrder>> GetActiveOrdersByTickerAsync(string ticker, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var openStatuses = new List<string>
        {
            "open",
            "new",
            "accepted",
            "partially_filled",
            "pending_exit_setup",
            "exit_submitted",
            "technical_exit_submitted"
        };
        
        var orders = await context.Orders
            .Where(x => x.Ticker == ticker)
            .ToListAsync(cancellationToken);
            
        return orders.Where(x => openStatuses.Contains(x.Status.ToLower())).ToList();
    }

    public async Task<PersistedOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Orders.FirstOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);
    }
}
