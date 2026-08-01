using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public class NewsFeedServiceTests
{
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
