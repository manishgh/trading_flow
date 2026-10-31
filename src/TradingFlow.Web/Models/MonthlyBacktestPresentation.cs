using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Contracts.V1;
using System.Globalization;
namespace TradingFlow.Web.Models;
public sealed record MonthlyBacktestRow(string AliasId,string Name,StrategyMonthPerformance Month,int ClosedTrades,
    decimal? OpeningEquity = null, decimal? ClosingEquity = null);
public static class MonthlyBacktestPresentation
{
    // Actual intraday execution belongs to its New York date; native-daily receipts retain their previous-session convention.
    public static DateOnly SourceSessionDate(DateTimeOffset timestamp, string? valuationBasis) =>
        valuationBasis == IntradayPortfolioExecution.ValuationBasis
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime)
            : NativeDailySessionLedger.SourceSessionDate(timestamp);

    // Publish the engine's reconciled source-session book; monthly costs/profit are changes in cumulative observations.
    public static IReadOnlyList<MonthlyRunResult> FromPortfolioState(StrategyBacktestResult strategy,
        AuthorizedRuntimeStrategy runtime, string runName, string cohortName, DateTimeOffset start, DateTimeOffset end)
    {
        var state = strategy.PortfolioState ?? throw new InvalidOperationException("The strategy has no portfolio session ledger.");
        var expectedBasis = StrategyHorizonResolver.Resolve(runtime.Definition) != "day"
            ? NativeDailySessionLedger.ValuationBasis : IntradayPortfolioExecution.ValuationBasis;
        if (state.ValuationBasis != expectedBasis || state.CutoffUtc != end || state.StartUtc != start)
            throw new InvalidDataException("Portfolio valuation basis or cutoff differs from the requested monthly period.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
        var sessions = state.Sessions.OrderBy(session => session.SessionDate).ToArray();
        if (sessions.Length == 0 || sessions.Select(session => session.SessionDate).Distinct().Count() != sessions.Length ||
            sessions.Any(session => session.SessionDate < first || session.SessionDate > last))
            throw new InvalidDataException("Portfolio monthly reporting requires unique session observations inside the frozen period.");
        var identity = new StrategyReference(runtime.Identity.StrategyId, runtime.Identity.SemanticVersion, runtime.Identity.ContentSha256);
        var rows = new List<MonthlyRunResult>();
        var opening = strategy.StartingCapital;
        var previousRealized = 0m;
        var previousFees = 0m;
        string? unavailable = null;
        foreach (var group in sessions.GroupBy(session => new DateOnly(session.SessionDate.Year, session.SessionDate.Month, 1)))
        {
            var month = group.Key;
            var periodStart = month < first ? first : month;
            var calendarEnd = month.AddMonths(1).AddDays(-1);
            var periodEnd = calendarEnd > last ? last : calendarEnd;
            var observations = group.ToArray();
            var close = observations[^1];
            if (state.UnavailableFromSession is { } boundary && boundary <= periodEnd)
                unavailable ??= state.UnavailableReason ?? $"Portfolio evidence is incomplete from {boundary:yyyy-MM-dd}.";
            var missing = observations.FirstOrDefault(session => session.Equity is null || session.UnavailableReason is not null);
            if (missing is not null)
                unavailable ??= missing.UnavailableReason ?? $"Equity is unavailable on {missing.SessionDate:yyyy-MM-dd}.";
            StrategyMonthPerformance? performance = null;
            if (unavailable is null)
            {
                performance = MonthlyStrategyPerformance.Calculate(identity.StrategyId, identity.SemanticVersion, identity.ContentSha256,
                    runName, "independent_strategy_cohort", state.ValuationBasis, opening, periodStart, periodEnd,
                    observations.Select(session => new StrategySessionEquity(session.SessionDate, session.Equity!.Value)).ToArray(),
                    observations.Select(session => session.SessionDate).ToArray()).Single();
            }
            var fills = state.Fills.Where(fill => fill.SourceSessionDate >= periodStart && fill.SourceSessionDate <= periodEnd).ToArray();
            rows.Add(new(identity, cohortName, month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                unavailable is null ? opening : null, unavailable is null ? close.Equity : null, performance?.ReturnPct,
                strategy.CompletedTrades.Count(trade => SourceSessionDate(trade.ExitTimestamp, state.ValuationBasis) >= periodStart && SourceSessionDate(trade.ExitTimestamp, state.ValuationBasis) <= periodEnd),
                state.ValuationBasis, periodStart != month || periodEnd != calendarEnd, unavailable, performance?.CloseDrawdownPct,
                strategy.StrategyId, strategy.StrategyName, close.RealizedNetProfit - previousRealized,
                unavailable is null ? close.UnrealizedProfit : null, close.Fees - previousFees, close.Cash,
                unavailable is null ? close.PositionValue : null, unavailable is null ? close.GrossExposurePct : null,
                close.OpenPositionCount, fills.Where(fill => fill.IsEntry).Select(fill => fill.CandidateId).Distinct(StringComparer.Ordinal).Count(), fills.Length));
            if (unavailable is null) opening = close.Equity!.Value;
            previousRealized = close.RealizedNetProfit;
            previousFees = close.Fees;
        }
        var expectedMonths = (last.Year - first.Year) * 12 + last.Month - first.Month + 1;
        if (rows.Count != expectedMonths)
            throw new InvalidDataException("A requested month has no authoritative portfolio session observation.");
        return rows;
    }

    // Calculate native-daily realized monthly performance from all reconciled exits before any UI trimming.
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
        // Attribute old realized records using the same native daily availability-to-session policy.
        DateOnly SourceDate(DateTimeOffset receipt)=>NativeDailySessionLedger.SourceSessionDate(receipt);
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
        var equityByDate = points.ToDictionary(point => point.SessionDate, point => point.Equity);
        return rows.Select(row=>new MonthlyBacktestRow(strategy.StrategyId,strategy.StrategyName,row,
            strategy.CompletedTrades.Count(trade=>SourceDate(trade.ExitTimestamp)>=row.PeriodStart&&SourceDate(trade.ExitTimestamp)<=row.PeriodEnd),
            row.PeriodStart == first ? strategy.StartingCapital : equityByDate[row.PeriodStart.AddDays(-1)],
            equityByDate[row.PeriodEnd])).ToArray();
    }
}
