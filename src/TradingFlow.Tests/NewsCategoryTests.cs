using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using TradingFlow.Data.Context;
using TradingFlow.Data.News;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class NewsCategoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<TradingFlowDbContext> options;
    private readonly SqliteNewsFeedRepository repository;
    public NewsCategoryTests()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        using var db = new TradingFlowDbContext(options);
        db.Database.EnsureCreated();
        repository = new(new Factory(options));
    }

    [Fact]
    public async Task SqlFiltersAndDisplayedTopicsAgreeForEveryCategoryAndSector()
    {
        var rows = new[] {
            Item("conflict", "MARKET", "War in Ukraine disrupts oil shipping; tariffs raise inflation"),
            Item("company", "AAA", "Software earnings rise", "Bank credit supports healthcare acquisition"),
            Item("summary", "MARKET", "Morning update", "Federal Reserve policy changes interest rates"),
            Item("punctuation", "MARKET", "(Semiconductors), real-estate and utility updates"),
            Item("unmatched", "MARKET", "Awards ceremony preview"),
            Item("empty", "MARKET", ""),
            Item("fed", "MARKET", "Statement"),
            Item("filing", "BBB", "Filing"),
            Item("un", "MARKET", "Public briefing"),
            Item("government", "MARKET", "Presidential document")
        };
        rows[6].Provider = "federal_reserve"; rows[7].Provider = "sec_edgar";
        rows[8].Provider = "un_news"; rows[9].Provider = "us_government";
        await Seed(rows);
        foreach (var category in NewsDisplayTaxonomy.Categories) {
            var page = await repository.QueryAsync(new(Now.AddHours(-4), Category: category.Key), default);
            var expected = rows.Where(x => category.Key == "all" || NewsDisplayTaxonomy.Describe(x).Categories.Contains(category.Key)).Select(x => x.Id).OrderBy(x => x);
            Assert.Equal(expected, page.Records.Select(x => x.Id).OrderBy(x => x));
            Assert.Equal(expected.Count(), page.TotalMatching);
        }
        foreach (var sector in NewsDisplayTaxonomy.Sectors) {
            var page = await repository.QueryAsync(new(Now.AddHours(-4), Sector: sector.Key), default);
            Assert.Equal(rows.Where(x => NewsDisplayTaxonomy.Describe(x).Sectors.Contains(sector.Key)).Select(x => x.Id).OrderBy(x => x),
                page.Records.Select(x => x.Id).OrderBy(x => x));
        }
        Assert.DoesNotContain("global", NewsDisplayTaxonomy.Describe(rows[4]).Categories);
        Assert.Contains("global", NewsDisplayTaxonomy.Describe(rows[0]).Categories);
        Assert.Contains("policy", NewsDisplayTaxonomy.Describe(rows[0]).Categories);
        Assert.Contains("financial", NewsDisplayTaxonomy.Describe(rows[0]).Categories);
        Assert.Contains("sector", NewsDisplayTaxonomy.Describe(rows[0]).Categories);
        Assert.Contains("global", NewsDisplayTaxonomy.Describe(rows[8]).Categories);
        Assert.Contains("policy", NewsDisplayTaxonomy.Describe(rows[9]).Categories);
    }

    [Fact]
    public async Task CategorySectorAndExistingDimensionsFilterBeforeCountAndStablePagination()
    {
        var rows = Enumerable.Range(0, 305).Select(i => {
            var row = Item(i.ToString("D4"), "AAA", i >= 300 ? "Tariffs on semiconductor chips affect earnings" : "Ordinary update");
            row.Timestamp = Now.AddSeconds(-i); row.Provider = i >= 300 ? "wire" : "other";
            row.Source = "fixture"; row.Url = "https://example.test/story"; row.SentimentScore = 0.4m;
            return row;
        }).ToArray();
        await Seed(rows);
        var query = new NewsFeedQuery(Now.AddHours(-4), Provider: "wire", Source: "fixture", Sentiment: "positive",
            Search: "earnings", LinkedOnly: true, Take: 2, Category: "policy", Sector: "technology");
        var first = await repository.QueryAsync(query, default);
        var second = await repository.QueryAsync(query with { Skip = 2 }, default);
        var last = await repository.QueryAsync(query with { Skip = 4 }, default);
        Assert.Equal(5, first.TotalMatching); Assert.Equal(5, second.TotalMatching); Assert.Equal(5, last.TotalMatching);
        Assert.Equal(new[] { "0300", "0301", "0302", "0303", "0304" }, first.Records.Concat(second.Records).Concat(last.Records).Select(x => x.Id));
    }

    [Fact]
    public async Task BroadContextRequiresOptInAndExplicitTickerOrLinkedScopeRemainsExact()
    {
        var rows = new[] { Item("market", "MARKET", "War raises oil prices"), Item("aaa", "AAA", "War raises oil prices"), Item("bbb", "BBB", "War raises oil prices") };
        foreach (var item in rows) item.Url = "https://example.test/news";
        await Seed(rows);
        var scoped = new NewsFeedQuery(Now.AddHours(-4), ScopeTickers: ["AAA"], Category: "global");
        Assert.Equal(new[] { "aaa" }, (await repository.QueryAsync(scoped, default)).Records.Select(x => x.Id));
        Assert.Equal(new[] { "aaa", "market" }, (await repository.QueryAsync(scoped with { IncludeMarketNews = true }, default)).Records.Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(new[] { "aaa" }, (await repository.QueryAsync(scoped with { IncludeMarketNews = true, Ticker = "AAA" }, default)).Records.Select(x => x.Id));
        Assert.Equal(new[] { "aaa", "market" }, (await repository.QueryAsync(scoped with { IncludeMarketNews = true, LinkedOnly = true }, default)).Records.Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(new[] { "aaa" }, (await repository.QueryAsync(scoped with { IncludeMarketNews = true, StockLinkedOnly = true }, default)).Records.Select(x => x.Id));
        Assert.Empty((await repository.QueryAsync(scoped with { ScopeTickers = [] }, default)).Records);
        Assert.Equal("market", Assert.Single((await repository.QueryAsync(scoped with { ScopeTickers = [], IncludeMarketNews = true }, default)).Records).Id);
        Assert.Empty((await repository.QueryAsync(scoped with { ScopeTickers = [], IncludeMarketNews = true, Ticker = "AAA" }, default)).Records);
        Assert.Empty((await repository.QueryAsync(scoped with { ScopeTickers = [], IncludeMarketNews = true, StockLinkedOnly = true }, default)).Records);
    }

    [Fact]
    public async Task ClockBoundsExcludeOldAndFutureRowsAndCategoryMetadataSurvivesDuplicateGrouping()
    {
        var first = Item("first", "AAA", "Shared story", "Semiconductor earnings");
        var second = Item("second", "BBB", "Shared story", "War triggers government sanctions on oil");
        var old = Item("old", "CCC", "Old policy"); old.Timestamp = Now.AddHours(-5);
        var future = Item("future", "DDD", "Future policy"); future.Timestamp = Now.AddSeconds(1);
        await Seed(first, second, old, future);
        var groups = new Mock<IWishlistRepository>(MockBehavior.Strict);
        var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        var service = new NewsQueryService(repository, groups.Object, account.Object, new FixedClock());
        var response = await service.QueryAsync(new() { Category = "all" }, new(new ClaimsIdentity()), default);
        Assert.Equal(2, response.TotalMatching);
        var item = Assert.Single(response.Items);
        Assert.Equal("AAA, BBB", item.Ticker);
        Assert.Contains("global", item.Categories!); Assert.Contains("company", item.Categories!);
        Assert.Contains("technology", item.Sectors!); Assert.Contains("energy", item.Sectors!);
        Assert.Equal(NewsDisplayTaxonomy.Version, item.CategoryVersion);
        Assert.Contains("headline/summary", item.CategoryRationale);
        groups.VerifyNoOtherCalls(); account.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PrivateCategoryScopesRequireAuthenticationBeforeMembershipOrAccountReads()
    {
        var groups = new Mock<IWishlistRepository>(MockBehavior.Strict);
        var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        var news = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        var service = new NewsQueryService(news.Object, groups.Object, account.Object, new FixedClock());
        foreach (var source in new[] { "positions", "wishlists", "finviz", "wishlists-finviz", "wishlist:" + Guid.NewGuid(), "saved:" + Guid.NewGuid() })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueryAsync(new() { Category = "global", StockSource = source }, new(new ClaimsIdentity()), default));
        groups.VerifyNoOtherCalls(); account.VerifyNoOtherCalls(); news.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("global", "unknown")]
    public async Task UnknownCategoryAndSectorAreRejectedWithoutRepositoryReads(string category, string? sector)
    {
        var service = new NewsQueryService(new Mock<INewsFeedRepository>(MockBehavior.Strict).Object,
            new Mock<IWishlistRepository>(MockBehavior.Strict).Object, new Mock<IDeskAccountReader>(MockBehavior.Strict).Object, new FixedClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryAsync(new() { Category = category, Sector = sector }, new(new ClaimsIdentity()), default));
    }

    [Fact]
    public void MissingTextAndProviderMetadataAreSafeAndTradingClassificationIsUnchanged()
    {
        var topics = NewsDisplayTaxonomy.Describe(new() { Headline = null!, Summary = null, Ticker = null!, Provider = null! });
        Assert.Equal(new[] { "market" }, topics.Categories); Assert.Empty(topics.Sectors);
        var company = Item("company", "AAA", "War affects banks and inflation");
        Assert.Equal("Company", NewsEvidenceClassification.Classify(company));
        Assert.Contains("global", NewsDisplayTaxonomy.Describe(company).Categories);
    }

    private async Task Seed(params PersistedNewsItem[] rows)
    { using var db = new TradingFlowDbContext(options); db.NewsItems.AddRange(rows); await db.SaveChangesAsync(); }
    private static PersistedNewsItem Item(string id, string ticker, string headline, string? summary = null) =>
        new() { Id = id, Ticker = ticker, Headline = headline, Summary = summary, Timestamp = Now.AddMinutes(-1), IngestedAt = Now, Provider = "wire" };
    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    { public TradingFlowDbContext CreateDbContext() => new(options); }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    public void Dispose() => connection.Dispose();
}
