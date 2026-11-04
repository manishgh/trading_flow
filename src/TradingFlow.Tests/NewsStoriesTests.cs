using TradingFlow.Domain.News;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class NewsStoriesTests
{
    private static PersistedNewsItem Item(string provider, string ticker, string headline, int minute) => new()
    {
        Id = provider + ticker + minute,
        Ticker = ticker,
        Headline = headline,
        Provider = provider,
        Timestamp = new DateTimeOffset(2026, 11, 3, 21, minute, 0, TimeSpan.Zero)
    };

    [Fact]
    public void SameStoryMergesAcrossStocksAndProvidersWithTheNewestCopyLeadingByDefault()
    {
        var records = new[]
        {
            Item("alpaca", "OUST", "Ouster Reports Q3 Results", 31),
            Item("finviz", "OUST", "Ouster reports Q3 results.", 34),
            Item("finviz", "LIDR", "Ouster reports Q3 results.", 34),
            Item("finviz", "AAPL", "Apple earnings recap", 10)
        };

        var stories = NewsStories.Combine(records);

        Assert.Equal(2, stories.Count);
        Assert.Equal(["LIDR", "OUST"], stories[0].Tickers);
        Assert.Equal("finviz", stories[0].Lead.Provider);
        Assert.Equal(3, stories[0].Records.Count);
    }

    [Fact]
    public void AScreenThatAsksForAlpacaGetsItsCopyEvenWhenFinvizIsNewer()
    {
        var records = new[]
        {
            Item("alpaca", "OUST", "Ouster Reports Q3 Results", 31),
            Item("finviz", "OUST", "Ouster reports Q3 results.", 34)
        };

        var lead = Assert.Single(NewsStories.Combine(records, NewsStories.RealTimeProvider)).Lead;

        Assert.Equal("alpaca", lead.Provider);
        Assert.Equal(31, lead.Timestamp.Minute);
    }
}
