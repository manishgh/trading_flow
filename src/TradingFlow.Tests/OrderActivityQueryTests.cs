using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class OrderActivityQueryTests
{
    [Fact]
    public async Task ListRecentAsync_ReturnsLatestLifecycleStateWithIntentMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        var runId = Guid.NewGuid();

        await using (var context = new TradingFlowDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.ProductionRuns.Add(CreateRun(runId));
            context.OrderIntents.Add(CreateIntent(runId, "tf-order-1", "MSFT"));
            context.OrderEvents.AddRange(
                CreateEvent(runId, "tf-order-1", OrderState.Intent, 1),
                CreateEvent(runId, "tf-order-1", OrderState.Submitted, 2),
                CreateEvent(runId, "tf-order-1", OrderState.Filled, 3, 10m, 101.25m));
            await context.SaveChangesAsync();
        }

        var query = new SqliteOrderActivityQuery(new TestDbContextFactory(options));
        var item = Assert.Single(await query.ListRecentAsync());

        Assert.Equal("MSFT", item.Symbol);
        Assert.Equal("strategy-a", item.StrategyId);
        Assert.Equal(OrderState.Filled, item.State);
        Assert.Equal(10m, item.FilledQuantity);
        Assert.Equal(101.25m, item.FillPrice);
        Assert.Equal("broker_rest", item.EventSource);
    }

    [Fact]
    public async Task ListRecentAsync_FiltersByLatestStateAndBoundsLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        var runId = Guid.NewGuid();

        await using (var context = new TradingFlowDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.ProductionRuns.Add(CreateRun(runId));
            context.OrderIntents.AddRange(
                CreateIntent(runId, "tf-order-1", "MSFT"),
                CreateIntent(runId, "tf-order-2", "NVDA"));
            context.OrderEvents.AddRange(
                CreateEvent(runId, "tf-order-1", OrderState.Rejected, 1),
                CreateEvent(runId, "tf-order-2", OrderState.Filled, 2, 5m, 120m));
            await context.SaveChangesAsync();
        }

        var query = new SqliteOrderActivityQuery(new TestDbContextFactory(options));
        var rejected = Assert.Single(await query.ListRecentAsync(OrderState.Rejected, limit: 50));
        var bounded = await query.ListRecentAsync(limit: 1);

        Assert.Equal("MSFT", rejected.Symbol);
        Assert.Single(bounded);
        Assert.Equal("NVDA", bounded[0].Symbol);
    }

    private static ProductionRun CreateRun(Guid runId) => new()
    {
        RunId = runId,
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.Parse("2026-07-31T13:00:00Z"),
        ConfigHash = "config",
        CodeVersion = "test"
    };

    private static OrderIntentRecord CreateIntent(Guid runId, string clientOrderId, string symbol) => new()
    {
        IntentId = Guid.NewGuid(),
        RunId = runId,
        ClientOrderId = clientOrderId,
        StrategyId = "strategy-a",
        Symbol = symbol,
        Side = "buy",
        OrderType = "limit",
        TimeInForce = "day",
        RequestedQuantity = 10m,
        LimitPrice = 100m,
        StopPrice = 98m,
        SessionDate = new DateOnly(2026, 7, 31),
        SequenceNumber = clientOrderId.EndsWith('1') ? 1 : 2,
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-31T13:01:00Z"),
        RequestJson = "{}",
        ConfigHash = "config",
        CodeVersion = "test"
    };

    private static OrderEventRecord CreateEvent(
        Guid runId,
        string clientOrderId,
        OrderState state,
        int minute,
        decimal? filledQuantity = null,
        decimal? fillPrice = null) => new()
    {
        RunId = runId,
        ClientOrderId = clientOrderId,
        BrokerOrderId = "broker-1",
        NewState = state.ToStorageValue(),
        Source = "broker_rest",
        BrokerTimestampUtc = DateTimeOffset.Parse("2026-07-31T13:00:00Z").AddMinutes(minute),
        LocalTimestampUtc = DateTimeOffset.Parse("2026-07-31T13:00:00Z").AddMinutes(minute),
        FilledQuantity = filledQuantity,
        FillPrice = fillPrice,
        PayloadJson = "{}",
        ConfigHash = "config",
        CodeVersion = "test"
    };

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
