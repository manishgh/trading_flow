using Moq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;

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

    // Exercise the complete refresh while provider requests fail and retained news remains readable.
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
        var clock = new FixedNewsClock(now);
        using var finviz = new FinvizClient(new HttpClient(new OfflineNewsHandler()),
            FinvizOptions.CreateDefault() with { AuthToken = "fixture" },
            Mock.Of<IRawArchiveWriter>(MockBehavior.Strict));
        using var publicFeeds = new HttpClient(new OfflineNewsHandler());
        var official = new OfficialMarketNewsProvider(publicFeeds, NullLogger<OfficialMarketNewsProvider>.Instance, clock);
        var alpaca = new AlpacaCredentialProvider(new ConfigurationBuilder().Build(), _ => null);
        var service = new NewsFeedService(null!, null!, null!, repository,
            null!, alpaca, null!, earnings.Object, official, finviz, clock, NullLogger<NewsFeedService>.Instance);
        await service.RefreshOnceAsync(CancellationToken.None);
        await service.RefreshOnceAsync(CancellationToken.None);

        var status = service.GetRefreshStatus();
        Assert.Equal("failed", status.State);
        Assert.Contains(status.Sources, source => source is { Source: "Finviz stocks", State: "failed" });
        Assert.Contains(status.Sources, source => source is { Source: "Federal Reserve", State: "failed" });
        var visible = await repository.GetRecentAsync(now.AddHours(-72), 200, null, CancellationToken.None);
        Assert.Equal("NVDA", Assert.Single(visible).Ticker);
        await using var verification = new TradingFlowDbContext(options);
        Assert.Equal(2, await verification.NewsItems.CountAsync());
        var retained = await verification.NewsItems.SingleAsync(x => x.Ticker == "MSFT");
        Assert.Equal(original.Timestamp, retained.Timestamp);
        Assert.Equal(original.ReceivedAt, retained.IngestedAt);
        Assert.Equal(original.Headline, retained.Headline);
    }

    // Simulate an offline provider without consulting ambient credentials or network.
    private sealed class OfflineNewsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Provider connection unavailable."));
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
