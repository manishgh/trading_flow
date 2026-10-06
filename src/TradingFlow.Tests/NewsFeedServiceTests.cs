using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public class NewsFeedServiceTests
{
    [Fact]
    public async Task ProviderFailure_DoesNotAbortOtherFeeds_ButShutdownCancellationEscapes()
    {
        var service = new NewsFeedService(null!, null!, null!, null!, null!, null!, null!, null!, null!,
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<NewsFeedService>.Instance);
        Assert.Empty(await service.LoadProviderAsync("failed", () => throw new HttpRequestException("offline"), CancellationToken.None));
        var called = false;
        var healthy = await service.LoadProviderAsync("healthy", () => {
            called = true;
            return Task.FromResult<IReadOnlyList<TradingFlow.Domain.Market.CatalystEvent>>([
                new("AA", DateTimeOffset.UtcNow, TradingFlow.Domain.Market.CatalystType.NewsReport, "Healthy article", 0, "alpaca")]);
        }, CancellationToken.None);
        Assert.True(called);
        Assert.Equal("Healthy article", Assert.Single(healthy).Headline);
        using var shutdown = new CancellationTokenSource(); shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadProviderAsync("canceled",
            () => throw new OperationCanceledException(shutdown.Token), shutdown.Token));
    }

    [Fact]
    public void NewsTargets_ReadWishlistSymbolsAndRotateBeyondTheBatchLimit()
    {
        var symbols = Enumerable.Range(0, 450).Select(x => "T" + x).ToArray();
        var first = NewsFeedService.SelectNewsTargets(symbols.Concat([" t0 "]), ["EVENT"], 0);
        var second = NewsFeedService.SelectNewsTargets(symbols, ["EVENT"], first.NextOffset);
        var third = NewsFeedService.SelectNewsTargets(symbols, ["EVENT"], second.NextOffset);
        Assert.Equal(200, first.Symbols.Count);
        Assert.Equal(200, second.Symbols.Count);
        Assert.Equal(451, first.Symbols.Concat(second.Symbols).Concat(third.Symbols).Distinct().Count());
        Assert.Contains("EVENT", third.Symbols);
        Assert.Equal("T0", first.Symbols[0]);
    }

    [Fact]
    public void FormatRelatedTickers_DedupesAndCombinesArticleTickers()
    {
        var display = NewsFeedService.FormatRelatedTickers(new[] { "MU", "INFQ", "mu", " MSFT ", "MARKET" });

        Assert.Equal("INFQ, MSFT, MU, MARKET", display);
    }

    [Fact]
    public void HeadlineIdentity_CollapsesProviderPunctuationDifferences()
    {
        var first = NewsFeedService.NormalizeHeadlineIdentity("Fed holds rates: Powell speaks");
        var second = NewsFeedService.NormalizeHeadlineIdentity("FED holds rates - Powell speaks!");

        Assert.Equal(first, second);
    }
}
