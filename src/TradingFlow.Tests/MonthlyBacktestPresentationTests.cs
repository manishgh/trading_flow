using TradingFlow.Web.Models;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
namespace TradingFlow.Tests;
public sealed class MonthlyBacktestPresentationTests
{
    [Fact]
    public void ExitReceiptOnFebruaryFirstBelongsToJanuarySourceSession_AndNextMonthDenominatorCarries()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs/strategy-catalog.json"),reader);
        var runtime=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(Path.Combine(root,"configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml"),StrategySelectionMode.Backtest);
        var t=DateTimeOffset.Parse("2026-02-01T05:00:00Z");
        BacktestTrade Trade(DateTimeOffset exit,decimal pnl)=>new("X","RSI","long",exit.AddDays(-2),exit,10,10,11,9,14,"technical_exit",pnl,0,pnl);
        var strategy=new StrategyBacktestResult(runtime.Definition.StrategyId,"RSI","test",1000,990,-10,-1,0,2,1,2,2,0,1,1,[Trade(t,100),Trade(t.AddDays(1),-110)]);
        var rows=MonthlyBacktestPresentation.FromCompletedTrades(strategy,runtime,"run",DateTimeOffset.Parse("2026-01-07T05:00:00Z"),DateTimeOffset.Parse("2026-02-03T05:00:00Z"));
        Assert.Equal(10,rows[0].Month.ReturnPct);Assert.Equal(-10,rows[1].Month.ReturnPct);
        Assert.Equal("realized_closed_trade_equity",rows[0].Month.ValuationBasis);Assert.Equal(1,rows[0].ClosedTrades);
    }
    [Fact]
    public void TrimmedTradeSummaryCannotBeDisplayedAsZeroReturn()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs/strategy-catalog.json"), reader);
        var runtime = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(
            Path.Combine(root, "configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml"), StrategySelectionMode.Backtest);
        var summary = new StrategyBacktestResult(runtime.Definition.StrategyId, "RSI", "test", 1000, 1100,
            100, 10, 0, 2, 0, 1, 1, 0, 1, 0, []);
        var error = Assert.Throws<InvalidOperationException>(() => MonthlyBacktestPresentation.FromCompletedTrades(
            summary, runtime, "run", DateTimeOffset.Parse("2026-01-07T05:00:00Z"), DateTimeOffset.Parse("2026-02-03T05:00:00Z")));
        Assert.Contains("trimmed UI summary", error.Message);
    }

}
