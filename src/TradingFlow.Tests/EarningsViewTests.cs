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
            MaxEpsSurprise = -10, MinRevenueSurprise = 5, MinRvol = 2, MinSentiment = 0.5m,
            Breakout = "PossibleBreakout", Assessment = "miss", HasNews = true };
        var result = await service.FilterAsync(Response, query, user, default);
        Assert.Equal("AAA", Assert.Single(result.Items).Ticker);
        Assert.Equal(1, result.TotalMatching);
        result = await service.FilterAsync(Response, new() { MinEpsSurprise = 0 }, user, default);
        Assert.Equal("BBB", Assert.Single(result.Items).Ticker);
        result = await service.FilterAsync(Response, new() { Sort = "rvol" }, user, default);
        Assert.Equal(new[] { "AAA", "BBB", "UNKNOWN" }, result.Items.Select(x => x.Ticker));
        Assert.Null(result.Items[^1].SlotRelativeVolume);
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

    [Theory]
    [InlineData(3, 2)]
    [InlineData(-1, 2)]
    public async Task InvalidVolumeBoundsRejectBeforeRendering(decimal minimum, decimal maximum)
    {
        var service = new EarningsViewService(Mock.Of<IWishlistRepository>(), Mock.Of<IDeskAccountReader>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FilterAsync(Response,
            new() { MinRvol = minimum, MaxRvol = maximum }, new(), default));
    }
}
