using TradingFlow.Web.Models;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
namespace TradingFlow.Tests;
public sealed class MonthlyBacktestPresentationTests
{
    /// <summary>An actual first-of-month day exit belongs to that month in both monthly totals and the shared drilldown date.</summary>
    [Fact]
    public void DayExitOnOctoberFirst_RemainsInOctoberAndCarriesPriorMonthEquity()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs/strategy-catalog.json"), reader);
        var runtime = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(
            Path.Combine(root, "configs/backtest/strategies/previous-day-breakout.bt-v1.yaml"), StrategySelectionMode.Backtest);
        var start = DateTimeOffset.Parse("2026-09-01T04:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-01T04:00:00Z");
        var exit = DateTimeOffset.Parse("2026-10-01T19:55:00Z");
        var trade = new BacktestTrade("ABC", runtime.Definition.StrategyName, "long", exit.AddHours(-1), exit,
            1, 100, 110, 90, 130, "session_deadline", 10, 0, 10, StrategyId: runtime.Definition.StrategyId, CandidateId: "day-close");
        var state = new BacktestPortfolioState(TradingFlow.Backtesting.IntradayPortfolioExecution.ValuationBasis, end,
            1, 1, 1, [], [], [], [], [
                new(new(2026, 9, 30), 1000, 0, 0, 0, 0, 1000, 0, 0, null),
                new(new(2026, 10, 30), 1010, 10, 0, 0, 0, 1010, 0, 0, null)
            ]) { StartUtc = start };
        var strategy = new StrategyBacktestResult(runtime.Definition.StrategyId, runtime.Definition.StrategyName, "test",
            1000, 1010, 10, 1, 0, 2, 0, 1, 1, 0, 1, 0, [trade]) { PortfolioState = state };
        var rows = MonthlyBacktestPresentation.FromPortfolioState(strategy, runtime, "run", "cohort", start, end);
        Assert.Equal(0, rows[0].ClosedTrades);
        Assert.Equal(1, rows[1].ClosedTrades);
        Assert.Equal(1000, rows[1].OpeningEquity);
        Assert.Equal(1, rows[1].ReturnPct);
        Assert.Equal(new DateOnly(2026, 10, 1), MonthlyBacktestPresentation.SourceSessionDate(exit, state.ValuationBasis));
        Assert.Equal(new DateOnly(2026, 9, 30), MonthlyBacktestPresentation.SourceSessionDate(exit,
            TradingFlow.Backtesting.NativeDailySessionLedger.ValuationBasis));
    }

    [Fact]
    public void SupportedEarlierMonth_RetainsItsClosedTradeCountWhenLaterMarksAreUnavailable()
    {
        var root=TestRepository.FindRoot();var reader=new SimpleYamlReader();
        var catalog=new StrategyArtifactCatalog(root,Path.Combine(root,"configs/strategy-catalog.json"),reader);
        var runtime=new StrategyRunArtifactValidator(reader,catalog).ReadAndValidateRuntimeArtifact(
            Path.Combine(root,"configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v6.yaml"),StrategySelectionMode.Backtest);
        var start=DateTimeOffset.Parse("2026-06-01T04:00:00Z");var end=DateTimeOffset.Parse("2026-08-01T04:00:00Z");
        var exit=DateTimeOffset.Parse("2026-07-01T04:00:00Z");
        var trade=new BacktestTrade("ABC",runtime.Definition.StrategyName,"long",exit.AddDays(-2),exit,1,100,110,90,120,
            "technical_exit",10,0,10,StrategyId:runtime.Definition.StrategyId,CandidateId:"closed");
        var state=new TradingFlow.Domain.Backtesting.BacktestPortfolioState(TradingFlow.Backtesting.NativeDailySessionLedger.ValuationBasis,
            end,2,2,1,[],[],[],[],
            [new(new(2026,6,30),1010,10,0,0,0,1010,0,0,null),
             new(new(2026,7,31),910,10,0,null,null,null,null,1,"Missing aligned July close.")])
            {StartUtc=start,UnavailableFromSession=new(2026,7,31),UnavailableReason="Missing aligned July close."};
        var strategy=new StrategyBacktestResult(runtime.Definition.StrategyId,runtime.Definition.StrategyName,"test",
            1000,1000,0,0,0,2,0,2,2,0,1,0,[trade]){PortfolioState=state};
        var suppressed=TradingFlow.Backtesting.BacktestRunner.SuppressStrategyEconomicClaims(strategy);
        var rows=MonthlyBacktestPresentation.FromPortfolioState(suppressed,runtime,"run","cohort",start,end);
        Assert.Equal(1,rows[0].ClosedTrades);Assert.Equal(1,rows[0].ReturnPct);Assert.Equal(1010,rows[0].ClosingEquity);
        Assert.Equal(0,rows[1].ClosedTrades);Assert.Null(rows[1].ReturnPct);Assert.Equal("Missing aligned July close.",rows[1].UnavailableReason);
    }

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
