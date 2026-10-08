using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
namespace TradingFlow.Web.Models;
public sealed record MonthlyBacktestRow(string AliasId,string Name,StrategyMonthPerformance Month,int ClosedTrades);
public static class MonthlyBacktestPresentation
{
    public static DateOnly SourceSessionDate(DateTimeOffset completedDailyReceipt) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(completedDailyReceipt.AddDays(-1),
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);

    public static IReadOnlyList<MonthlyBacktestRow> FromCompletedTrades(StrategyBacktestResult strategy,
        AuthorizedRuntimeStrategy runtime,string runName,DateTimeOffset start,DateTimeOffset end)
    {
        if(!strategy.EconomicResultsComplete||runtime.Definition.Execution.Timeframe!="1d"|| runtime.Definition.Session.IsContinuousMarket ||
            runtime.Definition.Session.ExchangeTimezone != "America/New_York" ||
            runtime.AdmissionProfile.ExecutionTimingPolicy is not ("completed-bar-next-slice-market-exits-v3" or "completed-bar-precommitted-terminal-open-v4" or "completed-bar-precommitted-terminal-owned-v5"))
            throw new InvalidOperationException("Verified native-daily completed-slice metadata is required for monthly display.");
        if(strategy.CompletedTrades.Count != strategy.AcceptedTradeCount ||
            Math.Abs(strategy.StartingCapital + strategy.CompletedTrades.Sum(trade => trade.NetProfit) - strategy.EndingCapital) > .01m)
            throw new InvalidOperationException("Complete, reconciled strategy trades are required; a trimmed UI summary cannot provide monthly returns.");
        var zone=TimeZoneInfo.FindSystemTimeZoneById(runtime.Definition.Session.ExchangeTimezone);
        DateOnly SourceDate(DateTimeOffset receipt)=>SourceSessionDate(receipt);
        var first=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start,zone).DateTime);
        var last=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1),zone).DateTime);
        var byDate=strategy.CompletedTrades.GroupBy(trade=>SourceDate(trade.ExitTimestamp)).ToDictionary(group=>group.Key,group=>group.Sum(trade=>trade.NetProfit));
        if(byDate.Keys.Any(date=>date<first||date>last))throw new InvalidOperationException("Completed trades exceed declared monthly period.");
        var dates=Enumerable.Range(0,last.DayNumber-first.DayNumber+1).Select(n=>first.AddDays(n)).ToArray();
        var equity=strategy.StartingCapital;var points=new List<StrategySessionEquity>();
        // Calendar-day ACCOUNTING observations, not invented market candles. Only realised exits change this basis.
        foreach(var date in dates){equity+=byDate.GetValueOrDefault(date);points.Add(new(date,equity));}
        var rows=MonthlyStrategyPerformance.Calculate(runtime.Identity.StrategyId,runtime.Identity.SemanticVersion,runtime.Identity.ContentSha256,
            runName,"independent_strategy","realized_closed_trade_equity",strategy.StartingCapital,first,last,points,dates);
        return rows.Select(row=>new MonthlyBacktestRow(strategy.StrategyId,strategy.StrategyName,row,
            strategy.CompletedTrades.Count(trade=>SourceDate(trade.ExitTimestamp)>=row.PeriodStart&&SourceDate(trade.ExitTimestamp)<=row.PeriodEnd))).ToArray();
    }
}
