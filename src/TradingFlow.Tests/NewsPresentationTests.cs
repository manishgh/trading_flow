using TradingFlow.Web.Models;
namespace TradingFlow.Tests;
public sealed class NewsPresentationTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("/relative")]
    public void UnsafeUrlsNeverBecomeLinks(string url)
    {
        var presentation=NewsPresentation.From(new(" aaa, AAA, AA ",DateTimeOffset.UtcNow,"Headline",.2m,"wire",null,url,"Summary"));
        Assert.Null(presentation.SafeUrl); Assert.Equal("AA, AAA",presentation.Tickers); Assert.Equal("Positive",presentation.SentimentLabel);
        Assert.Equal("Company",presentation.Classification);
    }
    [Fact]
    public void MarketSentinelIsDisplayedButNeverAddedAsAnEquity()
    {
        var presentation=NewsPresentation.From(new("MARKET, AAA",DateTimeOffset.UtcNow,"Fed report",0,"federal_reserve",null,null,null));
        Assert.Equal("AAA, MARKET",presentation.Tickers);Assert.Equal("AAA",presentation.GroupableTickers);
        Assert.Empty(NewsPresentation.From(new("MARKET",DateTimeOffset.UtcNow,"Fed report",0,"federal_reserve",null,null,null)).GroupableTickers);
    }
}
