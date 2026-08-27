using Moq;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

public sealed class PaperRunUniverseSnapshotResolverTests
{
    [Fact]
    public async Task ResolveAsync_ScreenerOnly_FreezesNormalizedSymbolsAndProvenance()
    {
        var repoRoot = TestRepository.FindRoot();
        var resolvedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screener
            .Setup(source => source.PreviewAsync(
                "large movers",
                ScreenerScope.Intraday,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenerSyncResult(
                ScreenerScope.Intraday,
                "large movers",
                "f=cap_large,sh_relvol_o2",
                [" nvda ", "MU", "NVDA"],
                ["NVDA", "MU"],
                resolvedAt,
                "2026-08-27",
                null));
        var resolver = CreateResolver(screener.Object);

        var snapshot = await resolver.ResolveAsync(
            [],
            includeSelectedTickers: false,
            selectedTickerSource: "wishlist",
            screenerInput: "large movers",
            includeScreener: true,
            wishlistId: null,
            strategyPath: Path.Combine(
                repoRoot,
                "configs",
                "strategies",
                "intraday-ema10-ema20-macd-volume.v1.yaml"),
            CancellationToken.None);

        Assert.Equal(["MU", "NVDA"], snapshot.Tickers);
        Assert.Empty(snapshot.SelectedSourceTickers);
        Assert.Equal(["MU", "NVDA"], snapshot.ScreenerTickers);
        Assert.Equal("finviz", snapshot.Source);
        Assert.Equal("f=cap_large,sh_relvol_o2", snapshot.ScreenerQuery);
        Assert.Equal(resolvedAt, snapshot.ResolvedAtUtc);
        screener.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_WishlistAndSwingScreen_DeduplicatesTheFrozenUnion()
    {
        var repoRoot = TestRepository.FindRoot();
        var wishlistId = Guid.NewGuid();
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screener
            .Setup(source => source.PreviewAsync(
                "swing leaders",
                ScreenerScope.Swing,
                wishlistId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenerSyncResult(
                ScreenerScope.Swing,
                "swing leaders",
                "f=cap_large,ta_sma20_pa",
                ["MSFT", "MU"],
                ["MSFT"],
                DateTimeOffset.UtcNow,
                null,
                null));
        var resolver = CreateResolver(screener.Object);

        var snapshot = await resolver.ResolveAsync(
            ["mu", "NVDA"],
            includeSelectedTickers: true,
            selectedTickerSource: "wishlist",
            screenerInput: "swing leaders",
            includeScreener: true,
            wishlistId,
            strategyPath: Path.Combine(
                repoRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml"),
            CancellationToken.None);

        Assert.Equal(["MSFT", "MU", "NVDA"], snapshot.Tickers);
        Assert.Equal(["MU", "NVDA"], snapshot.SelectedSourceTickers);
        Assert.Equal(["MSFT", "MU"], snapshot.ScreenerTickers);
        Assert.Equal("wishlist+finviz", snapshot.Source);
        screener.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_FailedScreen_RejectsBeforeRunArtifactCreation()
    {
        var repoRoot = TestRepository.FindRoot();
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        screener
            .Setup(source => source.PreviewAsync(
                "bad screen",
                ScreenerScope.Intraday,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScreenerSyncResult.Failed(
                ScreenerScope.Intraday,
                "bad screen",
                String.Empty,
                "Finviz read failed: timeout"));
        var resolver = CreateResolver(screener.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            [],
            includeSelectedTickers: false,
            selectedTickerSource: "ephemeral",
            screenerInput: "bad screen",
            includeScreener: true,
            wishlistId: null,
            strategyPath: Path.Combine(
                repoRoot,
                "configs",
                "strategies",
                "intraday-ema10-ema20-macd-volume.v1.yaml"),
            CancellationToken.None));

        Assert.Equal("Finviz read failed: timeout", exception.Message);
        screener.VerifyAll();
    }

    private static PaperRunUniverseSnapshotResolver CreateResolver(IScreenerSnapshotSource screener) =>
        new(screener, new SimpleYamlReader(), TimeProvider.System);
}
