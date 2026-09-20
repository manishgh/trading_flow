using Moq;
using System.Text.Json;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.News;
using TradingFlow.Web.Services.Discovery;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class DiscoverySourceAdapterTests
{
    [Fact]
    public async Task FinvizSource_PreservesVendorRvolAndProviderProvenanceAsDiscoveryMetadata()
    {
        var syncedAt = Utc(2026, 8, 27, 14, 0);
        var providerTimestamp = syncedAt.AddSeconds(-2);
        var repository = new Mock<IDiscoveryRepository>(MockBehavior.Strict);
        repository
            .Setup(value => value.GetSourceVersionAsync(
                It.IsAny<Guid>(),
                DiscoverySourceKinds.Finviz,
                "v=111&f=sh_relvol_o2",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screener
            .Setup(source => source.PreviewAsync(
                "v=111&f=sh_relvol_o2",
                ScreenerScope.Swing,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenerSyncResult(
                ScreenerScope.Swing,
                "relative-volume screen",
                "v=111&f=sh_relvol_o2",
                ["RGTI", "POET"],
                ["RGTI", "POET"],
                syncedAt,
                "2026-08-27",
                null)
            {
                SymbolDetails =
                [
                    new ScreenerSymbolResult("RGTI", 2.75m),
                    new ScreenerSymbolResult("POET", null)
                ],
                ProviderTimestampUtc = providerTimestamp,
                RawReference = "finviz:v=111&f=sh_relvol_o2|raw-archive:finviz-snapshot-42"
            });
        var source = new FinvizDiscoverySource(
            Guid.NewGuid(),
            "v=111&f=sh_relvol_o2",
            ["STALE"],
            "swing",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            null,
            repository.Object,
            screener.Object,
            new FixedTimeProvider(syncedAt));

        var capture = await source.CaptureAsync(default);

        Assert.Equal(providerTimestamp, capture.ProviderTimestampUtc);
        Assert.Equal(
            "finviz:v=111&f=sh_relvol_o2|raw-archive:finviz-snapshot-42",
            capture.RawReference);
        var rgti = Assert.Single(capture.Symbols, item => item.Symbol == "RGTI");
        using var rgtiMetadata = JsonDocument.Parse(rgti.MetadataJson);
        Assert.Equal(2.75m, rgtiMetadata.RootElement.GetProperty("vendor_reported_rvol").GetDecimal());
        var poet = Assert.Single(capture.Symbols, item => item.Symbol == "POET");
        using var poetMetadata = JsonDocument.Parse(poet.MetadataJson);
        Assert.Equal(JsonValueKind.Null, poetMetadata.RootElement.GetProperty("vendor_reported_rvol").ValueKind);
        repository.VerifyAll();
        screener.VerifyAll();
    }

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
            "swing",
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
            "swing",
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
