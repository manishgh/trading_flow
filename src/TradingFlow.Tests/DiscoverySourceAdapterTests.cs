using Moq;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.News;
using TradingFlow.Web.Services.Discovery;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class DiscoverySourceAdapterTests
{
    [Fact]
    public async Task NewsSource_RefreshesOnlyAllowedTickerAndPreservesArticleEvidence()
    {
        var now = Utc(2026, 8, 27, 14, 0);
        var clock = new FixedTimeProvider(now);
        var repository = new Mock<INewsFeedRepository>();
        repository.Setup(value => value.GetIngestedSinceForTickersAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PersistedNewsItem
                {
                    Id = "alpaca-42",
                    Ticker = "POET",
                    Provider = "alpaca",
                    Timestamp = now.AddSeconds(-30),
                    IngestedAt = now.AddSeconds(-20),
                    Url = "https://example.test/news/42",
                    Headline = "POET wins contract"
                }
            ]);
        var source = new NewsDiscoverySource(
            ["POET"],
            "intraday",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            repository.Object,
            clock);

        var capture = await source.CaptureAsync(default);

        var observation = Assert.Single(capture.Symbols);
        Assert.Equal("POET", observation.Symbol);
        Assert.Contains("alpaca-42", observation.MetadataJson, StringComparison.Ordinal);
        Assert.Equal(now.AddSeconds(-30), capture.ProviderTimestampUtc);
        Assert.Equal("news:alpaca-42", capture.RawReference);
    }

    [Fact]
    public async Task AlertSource_ExpiresOldPulseAndPreservesLatestAlertIdentity()
    {
        var clock = new MutableTimeProvider(Utc(2026, 8, 27, 14, 0));
        var pulses = new StockPulseReceiverService(clock);
        pulses.RegisterPulse("RGTI", "breakout", "RGTI accelerating");
        var source = new AlertDiscoverySource(
            ["RGTI"],
            "intraday",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            pulses,
            clock);

        var current = await source.CaptureAsync(default);
        Assert.Contains("breakout", Assert.Single(current.Symbols).MetadataJson, StringComparison.Ordinal);
        Assert.Equal(Utc(2026, 8, 27, 14, 0), current.ObservedAtUtc);

        clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<DiscoveryNoObservationException>(() => source.CaptureAsync(default));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
