using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class NativeEconomicClaimSuppressionTests
{
    [Fact]
    public void NativeAdmissionAudit_DoesNotRelabelAnAdmittedPartialExitFailureAsRejection()
    {
        var time=DateTimeOffset.Parse("2026-06-01T04:00:00Z");
        var strategy=new SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(),"configs","backtest","strategies",
            "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        var first=new BacktestCandidateTrade("ABC",strategy.StrategyName,"long",time,100,90,120,time.AddDays(1),110,"time_exit",10,
            SelectionScore:2,StrategyId:strategy.StrategyId,CandidateId:"one");
        var second=first with {Ticker="DEF",CandidateId="two",SelectionScore=1};
        var bars=new[]{new OhlcvBar("ABC",time,"1d",100,100,100,100,1000),
            new OhlcvBar("ABC",time.AddDays(1),"1d",110,110,110,110,400)};
        var calendar=new Dictionary<DateOnly,MarketSessionSchedule>{[new(2026,6,1)]=MarketSessionSchedule.RegularDay(new(2026,6,1)),
            [new(2026,6,2)]=MarketSessionSchedule.RegularDay(new(2026,6,2))};
        var accounting=new NativeDailyAccountingContext(time,time.AddDays(2),calendar,false);
        var method=typeof(BacktestRunner).GetMethod("BuildUnifiedPortfolioTradesWithAudit",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var outcome=(PortfolioBuildOutcome)method.Invoke(null,
            [new PortfolioConfig(10000,100,100,1,0,0,1,true,MaxBarParticipationPct:10),new[]{strategy},new[]{first,second},
                null,null,bars,null,accounting])!;
        Assert.Single(outcome.ExecutionFailures);
        Assert.Single(outcome.AdmissionAudit.RejectionCounts);
        Assert.True(outcome.AdmissionAudit.RejectionCounts.ContainsKey("portfolio_max_concurrent_positions_reached"));
        Assert.Equal(1,outcome.State!.AdmittedOrderCount);
    }

    [Fact]
    public void FailedAdmittedExecution_IsNotSubtractedAgainFromRejectedCandidateCount()
    {
        var time=DateTimeOffset.Parse("2026-07-01T04:00:00Z");
        var state=new BacktestPortfolioState(NativeDailySessionLedger.ValuationBasis,time,1,1,0,[],[],[],[],
            [new(new(2026,6,30),10000,0,0,0,0,10000,0,0,null)]);
        var failure=new BacktestExecutionFailure("one","ABC","Strategy","strategy","long",time,
            "required_execution_missing",1,1,0,1,100,0,0,100,time);
        var outcome=new PortfolioBuildOutcome([],new PortfolioAdmissionAudit(),[failure]){State=state};
        var first=new BacktestCandidateTrade("ABC","Strategy","long",time,100,90,120,null,null,"open_at_study_end",10,
            StrategyId:"strategy",CandidateId:"one");
        var second=first with {Ticker="DEF",CandidateId="two"};
        var method=typeof(BacktestRunner).GetMethod("BuildUnifiedPortfolioResult",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var result=(UnifiedPortfolioBacktestResult)method.Invoke(null,
            [new PortfolioConfig(10000,1,25,1,0,0,1,true),Array.Empty<StrategyDefinition>(),new[]{first,second},
                Array.Empty<OhlcvBar>(),null,null,null,outcome])!;
        Assert.Equal(1,result.AcceptedTradeCount);Assert.Equal(1,result.RejectedTradeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteNativeEconomics_PreserveActualCompletedFacts_ForSupportedEarlierMonths(bool native)
    {
        var time=DateTimeOffset.Parse("2026-07-01T04:00:00Z");
        var trade=new BacktestTrade("ABC","Strategy","long",time.AddDays(-2),time,1,100,110,90,120,"technical_exit",10,1,9);
        var state=native?new BacktestPortfolioState(NativeDailySessionLedger.ValuationBasis,time.AddMonths(1),1,1,1,[],[],[],[],[]):null;
        var strategy=new StrategyBacktestResult("strategy","Strategy","test",10000,10009,9,.09m,0,20,0,1,1,0,1,0,[trade])
            {PortfolioState=state};
        var unified=new UnifiedPortfolioBacktestResult(10000,10009,9,.09m,0,1,1,0,1,0,[trade]){PortfolioState=state};
        var suppressedStrategy=BacktestRunner.SuppressStrategyEconomicClaims(strategy);
        var suppressedUnified=BacktestRunner.SuppressUnifiedPortfolioEconomicClaims(unified);
        Assert.Equal(native?1:0,suppressedStrategy.CompletedTrades.Count);
        Assert.Equal(native?1:0,suppressedUnified.CompletedTrades.Count);
        Assert.False(suppressedStrategy.EconomicResultsComplete);Assert.False(suppressedUnified.EconomicResultsComplete);
        Assert.Equal(10000,suppressedStrategy.EndingCapital);Assert.Equal(0,suppressedStrategy.NetProfit);
    }
}
