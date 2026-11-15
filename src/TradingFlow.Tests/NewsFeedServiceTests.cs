using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public class NewsFeedServiceTests
{
    // The caller can see the failed source even when a second source returns rows.
    [Fact]
    public async Task ProviderFailure_DoesNotAbortOtherFeeds_ButShutdownCancellationEscapes()
    {
        var service = new NewsFeedService(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<NewsFeedService>.Instance);
        var failure = await service.ReadSourceAsync("failed", () => throw new HttpRequestException("offline"), "covered", CancellationToken.None);
        Assert.Empty(failure.Events);
        Assert.Equal("failed", failure.Reading.State);
        var called = false;
        var healthy = await service.ReadSourceAsync("healthy", () => {
            called = true;
            return Task.FromResult<IReadOnlyList<TradingFlow.Domain.Market.CatalystEvent>>([
                new("AA", DateTimeOffset.UtcNow, TradingFlow.Domain.Market.CatalystType.NewsReport, "Healthy article", 0, "alpaca")]);
        }, "covered", CancellationToken.None);
        Assert.True(called);
        Assert.Equal("Healthy article", Assert.Single(healthy.Events).Headline);
        Assert.Equal("partial", NewsFeedService.ClassifyRefresh([failure.Reading, healthy.Reading], false));
        Assert.Equal("failed", NewsFeedService.ClassifyRefresh([failure.Reading], false));
        Assert.Equal("partial", NewsFeedService.ClassifyRefresh([healthy.Reading], true));
        using var shutdown = new CancellationTokenSource(); shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadSourceAsync("canceled",
            () => throw new OperationCanceledException(shutdown.Token), "covered", shutdown.Token));
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
