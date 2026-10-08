namespace TradingFlow.Backtesting;

public sealed record StrategySessionEquity(DateOnly SessionDate, decimal Equity);
public sealed record StrategyMonthPerformance(string StrategyId, string Version, string ContentSha256,
    string RunName, string CapitalScope, string ValuationBasis, string Month, DateOnly PeriodStart,
    DateOnly PeriodEnd, bool PartialPeriod, decimal? ReturnPct, decimal CloseDrawdownPct);

/// <summary>Noncumulative, source-session monthly returns. Each caller supplies one verified capital pool.</summary>
public static class MonthlyStrategyPerformance
{
    public static IReadOnlyList<StrategyMonthPerformance> Calculate(string strategyId, string version,
        string contentSha256, string runName, string capitalScope, string valuationBasis, decimal startingEquity,
        DateOnly first, DateOnly last, IReadOnlyList<StrategySessionEquity> observations,
        IReadOnlyList<DateOnly> expectedSessions)
    {
        if (startingEquity <= 0 || last < first || String.IsNullOrWhiteSpace(strategyId) ||
            String.IsNullOrWhiteSpace(version) || String.IsNullOrWhiteSpace(runName) || contentSha256.Length != 64 ||
            contentSha256.Any(character => !Uri.IsHexDigit(character)) || String.IsNullOrWhiteSpace(capitalScope) || String.IsNullOrWhiteSpace(valuationBasis))
            throw new ArgumentException("Monthly reports require identity, capital/basis, positive initial equity and a valid period.");
        var points=observations.GroupBy(point=>point.SessionDate).ToDictionary(group=>group.Key,group=>group.Select(point=>point.Equity).Distinct().Single());
        if(expectedSessions.Any(date=>date<first||date>last)||points.Keys.Any(date=>date<first||date>last))
            throw new ArgumentException("Monthly observations must stay inside the declared source-session window.");
        if(expectedSessions.Distinct().Count()!=expectedSessions.Count || expectedSessions.Count==0 ||
            expectedSessions.Any(date=>!points.ContainsKey(date)))
            throw new InvalidOperationException("Missing or duplicate expected monthly valuation session; no return may be inferred.");
        var result=new List<StrategyMonthPerformance>();var opening=startingEquity;
        for(var month=new DateOnly(first.Year,first.Month,1);month<=last;month=month.AddMonths(1))
        {
            var calendarEnd=month.AddMonths(1).AddDays(-1);var start=month<first?first:month;var end=calendarEnd>last?last:calendarEnd;
            var values=expectedSessions.Where(date=>date>=start&&date<=end).Order().Select(date=>points[date]).ToArray();
            if(values.Length==0)throw new InvalidOperationException("Month has no verified valuation sessions.");
            var close=values[^1];var peak=opening;var dd=0m;
            foreach(var equity in values){peak=Math.Max(peak,equity);if(peak>0)dd=Math.Max(dd,(peak-equity)/peak*100m);}
            result.Add(new(strategyId,version,contentSha256,runName,capitalScope,valuationBasis,month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                start,end,start!=month||end!=calendarEnd,opening>0?(close/opening-1m)*100m:null,dd));
            opening=close;
        }
        return result;
    }
}
