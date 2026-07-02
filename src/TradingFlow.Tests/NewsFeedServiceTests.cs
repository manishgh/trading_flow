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
}
