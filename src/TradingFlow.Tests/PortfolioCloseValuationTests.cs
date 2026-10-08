using TradingFlow.Backtesting;
namespace TradingFlow.Tests;
public sealed class PortfolioCloseValuationTests
{
    [Fact]
    public void OpenLossAndFeesAppearBeforeTradeCloses_AndFutureMarksDoNotAlterPriorEquity()
    {
        var t=DateTimeOffset.Parse("2026-04-08T04:00:00Z");
        PortfolioValuationFill[] fills=[new("ABC",t,true,10,100,1),new("ABC",t.AddDays(2),false,10,110,1)];
        PortfolioCloseMark[] marks=[new("ABC",t,100),new("ABC",t.AddDays(1),80),new("ABC",t.AddDays(2),110)];
        var result=PortfolioCloseValuation.Analyze(10000,fills,marks);
        Assert.Equal(9799,result.Observations[1].Equity);Assert.Equal(10098,result.EndingEquity);
        Assert.Equal(2.01m,result.MaxCloseDrawdownPct);
        var changed=PortfolioCloseValuation.Analyze(10000,fills,[..marks[..2],marks[2] with {Price=99999}]);
        Assert.Equal(result.Observations[1],changed.Observations[1]);
    }
    [Fact]
    public void MissingValuationFailsInsteadOfUsingFutureOrStalePrice()
    {
        var t=DateTimeOffset.Parse("2026-04-08T04:00:00Z");
        Assert.Throws<InvalidOperationException>(()=>PortfolioCloseValuation.Analyze(10000,
            [new("ABC",t,true,10,100,1)],[new("ABC",t.AddDays(1),80)]));
    }
    [Fact]
    public void ExpectedGridDetectsAnEntireMissingValuationDate()
    {
        var t=DateTimeOffset.Parse("2026-04-08T04:00:00Z");
        PortfolioValuationFill[] fills=[new("ABC",t,true,10,100,1),new("ABC",t.AddDays(2),false,10,110,1)];
        PortfolioCloseMark[] marks=[new("ABC",t,100),new("ABC",t.AddDays(2),110)];
        Assert.Throws<InvalidOperationException>(()=>PortfolioCloseValuation.Analyze(10000,fills,marks,[t,t.AddDays(1),t.AddDays(2)]));
    }
    [Fact]
    public void DuplicateMethodsMayRepeatSameClose_ButConflictingMarksFail()
    {
        var t=DateTimeOffset.Parse("2026-04-08T04:00:00Z");
        Assert.Equal(10000,PortfolioCloseValuation.Analyze(10000,[],[new("ABC",t,100),new("abc",t,100)]).EndingEquity);
        Assert.Throws<InvalidOperationException>(()=>PortfolioCloseValuation.Analyze(10000,[],[new("ABC",t,100),new("abc",t,101)]));
    }
}
