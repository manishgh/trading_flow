using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

/// <summary>
/// The desk's Change and Change % read against the last completed session close
/// before today in New York, from Alpaca's snapshot daily bars. These fix the
/// selection across sessions and the response shapes it accepts.
/// </summary>
public sealed class AlpacaPreviousCloseTests
{
    private static readonly DateOnly Today = new(2026, 9, 29);

    [Fact]
    public void RegularSessionAndAfterHours_UseYesterdaysBarNotTodays()
    {
        const string body = """
            {
              "MU": {
                "dailyBar": { "t": "2026-09-29T04:00:00Z", "o": 101, "h": 112, "l": 100, "c": 110, "v": 1000 },
                "prevDailyBar": { "t": "2026-09-28T04:00:00Z", "o": 98, "h": 101, "l": 97, "c": 100, "v": 900 }
              }
            }
            """;

        var close = AlpacaQuoteService.ParsePreviousCloses(body, Today)["MU"];

        Assert.Equal(100m, close.Close);
        Assert.Equal(new DateOnly(2026, 9, 28), close.SessionDate);
    }

    [Fact]
    public void BeforeTheOpen_TheLatestDailyBarIsThePreviousSession()
    {
        const string body = """
            {
              "MU": {
                "dailyBar": { "t": "2026-09-28T04:00:00Z", "c": 100 },
                "prevDailyBar": { "t": "2026-09-25T04:00:00Z", "c": 95 }
              }
            }
            """;

        var close = AlpacaQuoteService.ParsePreviousCloses(body, Today)["MU"];

        Assert.Equal(100m, close.Close);
        Assert.Equal(new DateOnly(2026, 9, 28), close.SessionDate);
    }

    [Fact]
    public void MissingOrNullBars_LeaveTheSymbolAbsent_AndTheWrapperShapeIsRead()
    {
        const string body = """
            {
              "snapshots": {
                "AAA": { "latestTrade": null, "dailyBar": null, "prevDailyBar": null },
                "BBB": { "dailyBar": { "t": "2026-09-29T04:00:00Z", "c": 10 } },
                "CCC": { "prevDailyBar": { "t": "2026-09-26T04:00:00Z", "c": 0 } },
                "DDD": { "prevDailyBar": { "t": "2026-09-26T04:00:00Z", "c": 42.5 } }
              }
            }
            """;

        var closes = AlpacaQuoteService.ParsePreviousCloses(body, Today);

        Assert.Equal(["DDD"], closes.Keys);
        Assert.Equal(42.5m, closes["DDD"].Close);
    }

    [Fact]
    public void NewYorkDate_FollowsTheExchangeClockNotUtc()
    {
        Assert.Equal(new DateOnly(2026, 9, 28), AlpacaQuoteService.NewYorkDate(DateTimeOffset.Parse("2026-09-29T03:30:00Z")));
        Assert.Equal(new DateOnly(2026, 9, 29), AlpacaQuoteService.NewYorkDate(DateTimeOffset.Parse("2026-09-29T04:30:00Z")));
    }

    [Fact]
    public void DeskRow_ChangeIsMidAgainstThePreviousCloseOrUnknown()
    {
        var item = new WishlistItem { Ticker = "MU", Active = true };
        var quote = new AlpacaLatestQuote("MU", 109.9m, 110.1m, 100m, 100m, DateTimeOffset.Parse("2026-09-29T15:00:00Z"));

        var known = new WishlistDeskRow(item, quote, null, null, null)
        {
            PreviousClose = new AlpacaPreviousClose("MU", 100m, new DateOnly(2026, 9, 28))
        };
        Assert.Equal(10m, known.Change);
        Assert.Equal(10m, known.ChangePct);

        var unknown = new WishlistDeskRow(item, quote, null, null, null);
        Assert.Null(unknown.Change);
        Assert.Null(unknown.ChangePct);
    }
}
