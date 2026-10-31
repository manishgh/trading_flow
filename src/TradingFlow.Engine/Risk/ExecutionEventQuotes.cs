namespace TradingFlow.Engine.Risk;

/// <summary>The same quote transition is used by idle observation and active matching.</summary>
public static class ExecutionEventQuotes
{
    /// <summary>Returns one valid distinct quote; conflicting or ineligible same-time updates clear the quote.</summary>
    public static ExecutionQuoteTick? UniqueEligible(IReadOnlyList<ExecutionQuoteTick> quotes)
    {
        if (quotes.Count == 0) return null;
        var first = quotes[0];
        for (var i = 1; i < quotes.Count; i++) if (quotes[i] != first) return null;
        return first is { IsEligible: true, Bid: > 0, Ask: > 0, BidSizeShares: > 0, AskSizeShares: > 0 }
            && first.Ask >= first.Bid ? first : null;
    }
}
