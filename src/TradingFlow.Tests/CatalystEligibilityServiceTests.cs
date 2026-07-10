using TradingFlow.Domain.Market;
using TradingFlow.Engine.Catalysts;

namespace TradingFlow.Tests;

public sealed class CatalystEligibilityServiceTests
{
    private static readonly DateTimeOffset Published = new(2026, 6, 8, 20, 5, 0, TimeSpan.Zero);

    [Fact]
    public void KeyFor_SameStoryFromDifferentProviders_DedupesToOneKey()
    {
        var a = Catalyst("NVDA", externalId: "story-123", headline: "NVDA beats");
        var b = Catalyst("NVDA", externalId: "story-123", headline: "a different headline, same id");

        Assert.Equal(CatalystEligibilityService.KeyFor(a), CatalystEligibilityService.KeyFor(b));
    }

    [Fact]
    public void KeyFor_NoExternalId_UsesNormalizedHeadlineAndTicker()
    {
        var a = Catalyst("NVDA", headline: "NVDA  Beats   Earnings");
        var b = Catalyst("nvda", headline: "nvda beats earnings");
        var other = Catalyst("AMD", headline: "nvda beats earnings");

        Assert.Equal(CatalystEligibilityService.KeyFor(a), CatalystEligibilityService.KeyFor(b));
        Assert.NotEqual(CatalystEligibilityService.KeyFor(a), CatalystEligibilityService.KeyFor(other));
    }

    [Fact]
    public void Eligibility_UsesReceivedTimeWhenKnown_ElseFallsBackToPublishedWithFlag()
    {
        var received = Published.AddMinutes(3);

        var withReceived = CatalystEligibilityService.Eligibility(Catalyst("NVDA", receivedAt: received));
        var withoutReceived = CatalystEligibilityService.Eligibility(Catalyst("NVDA", receivedAt: null));

        Assert.Equal(received, withReceived.EligibleAt);
        Assert.False(withReceived.UsedPublishedFallback);
        Assert.Equal(Published, withoutReceived.EligibleAt);
        Assert.True(withoutReceived.UsedPublishedFallback);
    }

    [Fact]
    public void ResolveWindow_StartsOnFirstCandleStrictlyAfterEligibility_NotTheFormingBar()
    {
        // Received mid-bar at 13:32; the 13:30 bar is already forming (excluded); first confirmable is 13:45.
        var received = new DateTimeOffset(2026, 6, 9, 13, 32, 0, TimeSpan.Zero);
        var candles = new[] { T(13, 30), T(13, 45), T(14, 0), T(14, 15), T(14, 30), T(14, 45) };
        var service = new CatalystEligibilityService(confirmationWindowBars: 3);

        var window = service.ResolveWindow(Catalyst("NVDA", receivedAt: received), candles);

        Assert.NotNull(window);
        Assert.Equal(T(13, 45), window!.FirstConfirmable);
        Assert.Equal(1, window.FirstIndex);           // the 13:30 forming bar (index 0) is excluded
        Assert.Equal(3, window.LastIndexInclusive);   // 3-candle window: indices 1, 2, 3
        Assert.True(service.IsWithinWindow(window, 1));
        Assert.True(service.IsWithinWindow(window, 3));
        Assert.False(service.IsWithinWindow(window, 0)); // the still-forming bar (lookahead) is out
        Assert.False(service.IsWithinWindow(window, 4)); // past the bounded window
    }

    [Fact]
    public void ResolveWindow_WhenNoCandleAfterEligibility_ReturnsNull()
    {
        var received = new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);
        var candles = new[] { T(13, 30), T(13, 45), T(14, 0) };
        var service = new CatalystEligibilityService();

        Assert.Null(service.ResolveWindow(Catalyst("NVDA", receivedAt: received), candles));
    }

    [Fact]
    public void TryBeginAttempt_AllowsOneAttemptPerStory_ThenConsumesDuplicates()
    {
        var service = new CatalystEligibilityService();
        var first = Catalyst("NVDA", externalId: "story-1");
        var duplicate = Catalyst("NVDA", externalId: "story-1", headline: "same story, later copy");

        Assert.True(service.TryBeginAttempt(first));       // the single allowed attempt
        Assert.False(service.TryBeginAttempt(first));      // already consumed
        Assert.False(service.TryBeginAttempt(duplicate));  // duplicate story is consumed too
        Assert.True(service.IsConsumed(first));
    }

    private static DateTimeOffset T(int hour, int minute) => new(2026, 6, 9, hour, minute, 0, TimeSpan.Zero);

    private static CatalystEvent Catalyst(
        string ticker,
        string? externalId = null,
        string headline = "headline",
        DateTimeOffset? receivedAt = null)
    {
        return new CatalystEvent(
            ticker,
            Published,
            CatalystType.NewsReport,
            headline,
            0.6m,
            Provider: "alpaca",
            ExternalId: externalId,
            ReceivedAt: receivedAt);
    }
}
