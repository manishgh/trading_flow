using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class OrderOperationalTruthQueryTests
{
    [Fact]
    public async Task QueriesResolveRunIntentsAndCurrentPositionOwnerFromDurableJournals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var factory = new SharedContextFactory(options);
        var owningRunId = Guid.NewGuid();
        var otherRunId = Guid.NewGuid();
        const string entryClientOrderId = "owner-entry-client";
        context.ProductionRuns.AddRange(
            CreateRun(owningRunId),
            CreateRun(otherRunId));
        context.OrderIntents.AddRange(
            CreateIntent(owningRunId, entryClientOrderId, "MSFT"),
            CreateIntent(otherRunId, "other-entry-client", "AAPL"));
        context.OrderEvents.Add(new OrderEventRecord
        {
            ClientOrderId = entryClientOrderId,
            NewState = OrderState.Filled.ToStorageValue(),
            Source = "broker_stream",
            LocalTimestampUtc = DateTimeOffset.UtcNow,
            FilledQuantity = 12m,
            FillPrice = 100m,
            PayloadJson = "{}",
            RunId = owningRunId,
            ConfigHash = new string('a', 64),
            CodeVersion = "test"
        });
        context.PositionEvents.Add(new PositionEventRecord
        {
            AccountId = "paper-account",
            Symbol = "MSFT",
            StrategyId = "swing.test",
            ExecutionStrategyId = "swing.test",
            PositionGenerationEventId = 1,
            PositionGenerationClientOrderId = entryClientOrderId,
            QuantityAfter = 12m,
            FillQuantity = 12m,
            FillPrice = 100m,
            Side = "buy",
            BrokerOrderId = "broker-entry",
            ClientOrderId = entryClientOrderId,
            ExecutionId = "execution-1",
            Source = "broker_stream",
            BrokerTimestampUtc = DateTimeOffset.UtcNow,
            LocalTimestampUtc = DateTimeOffset.UtcNow,
            PayloadJson = "{}",
            RunId = owningRunId,
            ConfigHash = new string('a', 64),
            CodeVersion = "test"
        });
        await context.SaveChangesAsync();

        var intents = new SqliteOrderIntentRepository(factory);
        var positions = new SqlitePositionLedgerRepository(factory);
        var runIntents = await intents.ListByRunAsync(owningRunId);
        var runPositions = await positions.ListCurrentForRunAsync(owningRunId);
        var symbolOwners = await positions.ListCurrentOwnersForSymbolAsync("msft");

        Assert.Single(runIntents);
        Assert.Equal(entryClientOrderId, runIntents[0].ClientOrderId);
        var ownedPosition = Assert.Single(runPositions);
        Assert.Equal(owningRunId, ownedPosition.OwningRunId);
        Assert.Equal(12m, ownedPosition.Position.Quantity);
        Assert.Equal(entryClientOrderId, ownedPosition.Position.PositionGenerationClientOrderId);
        Assert.Equal(owningRunId, Assert.Single(symbolOwners).OwningRunId);
    }

    private static ProductionRun CreateRun(Guid runId) => new()
    {
        RunId = runId,
        Profile = "paper",
        Status = "running",
        StartedAtUtc = DateTimeOffset.UtcNow,
        ConfigHash = new string('a', 64),
        CodeVersion = "test"
    };

    private static OrderIntentRecord CreateIntent(Guid runId, string clientOrderId, string symbol) => new()
    {
        IntentId = Guid.NewGuid(),
        Kind = OrderIntentKind.StrategyEntry,
        ClientOrderId = clientOrderId,
        AccountId = "paper-account",
        StrategyId = "swing.test",
        Symbol = symbol,
        Side = "buy",
        OrderType = "limit",
        TimeInForce = "day",
        RequestedQuantity = 12m,
        LimitPrice = 100m,
        StopPrice = 98m,
        SessionDate = new DateOnly(2026, 9, 6),
        SequenceNumber = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        RequestJson = "{}",
        RunId = runId,
        ConfigHash = new string('a', 64),
        CodeVersion = "test"
    };

    private sealed class SharedContextFactory(DbContextOptions<TradingFlowDbContext> options) :
        IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);

        public Task<TradingFlowDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
