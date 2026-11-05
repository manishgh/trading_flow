using System.Security.Claims;
using System.Text.Json;
using Moq;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class EarningsViewTests
{
    private static readonly EarningsCalendarResponse Response = JsonSerializer.Deserialize<EarningsCalendarResponse>("""
    { "items": [
      { "id":"unknown", "ticker":"UNKNOWN", "provider":"calendar", "scheduledAtUtc":"2026-11-01T00:30:00Z" },
      { "id":"qualified", "ticker":"AAA", "provider":"calendar", "newsProvider":"news", "scheduledAtUtc":"2026-11-01T07:30:00Z",
        "epsSurprisePercent":-12, "revenueSurprisePercent":8, "slotRelativeVolume":2.2, "newsSentiment":0.6,
        "marketCapMillions":500, "breakoutAssessment":"PossibleBreakout", "dayGroup":"today", "epsOutcome":"miss", "epsActual":1.5,
        "newsHeadline":"Company result" },
      { "id":"excluded", "ticker":"BBB", "provider":"other", "newsProvider":"news", "scheduledAtUtc":"2026-11-01T08:30:00Z",
        "epsSurprisePercent":20, "revenueSurprisePercent":4, "slotRelativeVolume":0.8, "newsSentiment":-0.5, "epsActual":2 }
    ] }
    """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task ComposedFiltersPreserveUnknownEvidenceAndProviderMeaning()
    {
        var service = new EarningsViewService(Mock.Of<IWishlistRepository>(), Mock.Of<IDeskAccountReader>());
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var query = new EarningsViewQuery { Provider = "calendar", NewsProvider = "news", MinEpsSurprise = -15,
            MaxEpsSurprise = -10, MinRevenueSurprise = 5, MinSentiment = 0.5m,
            Breakout = "PossibleBreakout", Assessment = "miss", HasNews = true };
        var result = await service.FilterAsync(Response, query, user, default);
        Assert.Equal("AAA", Assert.Single(result.Items).Ticker);
        Assert.Equal(1, result.TotalMatching);
        result = await service.FilterAsync(Response, new() { MinEpsSurprise = 0 }, user, default);
        Assert.Equal("BBB", Assert.Single(result.Items).Ticker);
    }

    [Fact]
    public async Task SectorAndNewsEvidenceFiltersUseRowSectorsAndTheNewsPanel()
    {
        var service = new EarningsViewService(Mock.Of<IWishlistRepository>(), Mock.Of<IDeskAccountReader>());
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var response = Response with
        {
            Items = [Response.Items[0], Response.Items[1] with { Sector = "Technology" }, Response.Items[2] with { Sector = "Energy" }],
            News = [new(DateTimeOffset.Parse("2026-11-01T09:00:00Z"), "BBB, ZZZ", "Company", 0.2m, "Positive", "Beta wins contract", "", "news", "wire", "https://example.invalid", "")]
        };

        Assert.Equal("BBB", Assert.Single((await service.FilterAsync(response, new() { Sector = "energy" }, user, default)).Items).Ticker);
        Assert.Equal("UNKNOWN", Assert.Single((await service.FilterAsync(response, new() { Sector = EarningsViewService.UnknownSector }, user, default)).Items).Ticker);
        // AAA has a result headline; BBB has no headline but appears in the news panel; UNKNOWN has neither.
        Assert.Equal(new[] { "AAA", "BBB" }, (await service.FilterAsync(response, new() { HasNews = true }, user, default)).Items.Select(x => x.Ticker));
    }

    [Fact]
    public async Task PrivateFiltersRejectAnonymousRequestsAndIntersectMemberships()
    {
        var groups = new Mock<IWishlistRepository>(); var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        var service = new EarningsViewService(groups.Object, account.Object);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.FilterAsync(Response, new() { OnlyHeld = true }, new(), default));
        var identity = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"operator")], "fixture"));
        groups.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new() { Items = [new() { Ticker = "AAA" }, new() { Ticker = "BBB", Active = false }] }]);
        var result = await service.FilterAsync(Response, new() { OnlyGroups = true }, identity, default);
        Assert.Equal("AAA", Assert.Single(result.Items).Ticker);
    }

    [Fact]
    public async Task NewRecordFiltersComposeWithoutTreatingMissingAmountsAsZero()
    {
        var service=new EarningsViewService(Mock.Of<IWishlistRepository>(),Mock.Of<IDeskAccountReader>());
        var row=Response.Items[1] with {CompanyName="Alpha Devices",IsScheduleEstimate=true,EpsEstimate=-1,EpsActual=-.5m,
            RevenueEstimateMillions=100,RevenueActualMillions=120,MarketCapMillions=500,EventReturnPercent=-3,ProviderOneDayPriceReactionPercent=-2};
        var response=Response with {Items=[Response.Items[0],row]};
        var query=new EarningsViewQuery {Search="devices",Schedule="estimated",Result="observed",MinEpsEstimate=-2,MaxEpsEstimate=0,
            MinEpsActual=-1,MaxEpsActual=0,MinRevenueEstimate=90,MinRevenueActual=110,MinMarketCap=400,MaxMarketCap=600,
            MinEventReturn=-4,MaxEventReturn=-2,MinPriceReaction=-3,MaxPriceReaction=-1};
        Assert.Equal("AAA",Assert.Single((await service.FilterAsync(response,query,new(),default)).Items).Ticker);
        var pending=await service.FilterAsync(response,new(){Result="pending"},new(),default);
        Assert.Equal("UNKNOWN",Assert.Single(pending.Items).Ticker);
        Assert.Empty((await service.FilterAsync(response,new(){MinEventReturn=0},new(),default)).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.FilterAsync(response,new(){MinEventReturn=2,MaxEventReturn=1},new(),default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.FilterAsync(response,new(){Schedule="observed"},new(),default));
    }

    [Theory]
    [InlineData(0.5, 0.2)]
    [InlineData(-2, 0.2)]
    public async Task InvalidSentimentBoundsRejectBeforeRendering(decimal minimum, decimal maximum)
    {
        var service = new EarningsViewService(Mock.Of<IWishlistRepository>(), Mock.Of<IDeskAccountReader>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FilterAsync(Response,
            new() { MinSentiment = minimum, MaxSentiment = maximum }, new(), default));
    }
}
