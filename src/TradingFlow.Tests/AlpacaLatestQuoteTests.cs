using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public class AlpacaLatestQuoteTests
{
    [Fact]
    public void DisplayPrice_UsesBidAskMidpoint()
    {
        var quote = new AlpacaLatestQuote("RGTI", 23.20m, 23.26m, 100, 200, DateTimeOffset.UtcNow);

        Assert.Equal(23.23m, quote.MidPrice);
        Assert.Equal("23.23", quote.DisplayPrice);
        Assert.Equal("Buy 23.26", quote.BuyCaption);
        Assert.Equal("Sell 23.20", quote.SellCaption);
    }

    [Fact]
    public void Format_KeepsSubDollarPricesReadable()
    {
        Assert.Equal("0.1234", AlpacaLatestQuote.Format(0.1234m));
        Assert.Equal("--", AlpacaLatestQuote.Format(null));
    }
}
