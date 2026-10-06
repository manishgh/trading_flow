using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using TradingFlow.Data.News;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Preparation;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class NewsStockSourceTests
{
    private static readonly ClaimsPrincipal Operator = new(new ClaimsIdentity([new(ClaimTypes.Name, "operator")], "fixture"));

    [Fact]
    public async Task CatalogHasCombinedWishlistsAndEveryFinvizScreenWithoutProviderReads()
    {
        await using var f = await Fixture.CreateAsync();
        var choices = await f.Sources.CatalogAsync(default);
        Assert.Contains(choices, x => x.Key == "wishlists-finviz");
        Assert.Contains(choices, x => x.Key == "finviz");
        Assert.Contains(choices, x => x.Key == "wishlist:" + f.Core.Id);
        Assert.Contains(choices, x => x.Key == "saved:" + f.SavedId);
        Assert.Contains(choices, x => x.Key.StartsWith("signal:"));
        Assert.DoesNotContain(choices, x => x.Name == "Historical preparation");
        Assert.Equal(new[] { "My wishlists", "Finviz lists" }, choices.Where(x=>x.Group!="").Select(x=>x.Group).Distinct());
        f.Provider.VerifyNoOtherCalls();
        f.Account.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("wishlists", "AAPL,DUP,NVDA")]
    [InlineData("finviz", "DUP,F549")]
    [InlineData("wishlists-finviz", "AAPL,DUP,F549,NVDA")]
    public async Task AggregatesUseFullExactMembershipWithoutMarketInactivePreparationOrUnlistedHoldings(string key, string expected)
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Query.QueryAsync(new() { StockSource = key }, Operator, default);
        Assert.True(result.Enabled);
        Assert.Equal(expected.Split(','), result.Items.Select(x => x.Ticker).OrderBy(x => x).ToArray());
        Assert.Equal(expected.Split(',').Length, result.TotalMatching);
        f.Account.VerifyNoOtherCalls();
        if (key != "wishlists") Assert.Contains("Finviz returned 551", result.Message);
    }

    [Fact]
    public async Task IndividualWishlistAndLegacyLinksUseSameScopeAndTickerIntersection()
    {
        await using var f = await Fixture.CreateAsync();
        var individual = await f.Query.QueryAsync(new() { StockSource = "wishlist:" + f.Core.Id }, Operator, default);
        var legacy = await f.Query.QueryAsync(new() { GroupId = f.Core.Id }, Operator, default);
        Assert.Equal(new[] { "AAPL", "DUP" }, individual.Items.Select(x => x.Ticker).OrderBy(x => x));
        Assert.Equal(individual.TotalMatching, legacy.TotalMatching);
        var exact = await f.Query.QueryAsync(new() { StockSource = "wishlists-finviz", Ticker = " f549 " }, Operator, default);
        Assert.Equal("F549", Assert.Single(exact.Items).Ticker);
        Assert.Empty((await f.Query.QueryAsync(new() { StockSource = "wishlists", Ticker = "A" }, Operator, default)).Items);
        Assert.Empty((await f.Query.QueryAsync(new() { StockSource = "wishlists", Ticker = "F549" }, Operator, default)).Items);
        var publicStock = await f.Query.QueryAsync(new() { Ticker = "A" }, new(new ClaimsIdentity()), default);
        Assert.Equal("A", Assert.Single(publicStock.Items).Ticker);
    }

    [Fact]
    public async Task SavedAndCatalogScreenUseTheirOwnFiltersAndCacheFullMembershipAcrossPages()
    {
        await using var f = await Fixture.CreateAsync();
        var saved = await f.Query.QueryAsync(new() { StockSource = "saved:" + f.SavedId }, Operator, default);
        var page = await f.Query.QueryAsync(new() { StockSource = "saved:" + f.SavedId, Page = 9999, Search = "News" }, Operator, default);
        Assert.Equal(saved.TotalMatching, page.TotalMatching);
        Assert.Equal(1, page.Page);
        Assert.Contains(saved.Items, x => x.Ticker == "F549");
        f.Provider.Verify(x => x.PreviewAsync("ta_sma50_pa", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()), Times.Once);
        await f.Query.QueryAsync(new() { StockSource = "signal:ta_topgainers" }, Operator, default);
        f.Provider.Verify(x => x.PreviewAsync("s=ta_topgainers", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrIncompleteFinvizNeverFallsBackToWishlistsOrUnrestrictedNews(bool incomplete)
    {
        await using var f = await Fixture.CreateAsync();
        f.Result = incomplete ? f.Result with { AllSymbols = [], TotalCount = 551 }
            : ScreenerSyncResult.Failed(ScreenerScope.Swing, "screen", "query", "private credential detail");
        var result = await f.Query.QueryAsync(new() { StockSource = "wishlists-finviz" }, Operator, default);
        Assert.False(result.Enabled);
        Assert.Empty(result.Items);
        Assert.DoesNotContain("private credential detail", result.Message);
        Assert.NotEqual("Stored", result.Message);
    }

    [Fact]
    public async Task SuccessfulEmptyFinvizScopeIsEmptyRatherThanUnavailableOrAllNews()
    {
        await using var f = await Fixture.CreateAsync();
        f.Result = f.Result with { Symbols = [], AllSymbols = [], TotalCount = 0 };
        var result = await f.Query.QueryAsync(new() { StockSource = "finviz" }, Operator, default);
        Assert.True(result.Enabled);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalMatching);
    }

    [Fact]
    public async Task AnonymousPrivateScopesFailBeforeReadingAnyPrivateRepositoryOrProvider()
    {
        var groups = new Mock<IWishlistRepository>(MockBehavior.Strict);
        var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        var news = new Mock<INewsFeedRepository>(MockBehavior.Strict);
        news.Setup(x => x.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new NewsFeedPage([], 0));
        var service = new NewsQueryService(news.Object, groups.Object, account.Object, TimeProvider.System);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        foreach (var key in new[] { "finviz", "wishlists", "wishlists-finviz", "saved:" + Guid.NewGuid(), "positions" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueryAsync(new() { StockSource = key }, anonymous, default));
        Assert.True((await service.QueryAsync(new() { StockSource = "all" }, anonymous, default)).Enabled);
        groups.VerifyNoOtherCalls(); account.VerifyNoOtherCalls();
        news.Verify(x => x.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletedUnsupportedConflictingOrInvalidTickerCannotBroadenScopeOrReadProvider()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var query in new[] {
            new NewsHubQuery { StockSource = "wishlist:" + Guid.NewGuid() },
            new NewsHubQuery { StockSource = "saved:" + Guid.NewGuid() },
            new NewsHubQuery { StockSource = "typo" },
            new NewsHubQuery { StockSource = "finviz", GroupId = f.Core.Id },
            new NewsHubQuery { GroupId = f.Core.Id, Scope = "positions" },
            new NewsHubQuery { StockSource = "finviz", Ticker = "AAPL,MSFT" } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Query.QueryAsync(query, Operator, default));
        f.Provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentScreenReadsShareOneObservationAndCancellationDoesNotPoisonCache()
    {
        await using var f = await Fixture.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Provider.Setup(x => x.PreviewAsync("one", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()))
            .Returns(async (string _, ScreenerScope _, Guid? _, CancellationToken ct) => {
                started.SetResult(); await release.Task.WaitAsync(ct); return f.Result;
            });
        var first = f.Desk.ReadScreenAsync("one", default);
        await started.Task;
        var second = f.Desk.ReadScreenAsync("one", default);
        release.SetResult();
        await Task.WhenAll(first, second);
        f.Provider.Verify(x => x.PreviewAsync("one", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()), Times.Once);
        f.Provider.Setup(x => x.PreviewAsync("cancel", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()))
            .Returns(async (string _, ScreenerScope _, Guid? _, CancellationToken ct) => {
                await Task.Delay(Timeout.Infinite, ct); return f.Result;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Desk.ReadScreenAsync("cancel", cancellation.Token));
        f.Provider.Setup(x => x.PreviewAsync("cancel", ScreenerScope.Swing, null, It.IsAny<CancellationToken>())).ReturnsAsync(f.Result);
        Assert.True((await f.Desk.ReadScreenAsync("cancel", default)).Succeeded);
    }

    [Fact]
    public async Task ProviderTimeoutIsUnavailableAndBrieflyCachedWithoutStartingAnotherRead()
    {
        await using var f = await Fixture.CreateAsync();
        f.Provider.Setup(x => x.PreviewAsync("timeout", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()))
            .Returns(async (string _, ScreenerScope _, Guid? _, CancellationToken ct) => {
                await Task.Delay(Timeout.Infinite, ct); return f.Result;
            });
        Assert.False((await f.Desk.ReadScreenAsync("timeout", default)).Succeeded);
        Assert.False((await f.Desk.ReadScreenAsync("timeout", default)).Succeeded);
        f.Provider.Verify(x => x.PreviewAsync("timeout", ScreenerScope.Swing, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewsPageRejectsMalformedBoundGuidButDoesNotRejectUnboundResultValidation()
    {
        await using var f = await Fixture.CreateAsync();
        var model = new TradingFlow.Web.Pages.NewsModel(f.Query, f.Sources) {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        model.ModelState.AddModelError("groupId", "Invalid GUID");
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await model.OnGetAsync(default));
        model.ModelState.Clear();
        model.ModelState.AddModelError("FeedDescription", "Result not populated until OnGet");
        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await model.OnGetAsync(default));
    }

    [Fact]
    public async Task StockListUsesTheSameObservationShowsWishlistOverlapAndPagesFullMembership()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Query.QueryAsync(new() { StockSource = "wishlists-finviz" }, Operator, default);
        var selection = Assert.IsType<NewsStockSelection>(f.Query.StockSelection);
        var duplicate = Assert.Single(selection.Members, x=>x.Symbol=="DUP");
        Assert.Equal(new[] { "Core", "Growth" }, duplicate.Wishlists);
        Assert.True(duplicate.InFinviz);
        Assert.False(Assert.Single(selection.Members,x=>x.Symbol=="AAPL").InFinviz);
        var first = NewsStockSourceService.Page(selection,null,1);
        var last = NewsStockSourceService.Page(selection,null,999);
        Assert.Equal(100,first.Items.Count);
        Assert.Equal(553,first.Total);
        Assert.Equal(6,last.Page);Assert.Equal(53,last.Items.Count);
        Assert.Equal("F549",Assert.Single(NewsStockSourceService.Page(selection," f549 ",1).Items).Symbol);
        f.Provider.Verify(x=>x.PreviewAsync("ind_stocksonly",ScreenerScope.Swing,null,It.IsAny<CancellationToken>()),Times.Once);
        await f.Query.QueryAsync(new(), new(new ClaimsIdentity()), default);
        Assert.Null(f.Query.StockSelection);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CandidateWorkflowTestDatabase db = new();
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public Mock<IScreenerSnapshotSource> Provider { get; } = new(MockBehavior.Strict);
        public Mock<IDeskAccountReader> Account { get; } = new(MockBehavior.Strict);
        public ScreenerSyncResult Result { get; set; } = new(ScreenerScope.Swing, "Fixture", "ind_stocksonly", ["DUP"], [], DateTimeOffset.UtcNow, null, null) {
            AllSymbols = new[] { "DUP" }.Concat(Enumerable.Range(0, 550).Select(i => "F" + i)).ToArray(), TotalCount = 551 };
        public Wishlist Core { get; private set; } = null!;
        public Guid SavedId { get; private set; }
        public DeskSources Desk { get; private set; } = null!;
        public NewsStockSourceService Sources { get; private set; } = null!;
        public NewsQueryService Query { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); await f.db.InitializeAsync();
            var lists = new SqliteWishlistRepository(f.db);
            f.Core = await lists.SaveAsync(new() { Name = "Core", Items = new[] { "AAPL", "DUP", "INACTIVE" }.Select(x => new WishlistItem { Ticker = x, Active = x != "INACTIVE" }).ToList() }, default);
            await lists.SaveAsync(new() { Name = "Growth", Items = new[] { "NVDA", "DUP" }.Select(x => new WishlistItem { Ticker = x, Active = true }).ToList() }, default);
            var historical = await lists.SaveAsync(new() { Name = "Historical preparation", Items = [new() { Ticker = "OLD", Active = true }] }, default);
            await using (var context = f.db.CreateDbContext()) {
                var definition = new PreparationDefinitionRecord { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), OwnerId = "fixture", SettingsJson = "{}" };
                var occurrence = new PreparationOccurrenceRecord { Id = Guid.NewGuid(), DefinitionId = definition.Id, WorkflowId = definition.WorkflowId, SlotKey = "fixture", SlotJson = "{}" };
                context.PreparationDefinitions.Add(definition); context.PreparationOccurrences.Add(occurrence);
                context.PreparationReviews.Add(new() { Id = Guid.NewGuid(), OccurrenceId = occurrence.Id, WishlistId = historical.Id, OwnerId = "fixture", IdempotencyKey = "one", RequestSha256 = "hash", Json = "{}" });
                context.NewsItems.AddRange(new[] { "AAPL", "DUP", "NVDA", "F549", "A", "INACTIVE", "OLD", "HELD", "MARKET" }.Select(x => new PersistedNewsItem {
                    Id = x, Ticker = x, Headline = "News " + x, Timestamp = DateTimeOffset.UtcNow, IngestedAt = DateTimeOffset.UtcNow, Provider = "wire" }));
                await context.SaveChangesAsync();
            }
            var presets = new ScreenerPresetService(f.db, TimeProvider.System);
            f.SavedId = (await presets.SaveAsync("Saved filter", ScreenerScope.Swing, "ta_sma50_pa", default)).Id;
            f.Provider.Setup(x => x.PreviewAsync(It.IsAny<string>(), ScreenerScope.Swing, null, It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(f.Result));
            f.Desk = new(f.db, lists, presets, f.Provider.Object, f.cache);
            f.Sources = new(f.Desk);
            f.Query = new(new SqliteNewsFeedRepository(f.db), lists, f.Account.Object, TimeProvider.System, f.Sources);
            return f;
        }
        public async ValueTask DisposeAsync() { cache.Dispose(); await db.DisposeAsync(); }
    }
}
