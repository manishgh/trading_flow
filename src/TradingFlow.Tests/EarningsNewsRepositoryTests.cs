using Moq;
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

    [Fact]
    public async Task RefreshDuringProviderOutage_PreservesNewsOutsideTheDisplayWindow()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingFlowDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var repository = new SqliteNewsFeedRepository(new TestDbContextFactory(options));
        var now = DateTimeOffset.Parse("2026-11-15T13:00:00Z");
        var original = CreateNews("MSFT", now.AddDays(-10), "original-stream-receipt") with
        {
            ReceivedAt = now.AddDays(-10).AddSeconds(3)
        };
        await repository.UpsertAsync([original, CreateNews("NVDA", now.AddMinutes(-5), "recent")], CancellationToken.None);
        var earnings = new Moq.Mock<TradingFlow.Domain.Earnings.IEarningsRepository>();
        earnings.Setup(x => x.GetCalendarAsync(Moq.It.IsAny<DateOnly>(), Moq.It.IsAny<DateOnly>(),
            Moq.It.IsAny<IReadOnlyCollection<string>?>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TradingFlow.Domain.Earnings.EarningsCalendarEvent>());
        // Provider dependencies are unavailable deliberately: refresh tolerates their failure,
        // makes no network request, and still exercises the original automatic-expiry path.
        var service = new TradingFlow.Web.Services.NewsFeedService(null!, null!, null!, repository,
            null!, null!, null!, earnings.Object, null!, new FixedNewsClock(now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingFlow.Web.Services.NewsFeedService>.Instance);
        await service.RefreshOnceAsync(CancellationToken.None);
        await service.RefreshOnceAsync(CancellationToken.None);

        var visible = await repository.GetRecentAsync(now.AddHours(-72), 200, null, CancellationToken.None);
        Assert.Equal("NVDA", Assert.Single(visible).Ticker);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(2, await verification.NewsItems.CountAsync());
        var retained = await verification.NewsItems.SingleAsync(x => x.Ticker == "MSFT");
        Assert.Equal(original.Timestamp, retained.Timestamp);
        Assert.Equal(original.ReceivedAt, retained.IngestedAt);
        Assert.Equal(original.Headline, retained.Headline);
    }

    private sealed class FixedNewsClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
