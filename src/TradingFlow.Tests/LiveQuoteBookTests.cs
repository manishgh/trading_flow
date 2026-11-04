using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class LiveQuoteBookTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Parser_ReadsAStreamedQuoteAndIgnoresIncompleteOrOtherFrames()
    {
        Assert.True(AlpacaMarketDataMessageParser.TryParseQuote(
            Parse("""{"T":"q","S":"oust","bp":31.03,"bs":2,"ap":31.15,"as":3,"t":"2026-11-04T14:30:01.5Z"}"""), out var quote));
        Assert.Equal(new AlpacaStreamQuote("OUST", 31.03m, 31.15m, 2m, 3m, DateTimeOffset.Parse("2026-11-04T14:30:01.5Z")), quote);

        Assert.False(AlpacaMarketDataMessageParser.TryParseQuote(Parse("""{"T":"q","S":"OUST","bp":31.03,"t":"2026-11-04T14:30:01Z"}"""), out _));
        // An empty side of the book (0) is no price rather than a price of zero.
        Assert.True(AlpacaMarketDataMessageParser.TryParseQuote(
            Parse("""{"T":"q","S":"OUST","bp":0,"bs":0,"ap":31.15,"as":3,"t":"2026-11-04T14:30:01Z"}"""), out var oneSided));
        Assert.Null(oneSided!.BidPrice);
        Assert.Equal(31.15m, oneSided.AskPrice);
        Assert.False(AlpacaMarketDataMessageParser.TryParseQuote(Parse("""{"T":"t","S":"OUST","p":31.1,"s":5,"t":"2026-11-04T14:30:01Z"}"""), out _));
    }

    [Fact]
    public void Book_KeepsTheNewestQuoteAndPrefersItOverAnOlderPolledQuote()
    {
        var book = new LiveQuoteBook();
        var at = DateTimeOffset.Parse("2026-11-04T14:30:00Z");
        book.Update(new AlpacaStreamQuote("OUST", 31.00m, 31.10m, 1m, 1m, at.AddSeconds(2)));
        book.Update(new AlpacaStreamQuote("OUST", 30.00m, 30.10m, 1m, 1m, at)); // a late, older frame

        Assert.Equal(31.10m, book.Find("oust")!.AskPrice);
        var polled = new AlpacaLatestQuote("OUST", 31.20m, 31.30m, 1m, 1m, at.AddSeconds(1));
        Assert.Equal(31.10m, book.Newest("OUST", polled)!.AskPrice);
        Assert.Equal(31.30m, book.Newest("OUST", polled with { Timestamp = at.AddSeconds(5) })!.AskPrice);
        Assert.Same(polled, book.Newest("MSFT", polled));

        // An unsubscribed symbol or a lost connection removes the streamed price.
        book.Remove("OUST");
        Assert.Null(book.Find("OUST"));
        book.Update(new AlpacaStreamQuote("CVS", 90m, 90.1m, 1m, 1m, at));
        book.Clear();
        Assert.Null(book.Find("CVS"));
    }
}
