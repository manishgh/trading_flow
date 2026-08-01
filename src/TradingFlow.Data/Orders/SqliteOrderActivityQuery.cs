using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Builds an operator-facing order book from each intent and its latest immutable event.
/// </summary>
public sealed class SqliteOrderActivityQuery(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IOrderActivityQuery
{
    private const int MaximumLimit = 500;

    public async Task<IReadOnlyList<OrderActivitySnapshot>> ListRecentAsync(
        OrderState? state = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaximumLimit);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));

        var query =
            from intent in context.OrderIntents.AsNoTracking()
            join orderEvent in context.OrderEvents.AsNoTracking()
                    .Where(record => latestEventIds.Contains(record.EventId))
                on intent.ClientOrderId equals orderEvent.ClientOrderId
            select new { Intent = intent, Event = orderEvent };

        if (state is { } selectedState)
        {
            var storageState = selectedState.ToStorageValue();
            query = query.Where(item => item.Event.NewState == storageState);
        }

        var rows = await query
            .OrderByDescending(item => item.Event.EventId)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return rows.Select(item => new OrderActivitySnapshot(
                item.Intent.RunId,
                item.Intent.ClientOrderId,
                item.Event.BrokerOrderId,
                item.Intent.StrategyId,
                item.Intent.Symbol,
                item.Intent.Side,
                item.Intent.OrderType,
                item.Intent.TimeInForce,
                item.Intent.RequestedQuantity,
                item.Intent.LimitPrice,
                item.Intent.StopPrice,
                OrderStateMachine.ParseStorageValue(item.Event.NewState),
                item.Intent.CreatedAtUtc,
                item.Event.LocalTimestampUtc,
                item.Event.FilledQuantity,
                item.Event.FillPrice,
                item.Event.Source))
            .ToArray();
    }
}
