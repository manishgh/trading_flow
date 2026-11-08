using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Data.News;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Preparation;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class DeskReadModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T15:00:00Z");
    [Fact]
    public void AllFollowed_DeduplicatesAndKeepsEverySource_WithoutChangingObservation()
    {
        var first = List("Core", "aapl", "MSFT"); first.IsObserved = true;
        var second = List("Growth", "AAPL", "NVDA"); second.Items.Add(new(){Ticker="TSLA",Active=false});
        var rows=DeskSources.Merge([first,second]);
        Assert.Equal(["AAPL","MSFT","NVDA"],rows.Select(x=>x.Symbol));
        Assert.Equal(2,rows[0].Wishlists.Count);Assert.Single(rows[0].Wishlists,x=>x.Observed);Assert.False(second.IsObserved);
    }
    [Fact]
    public async Task CatalogAndResolution_ExcludeHistoricalPreparation_IncludeScreenerOnlySymbols_AndDoNotWrite()
    {
        await using var db=new CandidateWorkflowTestDatabase();await db.InitializeAsync();
        var repository=new SqliteWishlistRepository(db);
        var first=await repository.SaveAsync(List("Core","AAPL"),default);
        var historical=await repository.SaveAsync(List("Preparation revision","OLD"),default);
        await using(var context=db.CreateDbContext()) {
            var definition=new PreparationDefinitionRecord{Id=Guid.NewGuid(),WorkflowId=Guid.NewGuid(),OwnerId="fixture",SettingsJson="{}"};
            var occurrence=new PreparationOccurrenceRecord{Id=Guid.NewGuid(),DefinitionId=definition.Id,WorkflowId=definition.WorkflowId,SlotKey="fixture",SlotJson="{}"};
            context.PreparationDefinitions.Add(definition);context.PreparationOccurrences.Add(occurrence);
            context.PreparationReviews.Add(new(){Id=Guid.NewGuid(),OccurrenceId=occurrence.Id,WishlistId=historical.Id,OwnerId="fixture",IdempotencyKey="one",RequestSha256="hash",Json="{}"});await context.SaveChangesAsync(); }
        var screener=new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screener.Setup(x=>x.PreviewAsync(It.IsAny<string>(),ScreenerScope.Swing,null,It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenerSyncResult(ScreenerScope.Swing,"test","test",["AAPL","NVDA"],[],Now,null,null){TotalCount=302});
        using var cache=new MemoryCache(new MemoryCacheOptions());
        var presets=new ScreenerPresetService(db,new CandidateWorkflowTestClock(Now));
        await presets.SaveAsync("My saved screen",ScreenerScope.Swing,"ta_sma50_pa",default);
        var sources=new DeskSources(db,repository,presets,screener.Object,cache);
        var catalog=await sources.CatalogAsync(default);
        Assert.Contains(catalog,x=>x.Group=="Saved Elite screens");Assert.Contains(catalog,x=>x.Group=="TradingFlow swing screens");
        Assert.DoesNotContain(catalog,x=>x.Key.Contains(historical.Id.ToString()));
        Assert.Equal("AAPL",Assert.Single((await sources.ResolveAsync("all",default)).Members).Symbol);
        var screen=await sources.ResolveAsync("swing:trend",default);
        Assert.Equal(302,screen.TotalCount);Assert.Equal(2,screen.Members.Count);
        Assert.Empty(screen.Members.Single(x=>x.Symbol=="NVDA").Wishlists);
        Assert.Equal(first.Id,Assert.Single(screen.Members.Single(x=>x.Symbol=="AAPL").Wishlists).Id);
        await sources.ResolveAsync("swing:trend",default);
        screener.Verify(x=>x.PreviewAsync(It.IsAny<string>(),ScreenerScope.Swing,null,It.IsAny<CancellationToken>()),Times.Once);
        Assert.Equal(2,(await repository.ListAsync(default)).Count);
        Assert.False((await repository.GetByIdAsync(first.Id,default))!.IsObserved);
        await Assert.ThrowsAsync<ArgumentException>(()=>sources.ResolveAsync("wishlist:"+historical.Id,default));
        await repository.AddOrUpdateItemAsync(first.Id,"MSFT",null,null,default);
        Assert.Equal(2,(await sources.ResolveAsync("all",default)).Members.Count);
        await repository.AddOrUpdateItemAsync(first.Id,"AAPL","Apple","Keep my note",default);
        await sources.FollowAsync("all","AAPL",first.Id,default);
        Assert.Equal("Keep my note",(await repository.GetByIdAsync(first.Id,default))!.Items.Single(x=>x.Ticker=="AAPL").Notes);
        await sources.FollowAsync("swing:trend","NVDA",first.Id,default);
        await sources.FollowAsync("swing:trend","NVDA",first.Id,default);
        Assert.Single((await repository.GetByIdAsync(first.Id,default))!.Items,x=>x.Ticker=="NVDA");
        await Assert.ThrowsAsync<ArgumentException>(()=>sources.FollowAsync("all","NOTINSOURCE",first.Id,default));
        await Assert.ThrowsAsync<ArgumentException>(()=>sources.FollowAsync("all","AAPL",historical.Id,default));

    }
    [Fact]
    public async Task FailedScreener_IsNotASuccessfulEmptyResult()
    {
        await using var db=new CandidateWorkflowTestDatabase();await db.InitializeAsync();
        var screener=new Mock<IScreenerSnapshotSource>();screener.Setup(x=>x.PreviewAsync(It.IsAny<string>(),ScreenerScope.Swing,null,It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScreenerSyncResult.Failed(ScreenerScope.Swing,"screen","query","credential-related provider detail"));
        using var cache=new MemoryCache(new MemoryCacheOptions());
        var sources=new DeskSources(db,new SqliteWishlistRepository(db),new ScreenerPresetService(db,TimeProvider.System),screener.Object,cache);
        var result=await sources.ResolveAsync("swing:highs",default);Assert.NotNull(result.Error);Assert.Empty(result.Members);Assert.DoesNotContain("credential-related",result.Error);
    }
    [Fact]
    public void SearchAndPaging_ReachSymbolsBeyondTheFirst250()
    {
        var members=Enumerable.Range(0,310).Select(i=>new DeskMembership("STOCK"+i.ToString("D3"),null,[])).ToArray();
        var source=DeskSources.BuiltIns[0];
        var first=DeskSources.Window(source,members,members.Length,null,null,0,"");Assert.Equal(50,first.Members.Count);Assert.Equal(7,first.PageCount);
        var last=DeskSources.Window(source,members,members.Length,null,null,6,"");Assert.Equal(10,last.Members.Count);Assert.Equal("STOCK309",last.Members.Last().Symbol);
        var searched=DeskSources.Window(source,members,members.Length,null,null,0,"STOCK309");Assert.Equal("STOCK309",Assert.Single(searched.Members).Symbol);Assert.Equal(1,searched.MatchedCount);
    }
    [Theory]
    [InlineData("https://elite.finviz.com/screener.ashx?f=ta_sma50_pa&auth=secret", "ta_sma50_pa")]
    [InlineData("f=sh_price_o10,ta_sma50_pa", "sh_price_o10,ta_sma50_pa")]
    public void Import_SavesOnlyFilters(string input,string expected) => Assert.Equal(expected,DeskSources.ImportFilter(input));
    [Theory]
    [InlineData("https://attacker.example/screener?f=ta_sma50_pa")]
    [InlineData("https://elite.finviz.com/screener.ashx?auth=secret")]
    [InlineData("ta_sma50_pa&auth=secret")]
    [InlineData("")]
    public void Import_RejectsNonFilters(string input) => Assert.Throws<ArgumentException>(()=>DeskSources.ImportFilter(input));
    [Fact]
    public void QuoteFreshness_IsPerSymbol_AndRejectsFutureClock()
    {
        Assert.Equal("current",DeskReadService.Freshness(Now.AddSeconds(-5),Now));
        Assert.Equal("delayed",DeskReadService.Freshness(Now.AddSeconds(-45),Now));
        Assert.Equal("stale",DeskReadService.Freshness(Now.AddMinutes(-5),Now));
        Assert.Equal("unavailable",DeskReadService.Freshness(null,Now));
        Assert.Equal("clock mismatch",DeskReadService.Freshness(Now.AddMinutes(1),Now));
    }
    [Fact]
    public async Task RelevantNews_IsFilteredBeforeLimit_AndArticleDuplicatesKeepBothSymbols()
    {
        await using var db=new CandidateWorkflowTestDatabase();await db.InitializeAsync();var repository=new SqliteNewsFeedRepository(db);
        var events=Enumerable.Range(0,220).Select(i=>new CatalystEvent("OTHER",Now.AddMinutes(-1),CatalystType.NewsReport,"Unrelated "+i,0,Provider:"fixture",Url:"https://example.org/"+i)).ToList();
        events.Add(new("AAPL",Now.AddHours(-1),CatalystType.NewsReport,"Relevant",0,Provider:"fixture",Url:"https://example.org/relevant"));
        events.Add(new("MSFT",Now.AddHours(-1),CatalystType.NewsReport,"Relevant",0,Provider:"fixture",Url:"https://example.org/relevant"));
        await repository.UpsertAsync(events,default);
        var rows=await repository.GetRecentForTickersAsync(Now.AddHours(-72),40,["AAPL","MSFT"],default);
        var article=Assert.Single(DeskReadService.AggregateNews(rows));Assert.Equal(["AAPL","MSFT"],article.Symbols);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerHoldings_GetOnlyBoundAccount_IncludesUntrackedPositions_AndCaches(bool wrongAccount)
    {
        var clock=new CandidateWorkflowTestClock(Now);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Alpaca:KeyId","fixture"},{"Alpaca:SecretKey","fixture"},{"Alpaca:ExpectedAccountId",wrongAccount?"another":"bound"}}).Build();
        using var handler=new BrokerHandler();var clients=new Mock<IHttpClientFactory>();clients.Setup(x=>x.CreateClient(It.IsAny<string>())).Returns(()=>new HttpClient(handler,false));
        var archiveRoot=Path.Combine(Path.GetTempPath(),"desk-archive-"+Guid.NewGuid().ToString("N"));
        try {
            using var reader=new DeskAccountReader(new(config),config,clients.Object,new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot)),clock,NullLogger<DeskAccountReader>.Instance);
            var result=await reader.ReadAsync(default);
            if(wrongAccount){Assert.False(result.Available);Assert.Empty(result.Holdings);Assert.Single(handler.Requests);return;}
            Assert.True(result.Available);var holding=Assert.Single(result.Holdings);Assert.Equal("UNTRACKED",holding.Symbol);Assert.Equal(2.5m,holding.Quantity);Assert.Equal(-5m,holding.UnrealizedPl);
            await reader.ReadAsync(default);Assert.Equal(2,handler.Requests.Count);Assert.All(handler.Requests,x=>Assert.StartsWith("GET https://paper-api.alpaca.markets/v2/",x));
            clock.Now=Now.AddSeconds(20);handler.Fail=true;var unavailable=await reader.ReadAsync(default);Assert.False(unavailable.Available);Assert.Empty(unavailable.Holdings);
        } finally { if(Directory.Exists(archiveRoot))Directory.Delete(archiveRoot,true); }
    }
    private static Wishlist List(string name,params string[] symbols)=>new(){Id=Guid.NewGuid(),Name=name,Items=symbols.Select(x=>new WishlistItem{Ticker=x,Active=true}).ToList()};
    private sealed class BrokerHandler:HttpMessageHandler
    {
        public List<string> Requests{get;}=[];public bool Fail{get;set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Add(request.Method+" "+request.RequestUri);Assert.Equal(HttpMethod.Get,request.Method);
            return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath=="/v2/account"?"{\"id\":\"bound\"}":"""[{"symbol":"UNTRACKED","qty":"2.5","avg_entry_price":"100","cost_basis":"250","market_value":"245","unrealized_pl":"-5"}]""")});
        }
    }
}
