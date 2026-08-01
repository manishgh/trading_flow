using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;

namespace TradingFlow.Tests;

public sealed class EarningsNewsRepositoryTests
{
    [Fact]
    public async Task RecentForTickers_ReturnsOnlyRequestedSymbolsWithinWindow()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteNewsFeedRepository(new TestDbContextFactory(options));
        var now = DateTimeOffset.Parse("2026-07-31T14:00:00Z");
        await repository.UpsertAsync(
        [
            CreateNews("MSFT", now.AddMinutes(-5), "msft"),
            CreateNews("NVDA", now.AddMinutes(-4), "nvda"),
            CreateNews("AAPL", now.AddMinutes(-3), "aapl"),
            CreateNews("MSFT", now.AddDays(-4), "stale")
        ], CancellationToken.None);

        var result = await repository.GetRecentForTickersAsync(
            now.AddDays(-3),
            5000,
            ["MSFT", "NVDA"],
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(["NVDA", "MSFT"], result.Select(item => item.Ticker).ToArray());
    }

    [Fact]
    public async Task Upsert_NormalizesPublishedAndReceivedTimestampsToUtc()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteNewsFeedRepository(new TestDbContextFactory(options));
        var published = DateTimeOffset.Parse("2026-07-31T18:00:00+05:30");
        var received = DateTimeOffset.Parse("2026-07-31T15:00:00+02:00");
        var item = CreateNews("MSFT", published, "utc-test") with { ReceivedAt = received };

        await repository.UpsertAsync([item], CancellationToken.None);
        await using var verification = new TradingFlowDbContext(options);
        var stored = await verification.NewsItems.SingleAsync();

        Assert.Equal(TimeSpan.Zero, stored.Timestamp.Offset);
        Assert.Equal(published.UtcDateTime, stored.Timestamp.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, stored.IngestedAt.Offset);
        Assert.Equal(received.UtcDateTime, stored.IngestedAt.UtcDateTime);
    }

    private static CatalystEvent CreateNews(string ticker, DateTimeOffset timestamp, string id) => new(
        ticker,
        timestamp,
        CatalystType.EarningsRelease,
        $"{ticker} reports earnings",
        0.5m,
        Provider: "alpaca",
        ExternalId: id);

    private sealed class TestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
