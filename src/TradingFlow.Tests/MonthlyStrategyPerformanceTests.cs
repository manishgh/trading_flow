using TradingFlow.Backtesting;
namespace TradingFlow.Tests;
public sealed class MonthlyStrategyPerformanceTests
{
    [Fact]
    public void MonthOpeningCarriesPriorClose_AndPartialPeriodsAreExplicit()
    {
        var apr=new DateOnly(2026,4,30);var may=new DateOnly(2026,5,1);
        var rows=MonthlyStrategyPerformance.Calculate("id","1.0.0",new string('0',64),"run","independent_strategy","daily_close",100,
            new(2026,4,7),new(2026,5,1),[new(apr,110),new(may,99)],[apr,may]);
        Assert.Equal(10m,rows[0].ReturnPct);Assert.Equal(-10m,rows[1].ReturnPct);
        Assert.True(rows[0].PartialPeriod);Assert.True(rows[1].PartialPeriod);Assert.Equal("id",rows[1].StrategyId);
    }
    [Fact]
    public void MissingMonthEndObservationIsUnavailableRatherThanZero()
    {
        var day=new DateOnly(2026,4,30);
        Assert.Throws<InvalidOperationException>(()=>MonthlyStrategyPerformance.Calculate("id","1",new string('0',64),"run","one","close",100,
            day,day,[],[day]));
    }
    [Fact]
    public void ZeroTradesCanBeFlatOnlyWithCompleteEquityEvidence()
    {
        var day=new DateOnly(2026,4,30);
        var row=Assert.Single(MonthlyStrategyPerformance.Calculate("zero","1",new string('0',64),"run","one","close",100,
            day,day,[new(day,100)],[day]));Assert.Equal(0m,row.ReturnPct);Assert.Equal(0m,row.CloseDrawdownPct);
    }
}
