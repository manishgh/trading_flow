using TradingFlow.Backtesting;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Configuration;
namespace TradingFlow.Tests;
public sealed class PrecommittedTerminalTests
{
    [Fact]
    public void FinalSessionIsFrozenFromCalendar_NotLastObservedStockBar()
    {
        var root=TestRepository.FindRoot();var strategy=new SimpleYamlReader().ReadStrategy(Path.Combine(root,"configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml"));
        var start=DateTimeOffset.Parse("2026-10-05T04:00:00Z");var end=DateTimeOffset.Parse("2026-10-07T04:00:00Z");
        var cal=new Dictionary<DateOnly,MarketSessionSchedule>{{new(2026,10,5),MarketSessionSchedule.RegularDay(new(2026,10,5))},
            {new(2026,10,6),MarketSessionSchedule.RegularDay(new(2026,10,6))},{new(2026,10,7),MarketSessionSchedule.RegularDay(new(2026,10,7))}};
        Assert.Equal(start.AddDays(1),BacktestDecisionTiming.ValidateTerminalOpen(start.AddDays(1),start,end,"1d",strategy.Session,cal));
        Assert.Throws<InvalidOperationException>(()=>BacktestDecisionTiming.ValidateTerminalOpen(start,start,end,"1d",strategy.Session,cal));
        cal.Remove(new(2026,10,6));Assert.Throws<InvalidOperationException>(()=>BacktestDecisionTiming.ValidateTerminalOpen(start.AddDays(1),start,end,"1d",strategy.Session,cal));
    }
}
